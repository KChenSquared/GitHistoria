namespace Historia.Views;

public sealed partial class ConflictResolverView
{
    private void OnResultDocumentTextChanged(object? sender, EventArgs e)
    {
        if (_suppressTextChanged || _currentFile == null)
        {
            return;
        }

        // Rows first: everything below reads the session's spans for this text.
        bool reinstalled;
        _inResultTextChanged = true;
        try
        {
            reinstalled = _session.Normalize();
        }
        finally
        {
            _inResultTextChanged = false;
        }

        RegisterUnsavedWork();

        if (_session.IsMutating)
        {
            return; // the session realigns the panes once its edit is done
        }

        if (reinstalled)
        {
            // An undo/redo of a toggle can move rows: realign now rather than after the debounce.
            _debounceCts?.Cancel();
            UpdateConflictParseSync();
            UpdateEditorsFromParse();
            return;
        }

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _debounceCts = new CancellationTokenSource();
        var ct = _debounceCts.Token;

        var docSnapshot = _resultEditor.Document.CreateSnapshot();
        var versionSnapshot = _resultEditor.Document.Version;
        int markerSize = _workingCopy?.MarkerSize ?? 7;
        var spans = _session.Spans;
        var managedAnalyses = ManagedAnalyses();

        Task.Delay(150, ct).ContinueWith(_ =>
        {
            if (ct.IsCancellationRequested || _disposed)
            {
                return;
            }

            var text = docSnapshot.Text;
            var parse = ConflictMarkerParser.Parse(text, markerSize);
            var oursLines = ConflictPaneBuilder.Build(parse, ConflictSide.Ours, spans);
            var theirsLines = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs, spans);
            var analyses = ConflictRegionAnalyzer.AnalyzeAll(parse).Concat(managedAnalyses).ToList();
            var snapshot = new ConflictParseSnapshot(parse, oursLines, theirsLines, analyses, versionSnapshot);

            Dispatcher.UIThread.Post(() =>
            {
                if (ct.IsCancellationRequested || _disposed)
                {
                    return;
                }

                if (_resultEditor.Document.Version.CompareAge(versionSnapshot) == 0)
                {
                    InstallSnapshot(snapshot);
                    _parse = parse;
                    _parseVersion = versionSnapshot;

                    ConflictPaneUpdateHelper.UpdateDocumentIncremental(_oursEditor.Document, oursLines);
                    ConflictPaneUpdateHelper.UpdateDocumentIncremental(_theirsEditor.Document, theirsLines);

                    _session.SetMarkerRegions(parse.Regions);
                    _minimap.Parse = parse;
                    _minimap.ResolvedRanges = ManagedRanges();
                    UpdateConflictsCount();
                    _oursEditor.TextArea.TextView.Redraw();
                    _theirsEditor.TextArea.TextView.Redraw();
                    _resultEditor.TextArea.TextView.Redraw();
                }
            });
        }, TaskScheduler.Default);
    }

    private void UpdateConflictParseSync()
    {
        int markerSize = _workingCopy?.MarkerSize ?? 7;
        var text = _resultEditor.Text;
        _parse = ConflictMarkerParser.Parse(text, markerSize);
        var parse = _parse;
        var spans = _session.Spans;
        var oursLines = ConflictPaneBuilder.Build(parse, ConflictSide.Ours, spans);
        var theirsLines = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs, spans);
        var version = _resultEditor.Document.Version;
        _parseVersion = version;
        _session.SetMarkerRegions(parse.Regions);
        var managedAnalyses = ManagedAnalyses();

        // No-blink carryover: retain analyses for regions whose text is unchanged
        var prevAnalyses = _snapshot?.Analyses;
        var carriedAnalyses = new List<ConflictRegionAnalysis>();
        if (prevAnalyses != null && prevAnalyses.Count > 0)
        {
            foreach (var r in parse.Regions)
            {
                var match = prevAnalyses.FirstOrDefault(a => RegionLinesEqual(r, a.Region));
                if (match != null)
                {
                    carriedAnalyses.Add(RemapAnalysis(match, r));
                }
            }
        }

        carriedAnalyses.AddRange(managedAnalyses);
        InstallSnapshot(new ConflictParseSnapshot(parse, oursLines, theirsLines, carriedAnalyses, version));
        _minimap.Parse = parse;
        _minimap.ResolvedRanges = ManagedRanges();
        UpdateConflictsCount();

        Task.Run(() =>
        {
            if (_disposed)
            {
                return;
            }

            var fullAnalyses = ConflictRegionAnalyzer.AnalyzeAll(parse).Concat(managedAnalyses).ToList();
            Dispatcher.UIThread.Post(() =>
            {
                if (_disposed)
                {
                    return;
                }

                if (_resultEditor.Document.Version.CompareAge(version) == 0)
                {
                    InstallSnapshot(new ConflictParseSnapshot(parse, oursLines, theirsLines, fullAnalyses, version));
                    _oursEditor.TextArea.TextView.Redraw();
                    _theirsEditor.TextArea.TextView.Redraw();
                    _resultEditor.TextArea.TextView.Redraw();
                }
            });
        });
    }

    private static bool RegionLinesEqual(ConflictRegion a, ConflictRegion b)
    {
        if (a.OursLines.Count != b.OursLines.Count || a.TheirsLines.Count != b.TheirsLines.Count)
            return false;
        for (int i = 0; i < a.OursLines.Count; i++)
            if (a.OursLines[i] != b.OursLines[i]) return false;
        for (int i = 0; i < a.TheirsLines.Count; i++)
            if (a.TheirsLines[i] != b.TheirsLines[i]) return false;

        if (a.HasBase != b.HasBase) return false;
        if (a.HasBase)
        {
            if (a.BaseLines == null || b.BaseLines == null || a.BaseLines.Count != b.BaseLines.Count)
                return false;
            for (int i = 0; i < a.BaseLines.Count; i++)
                if (a.BaseLines[i] != b.BaseLines[i]) return false;
        }

        return true;
    }

    private static ConflictRegionAnalysis RemapAnalysis(ConflictRegionAnalysis prev, ConflictRegion newRegion)
    {
        if (prev.Region.OursStartLine == newRegion.OursStartLine &&
            prev.Region.TheirsStartLine == newRegion.TheirsStartLine &&
            prev.Region.BaseStartLine == newRegion.BaseStartLine)
        {
            return new ConflictRegionAnalysis(newRegion, prev.OursHighlights, prev.TheirsHighlights, prev.BaseHighlights);
        }

        var oursHl = new List<RegionLineHighlight>(prev.OursHighlights.Count);
        for (int i = 0; i < prev.OursHighlights.Count; i++)
        {
            var h = prev.OursHighlights[i];
            oursHl.Add(new RegionLineHighlight(h.LineIndex, newRegion.OursStartLine + i, h.Spans, h.ChangedFromBase));
        }

        var theirsHl = new List<RegionLineHighlight>(prev.TheirsHighlights.Count);
        for (int i = 0; i < prev.TheirsHighlights.Count; i++)
        {
            var h = prev.TheirsHighlights[i];
            theirsHl.Add(new RegionLineHighlight(h.LineIndex, newRegion.TheirsStartLine + i, h.Spans, h.ChangedFromBase));
        }

        List<RegionLineHighlight>? baseHl = null;
        if (prev.BaseHighlights != null)
        {
            baseHl = new List<RegionLineHighlight>(prev.BaseHighlights.Count);
            for (int i = 0; i < prev.BaseHighlights.Count; i++)
            {
                var h = prev.BaseHighlights[i];
                baseHl.Add(new RegionLineHighlight(h.LineIndex, newRegion.BaseStartLine + i, h.Spans, h.ChangedFromBase));
            }
        }

        return new ConflictRegionAnalysis(newRegion, oursHl, theirsHl, baseHl);
    }

    private void UpdateEditorsFromParse()
    {
        if (_snapshot == null)
        {
            return;
        }

        _oursEditor.Text = string.Join('\n', _snapshot.OursLines.Select(l => l.Text));
        _theirsEditor.Text = string.Join('\n', _snapshot.TheirsLines.Select(l => l.Text));

        _oursEditor.TextArea.TextView.Redraw();
        _theirsEditor.TextArea.TextView.Redraw();
        _resultEditor.TextArea.TextView.Redraw();
    }

    private IReadOnlyList<ConflictPaneLine>? GetPaneLines(ConflictSide side)
    {
        return side == ConflictSide.Ours ? _snapshot?.OursLines : _snapshot?.TheirsLines;
    }

    /// <summary>The Result line of a side's row in a top pane (an ours, theirs or deletion row);
    /// -1 for a marker, padding or common row, which carry no hunk box.</summary>
    private int PaneDocLine(ConflictSide side, int paneRow)
    {
        var lines = GetPaneLines(side);
        if (lines == null || paneRow < 0 || paneRow >= lines.Count)
        {
            return -1;
        }

        var line = lines[paneRow];
        return line.Type is ConflictPaneLineType.Ours or ConflictPaneLineType.Theirs or ConflictPaneLineType.Deletion ? line.DocumentLineIndex : -1;
    }

    private string PaneHunkTip(ConflictSide side, int paneRow)
    {
        var lines = GetPaneLines(side);
        bool isDeletion = lines != null && paneRow >= 0 && paneRow < lines.Count && lines[paneRow].Type == ConflictPaneLineType.Deletion;
        if (side == ConflictSide.Ours)
        {
            return isDeletion ? "Use the ours deletion" : "Use these ours lines";
        }
        return isDeletion ? "Use the theirs deletion" : "Use these theirs lines";
    }

    private ConflictToggleGlyph PaneHunkGlyph(ConflictSide side, int paneRow)
        => _session.HunkGlyph(side, PaneDocLine(side, paneRow));

    private ConflictRowTint PaneTint(ConflictSide side, int row)
    {
        var lines = GetPaneLines(side);
        return lines != null && row >= 0 && row < lines.Count ? ConflictRowTints.ForPane(lines[row].Type) : ConflictRowTint.None;
    }

    private ConflictRowTint ResultTint(int row) => ConflictRowTints.ForResult(row, _session.RowVisual(row), _parse);

    /// <summary>The conflicts the arrows and the counter step through — unresolved marker regions
    /// and managed regions — by document line, in order.</summary>
    internal IReadOnlyList<(int Start, int End)> NavigableRegions()
    {
        var regions = new List<(int Start, int End)>();
        if (_parse != null)
        {
            foreach (var r in _parse.Regions)
            {
                regions.Add((r.StartLine, r.EndLine));
            }
        }
        foreach (var r in _session.Regions)
        {
            if (!r.IsBroken)
            {
                regions.Add((r.StartLine, r.EndLine));
            }
        }
        regions.Sort((a, b) => a.Start.CompareTo(b.Start));
        return regions;
    }

    /// <summary>
    /// The 0-based index of the conflict the caret is in; otherwise of the next one below the
    /// caret; the last one when the caret is below them all. -1 when there are none.
    /// </summary>
    internal int CurrentRegionIndex() => CurrentRegionIndex(NavigableRegions());

    private int CurrentRegionIndex(IReadOnlyList<(int Start, int End)> regions)
    {
        if (regions.Count == 0)
        {
            return -1;
        }

        int caretLine = _resultEditor.TextArea.Caret.Line - 1;
        for (int i = 0; i < regions.Count; i++)
        {
            if (caretLine <= regions[i].End)
            {
                return i;
            }
        }
        return regions.Count - 1;
    }

    private void UpdateConflictsCount()
    {
        var regions = NavigableRegions();
        int total = regions.Count;
        int malformed = _parse?.MalformedAt.Count ?? 0;
        int current = CurrentRegionIndex(regions) + 1;
        int unresolved = _parse?.Regions.Count ?? 0;

        _conflictsCountText.IsVisible = total > 0;
        _conflictsCountText.Text = total > 0 ? $"{current}/{total}" : string.Empty;
        ToolTip.SetTip(_conflictsCountText, total > 0 ? $"Conflict {current} of {total} in this file · {unresolved} unresolved" : null);
        bool canNavigate = !_isBusy && _isEditable && total > 0;
        _prevConflictBtn.IsEnabled = canNavigate;
        _nextConflictBtn.IsEnabled = canNavigate;

        if (malformed > 0)
        {
            var firstLine = _parse!.MalformedAt[0] + 1;
            _damagedMarkersHint.Text = $"Conflict markers damaged near line {firstLine}";
            _damagedMarkersHint.IsVisible = true;
        }
        else
        {
            _damagedMarkersHint.IsVisible = false;
        }
    }

    private void OnResultCaretPositionChanged(object? sender, EventArgs e)
    {
        if (_isEditable)
        {
            UpdateConflictsCount();
        }
    }

    private void SyncScroll(ConflictPaneEditor source, Vector offset)
    {
        if (_isSyncingScroll)
        {
            return;
        }

        _isSyncingScroll = true;
        try
        {
            if (!ReferenceEquals(source, _oursEditor))
            {
                _oursEditor.SetScrollOffset(offset);
            }
            if (!ReferenceEquals(source, _theirsEditor))
            {
                _theirsEditor.SetScrollOffset(offset);
            }
            if (!ReferenceEquals(source, _resultEditor))
            {
                _resultEditor.SetScrollOffset(offset);
            }

            if (_resultEditor.TextArea.TextView is { VisualLinesValid: true } tv && tv.VisualLines.Count > 0)
            {
                var first = tv.VisualLines[0].FirstDocumentLine.LineNumber;
                var last = tv.VisualLines[^1].LastDocumentLine.LineNumber;
                _minimap.DisplayRange = (first, last);
            }
            else
            {
                int line = _resultEditor.TextArea.Caret.Line;
                _minimap.DisplayRange = (line, line + 20);
            }
        }
        finally
        {
            _isSyncingScroll = false;
        }
    }
}
