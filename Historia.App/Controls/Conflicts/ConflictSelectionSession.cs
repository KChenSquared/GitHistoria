namespace Historia.Controls;

/// <summary>
/// Managed conflict regions over the Result document. Checking a hunk turns a marker region into
/// a managed region: its marker block is replaced by every ours row and every theirs row, each kept
/// or dropped (or an empty deletion row if a side has no lines). Dropped rows and deletion rows stay
/// in the document — dimmed, struck through or neutral, and read-only — so the three panes stay
/// line-aligned; <see cref="ComposeOutput"/> leaves them out of the saved text.
/// <para>
/// Rows are <see cref="TextSegment"/>s in one <see cref="TextSegmentCollection{T}"/>, so offsets
/// follow user edits. The view calls <see cref="Normalize"/> at the top of its
/// <c>TextChanged</c> handler; the session never subscribes to document events itself.
/// Trap: <see cref="TextSegmentCollection{T}"/> updates its offsets from its own weak
/// <c>TextDocument.Changed</c> handler, and a session handler on the same event can run before it
/// and see stale offsets. <c>TextChanged</c> fires after every <c>Changed</c> handler and, during
/// undo/redo, once after the whole group has played back.
/// </para>
/// <para>
/// Every session change is an undo step on the document's own undo stack. A change that edits text
/// is bracketed by two operations: one pushed first whose undo reinstalls the state from before,
/// one pushed last whose redo reinstalls the state from after. Trap: a single operation cannot do
/// both — a group undoes last-pushed-first and redoes first-pushed-first, so a lone operation
/// would reinstall offsets against the wrong text. Reinstalling only records the state; it is
/// applied by the next <see cref="Normalize"/>, after playback, because the document cannot change
/// during playback and offsets only match the text once the whole group has played back.
/// </para>
/// All members run on the UI thread.
/// </summary>
public sealed class ConflictSelectionSession : IReadOnlySectionProvider
{
    /// <summary>One managed conflict.</summary>
    public sealed class ManagedRegion
    {
        internal ManagedRegion(ConflictSelection selection, ConflictRegion original, string originalBlockText, string delimiter, ConflictRegionAnalysis? analysis)
        {
            Selection = selection;
            Original = original;
            OriginalBlockText = originalBlockText;
            Delimiter = delimiter;
            Analysis = analysis;
        }

        public ConflictSelection Selection { get; }

        /// <summary>The marker region the conflict was activated from.</summary>
        public ConflictRegion Original { get; }

        /// <summary>The marker block's text, with its trailing line break, as activated.</summary>
        public string OriginalBlockText { get; }

        /// <summary>The intra-line analysis of the marker region, kept for its highlights.</summary>
        public ConflictRegionAnalysis? Analysis { get; }

        internal string Delimiter { get; }
        internal List<RowSegment> Rows { get; } = [];

        /// <summary>First and last document line (0-based) of the region.</summary>
        public int StartLine { get; internal set; }
        public int EndLine { get; internal set; }

        /// <summary>A user edit broke the rows; the region is plain text until it is frozen.</summary>
        internal bool IsBroken { get; set; }

        internal int[] OursRowLines { get; set; } = [];
        internal int[] TheirsRowLines { get; set; } = [];

        /// <summary>The current document line (0-based) of a row.</summary>
        public int RowLine(ConflictSide side, int index) => side == ConflictSide.Ours ? OursRowLines[index] : TheirsRowLines[index];
    }

    internal sealed class RowSegment : TextSegment
    {
        public required ManagedRegion Region { get; init; }
        public required ConflictSide Side { get; init; }
        public required int Index { get; init; }

        /// <summary>The line's total length when the row was last attached to it.</summary>
        public int AttachedLength { get; set; }
    }

    private readonly record struct LineInfo(ManagedRegion Region, ConflictRowOrigin Origin, int Index, bool Excluded, ConflictRowVisual Visual);

    private readonly record struct RowState(int Start, int Length, ConflictSide Side, int Index, int AttachedLength);

    private sealed record RegionState(
        ConflictSelection Selection, ConflictRegion Original, string OriginalBlockText, string Delimiter,
        ConflictRegionAnalysis? Analysis, RowState[] Rows);

    private sealed record Snapshot(RegionState[] Regions);

    private readonly TextDocument _document;
    private readonly TextSegmentCollection<RowSegment> _segments;
    private readonly List<ManagedRegion> _regions = [];
    private readonly Dictionary<int, LineInfo> _lines = [];
    private readonly Dictionary<int, ConflictToggleGlyph> _oursHunkGlyphs = [];
    private readonly Dictionary<int, ConflictToggleGlyph> _theirsHunkGlyphs = [];
    private int[] _omittedLines = [];
    private IReadOnlyList<ConflictRegion> _markerRegions = [];
    private IReadOnlyList<ConflictManagedSpan> _spans = [];
    private Snapshot _lastGood = new([]);
    private Snapshot? _pending;
    private bool _mutating;
    private bool _freezePosted;

    public ConflictSelectionSession(TextDocument document)
    {
        _document = document;
        _segments = new TextSegmentCollection<RowSegment>(document);
    }

    /// <summary>Raised after every session change, undo or redo of one, and normalisation.</summary>
    public event EventHandler? Changed;

    /// <summary>The read-only sections to install on the Result <c>TextArea</c>: dropped rows, deletion rows
    /// and the line breaks around them.</summary>
    public IReadOnlySectionProvider ReadOnlySections => this;

    public IReadOnlyList<ManagedRegion> Regions => _regions;

    /// <summary>Managed regions as pane-builder spans, sorted by line. Immutable; safe to hand to a
    /// background build.</summary>
    public IReadOnlyList<ConflictManagedSpan> Spans => _spans;

    /// <summary>True while the session itself is editing the document.</summary>
    public bool IsMutating => _mutating;

    /// <summary>Managed regions that keep no line at all.</summary>
    public int EmptyRegionCount => _regions.Count(r => r.Selection.IsEmpty);

    #region Queries

    public ManagedRegion? RegionAt(int docLine) => _lines.TryGetValue(docLine, out var info) ? info.Region : null;

    public bool IsExcluded(int docLine) => _lines.TryGetValue(docLine, out var info) && info.Excluded;

    public bool IsDeletionRow(int docLine) => _lines.TryGetValue(docLine, out var info) && (info.Visual & ConflictRowVisual.Deletion) != 0;

    public ConflictRowVisual RowVisual(int docLine) => _lines.TryGetValue(docLine, out var info) ? info.Visual : ConflictRowVisual.None;

    /// <summary>The Result pane's per-line toggle: on every ours/theirs row of a managed region.</summary>
    public ConflictToggleGlyph RowGlyph(int docLine)
    {
        if (!_lines.TryGetValue(docLine, out var info) || info.Origin == ConflictRowOrigin.Added)
        {
            return ConflictToggleGlyph.None;
        }
        return info.Excluded ? ConflictToggleGlyph.Unchecked : ConflictToggleGlyph.Checked;
    }

    /// <summary>The hunk box a top pane draws: on the first line of that side's rows in each
    /// conflict, marker regions included.</summary>
    public ConflictToggleGlyph HunkGlyph(ConflictSide pane, int docLine)
    {
        var map = pane == ConflictSide.Ours ? _oursHunkGlyphs : _theirsHunkGlyphs;
        return map.TryGetValue(docLine, out var glyph) ? glyph : ConflictToggleGlyph.None;
    }

    /// <summary>The managed row on a line, if the line is an ours or theirs row.</summary>
    public bool TryGetRow(int docLine, out ManagedRegion region, out ConflictSide side, out int index)
    {
        if (_lines.TryGetValue(docLine, out var info) && info.Origin != ConflictRowOrigin.Added)
        {
            region = info.Region;
            side = info.Origin == ConflictRowOrigin.Ours ? ConflictSide.Ours : ConflictSide.Theirs;
            index = info.Index;
            return true;
        }

        region = null!;
        side = default;
        index = -1;
        return false;
    }

    /// <summary>The current parse's marker regions, for the hunk boxes on unresolved conflicts.</summary>
    public void SetMarkerRegions(IReadOnlyList<ConflictRegion> regions)
    {
        _markerRegions = regions;
        RebuildHunkGlyphs();
    }

    #endregion

    #region Mutations

    /// <summary>
    /// Replaces a marker block with all of its ours and theirs rows and applies
    /// <paramref name="select"/>. Returns null when the conflict has no rows on either side.
    /// </summary>
    public ManagedRegion? Activate(ConflictRegion region, Action<ConflictSelection> select, ConflictRegionAnalysis? analysis = null)
    {
        if (region.OursLineCount + region.TheirsLineCount == 0)
        {
            return null;
        }

        var selection = new ConflictSelection(region.OursLines, region.TheirsLines);
        select(selection);

        int endLineNumber = region.EndLine + 1;
        var endLine = _document.GetLineByNumber(endLineNumber);
        string trailing = _document.GetText(endLine.EndOffset, endLine.DelimiterLength);
        string delimiter = trailing.Length > 0 ? trailing : FirstDelimiter();
        string blockText = _document.GetText(region.TotalOffset, region.TotalLength);
        var managed = new ManagedRegion(selection, region, blockText, delimiter, analysis);

        var layout = selection.Layout;
        var texts = layout.Select(r => selection.RowText(r.Side, r.Index)).ToList();
        string replacement = string.Join(delimiter, texts) + trailing;

        Mutate(() =>
        {
            _document.Replace(region.TotalOffset, region.TotalLength, replacement);
            int offset = region.TotalOffset;
            for (int i = 0; i < layout.Count; i++)
            {
                int length = texts[i].Length + (i < layout.Count - 1 ? delimiter.Length : trailing.Length);
                var row = new RowSegment { Region = managed, Side = layout[i].Side, Index = layout[i].Index, StartOffset = offset, Length = length, AttachedLength = length };
                managed.Rows.Add(row);
                _segments.Add(row);
                offset += length;
            }
            InsertRegionSorted(managed);
        });
        return managed;
    }

    /// <summary>Unchecked or partial → keep every row of the side; checked → drop them all.</summary>
    public void ToggleHunk(ManagedRegion region, ConflictSide side)
    {
        bool include = region.Selection.HunkState(side) != ConflictHunkState.Checked;
        ApplySelection(region, s => s.SetHunk(side, include));
    }

    /// <summary>Toggles the managed row on <paramref name="docLine"/>; false when there is none.</summary>
    public bool ToggleRow(int docLine)
    {
        if (!TryGetRow(docLine, out var region, out var side, out int index))
        {
            return false;
        }

        ApplySelection(region, s => s.ToggleRow(side, index));
        return true;
    }

    /// <summary>Changes a managed region's selection; rows move only when the side order changes.</summary>
    public void ApplySelection(ManagedRegion region, Action<ConflictSelection> change)
    {
        var before = Capture();
        var oldOrder = region.Selection.SideLayout[0];
        change(region.Selection);
        if (region.Selection.SideLayout[0] == oldOrder)
        {
            var after = Capture();
            _lastGood = after;
            _document.UndoStack.Push(new SelectionSwap(this, before, after));
            RebuildIndex();
            return;
        }

        // Undo must restore the selection from before the change, which `before` holds; the text
        // edit below is bracketed like every other session edit.
        Mutate(() => Reorder(region), before);
    }

    /// <summary>Puts the original marker block back and drops the region.</summary>
    public void Restore(ManagedRegion region)
    {
        Mutate(() =>
        {
            var (start, end) = RegionRange(region);
            DropRegion(region);
            _document.Replace(start, end - start, region.OriginalBlockText);
        });
    }

    /// <summary>
    /// Forgets every region without touching the document or the undo stack. Must run before the
    /// view replaces the whole text: a full-text replace collapses every segment, and normalising
    /// those would freeze the regions against the newly loaded file.
    /// </summary>
    public void Clear()
    {
        _segments.Clear();
        _regions.Clear();
        _pending = null;
        _lastGood = new([]);
        _markerRegions = [];
        RebuildIndex();
    }

    #endregion

    #region Normalisation

    /// <summary>
    /// Called by the view at the top of every <c>TextChanged</c>. After undo/redo it installs the
    /// state the played-back operations recorded. After a user edit it re-attaches every row to the
    /// line its start offset is now on; lines inside a region that no row starts are added lines.
    /// A region whose row lost its line, or with two rows starting on one line, is frozen: its
    /// dropped rows are deleted and it becomes plain text, in a posted undo step that continues the
    /// edit's own step, so one Ctrl+Z brings both back.
    /// </summary>
    /// <returns>True when an undo or redo of a session change was installed, which may have moved
    /// rows to other lines.</returns>
    public bool Normalize()
    {
        if (_mutating)
        {
            return false;
        }

        if (_pending != null)
        {
            Install(_pending);
            _pending = null;
            _lastGood = Capture();
            RebuildIndex();
            return true;
        }

        if (_regions.Count == 0)
        {
            return false;
        }

        bool broken = false;
        foreach (var region in _regions)
        {
            if (!Reattach(region))
            {
                broken = true;
            }
        }

        if (!broken)
        {
            _lastGood = Capture();
        }
        else if (!_freezePosted)
        {
            _freezePosted = true;
            Dispatcher.UIThread.Post(FreezeBrokenRegions);
        }

        RebuildIndex();
        return false;
    }

    /// <summary>Re-attaches each row to its line; false when the region can no longer be managed.</summary>
    private bool Reattach(ManagedRegion region)
    {
        region.IsBroken = !RowsIntact(region);
        if (region.IsBroken)
        {
            return false;
        }

        // Every row is re-anchored to exactly its own line. The region ends at its last row, so a
        // line typed below that row is outside it, like any line typed below a conflict.
        foreach (var row in region.Rows)
        {
            var line = _document.GetLineByOffset(row.StartOffset);
            AttachToLine(row, line);
        }
        region.StartLine = _document.GetLineByOffset(region.Rows[0].StartOffset).LineNumber - 1;
        region.EndLine = _document.GetLineByOffset(region.Rows[^1].StartOffset).LineNumber - 1;
        return true;
    }

    private void AttachToLine(RowSegment row, DocumentLine line)
    {
        if (row.StartOffset != line.Offset)
        {
            row.StartOffset = line.Offset;
        }
        row.Length = line.TotalLength;
        row.AttachedLength = line.TotalLength;
    }

    private void FreezeBrokenRegions()
    {
        _freezePosted = false;
        var broken = _regions.Where(r => !RowsIntact(r)).ToList();
        if (_pending != null)
        {
            return; // an undo/redo is being installed; it decides the regions
        }
        if (broken.Count == 0)
        {
            return;
        }

        var before = _lastGood;
        var stack = _document.UndoStack;
        stack.StartContinuedUndoGroup();
        try
        {
            stack.Push(new Reinstall(this, undo: before, redo: null));
            _mutating = true;
            try
            {
                using (_document.RunUpdate())
                {
                    foreach (var region in broken)
                    {
                        // Drop the struck rows whose lines still stand on their own (no other live
                        // row starts there; a row whose line was deleted has collapsed), bottom up.
                        // A deletion row's line goes only while it is still empty.
                        var live = region.Rows.Where(r => !(r.Length == 0 && r.AttachedLength > 0)).ToList();
                        var deletable = live
                            .Where(r => (region.Selection.IsDeletionRow(r.Side, r.Index)
                                            ? _document.GetLineByOffset(r.StartOffset).Length == 0
                                            : !region.Selection.IsIncluded(r.Side, r.Index))
                                        && r.StartOffset == _document.GetLineByOffset(r.StartOffset).Offset
                                        && !live.Any(o => o != r && _document.GetLineByOffset(o.StartOffset).LineNumber == _document.GetLineByOffset(r.StartOffset).LineNumber))
                            .Select(r => _document.GetLineByOffset(r.StartOffset))
                            .OrderByDescending(l => l.LineNumber)
                            .ToList();
                        DropRegion(region);
                        foreach (var line in deletable)
                        {
                            RemoveLine(line);
                        }
                    }
                }
            }
            finally
            {
                _mutating = false;
            }

            foreach (var region in _regions)
            {
                Reattach(region);
            }
            var after = Capture();
            _lastGood = after;
            stack.Push(new Reinstall(this, undo: null, redo: after));
        }
        finally
        {
            stack.EndUndoGroup();
        }

        RebuildIndex();
    }

    private bool RowsIntact(ManagedRegion region)
    {
        int previousLine = -1;
        foreach (var row in region.Rows)
        {
            if (row.Length == 0 && row.AttachedLength > 0)
            {
                return false; // the row's whole line, line break included, was deleted
            }
            int line = _document.GetLineByOffset(row.StartOffset).LineNumber;
            if (line <= previousLine)
            {
                return false; // two rows now start on one line
            }
            previousLine = line;
        }
        return true;
    }

    private void RemoveLine(DocumentLine line)
    {
        if (line.DelimiterLength > 0)
        {
            _document.Remove(line.Offset, line.TotalLength);
        }
        else if (line.PreviousLine is { } previous)
        {
            // The document's last line: take the line break before it instead.
            _document.Remove(previous.EndOffset, line.EndOffset - previous.EndOffset);
        }
        else
        {
            _document.Remove(line.Offset, line.Length);
        }
    }

    #endregion

    #region Output

    /// <summary>
    /// The document text with dropped rows and deletion rows (and their line breaks) removed; a region with nothing
    /// kept contributes no lines. Line breaks and the presence of a final line break are kept.
    /// </summary>
    public string ComposeOutput()
    {
        if (_omittedLines.Length == 0)
        {
            return _document.Text;
        }

        var sb = new System.Text.StringBuilder(_document.TextLength);
        int next = 0;
        bool lastLineExcluded = false;
        foreach (var line in _document.Lines)
        {
            int index = line.LineNumber - 1;
            if (next < _omittedLines.Length && _omittedLines[next] == index)
            {
                next++;
                lastLineExcluded = line.NextLine == null;
                continue;
            }
            sb.Append(_document.GetText(line.Offset, line.TotalLength));
        }

        if (lastLineExcluded)
        {
            // The omitted row was the last line, which had no line break: neither does the output.
            int end = sb.Length;
            if (end > 0 && sb[end - 1] == '\n')
            {
                end--;
            }
            if (end > 0 && sb[end - 1] == '\r')
            {
                end--;
            }
            sb.Length = end;
        }

        return sb.ToString();
    }

    #endregion

    #region Read-only sections

    /// <summary>Nothing can be typed on a dropped or deletion row, at either end included.</summary>
    public bool CanInsert(int offset)
    {
        if (_omittedLines.Length == 0)
        {
            return true;
        }
        var line = _document.GetLineByOffset(offset);
        return Array.BinarySearch(_omittedLines, line.LineNumber - 1) < 0;
    }

    /// <summary>
    /// The parts of <paramref name="segment"/> outside every dropped or deletion row's protected range, which
    /// runs from the previous line's line break through the row's own line break, so an omitted line
    /// can be neither edited nor joined to its neighbours.
    /// </summary>
    public IEnumerable<ISegment> GetDeletableSegments(ISegment segment)
    {
        if (_omittedLines.Length == 0)
        {
            return [segment];
        }

        if (segment.Length == 0)
        {
            return CanInsert(segment.Offset) ? [segment] : [];
        }

        int firstLine = _document.GetLineByOffset(segment.Offset).LineNumber - 1;
        int lastLine = Math.Min(_document.LineCount - 1, _document.GetLineByOffset(segment.EndOffset).LineNumber);
        int at = Array.BinarySearch(_omittedLines, firstLine);
        if (at < 0)
        {
            at = ~at;
        }

        var result = new List<ISegment>();
        int writableFrom = segment.Offset;
        int end = segment.EndOffset;
        for (; at < _omittedLines.Length && _omittedLines[at] <= lastLine; at++)
        {
            var line = _document.GetLineByNumber(_omittedLines[at] + 1);
            int protectedStart = line.PreviousLine?.EndOffset ?? line.Offset;
            int protectedEnd = line.EndOffset + line.DelimiterLength;
            if (protectedEnd <= writableFrom || protectedStart >= end)
            {
                continue;
            }
            if (protectedStart > writableFrom)
            {
                result.Add(new SimpleSegment(writableFrom, protectedStart - writableFrom));
            }
            writableFrom = Math.Max(writableFrom, protectedEnd);
        }

        if (writableFrom < end)
        {
            result.Add(new SimpleSegment(writableFrom, end - writableFrom));
        }
        return result;
    }

    #endregion

    #region Mechanics

    /// <summary>
    /// Runs a session text edit as one undo step: a reinstall-before op, the edit, a
    /// reinstall-after op.
    /// </summary>
    private void Mutate(Action edit, Snapshot? before = null)
    {
        before ??= Capture();
        var stack = _document.UndoStack;
        stack.StartUndoGroup();
        try
        {
            stack.Push(new Reinstall(this, undo: before, redo: null));
            _mutating = true;
            try
            {
                using (_document.RunUpdate())
                {
                    edit();
                }
            }
            finally
            {
                _mutating = false;
            }

            foreach (var region in _regions)
            {
                Reattach(region);
            }
            var after = Capture();
            _lastGood = after;
            stack.Push(new Reinstall(this, undo: null, redo: after));
        }
        finally
        {
            stack.EndUndoGroup();
        }

        RebuildIndex();
    }

    /// <summary>Rewrites a region's rows in its selection's layout, each with its current text and
    /// the added lines below it carried along.</summary>
    private void Reorder(ManagedRegion region)
    {
        var (start, end) = RegionRange(region);
        var lastLine = _document.GetLineByNumber(region.EndLine + 1);
        string trailing = _document.GetText(lastLine.EndOffset, lastLine.DelimiterLength);

        // Each row with the added lines below it, by (side, index).
        var groups = new Dictionary<(ConflictSide, int), List<string>>();
        List<string>? current = null;
        int rowAt = 0;
        for (int ln = region.StartLine; ln <= region.EndLine; ln++)
        {
            var line = _document.GetLineByNumber(ln + 1);
            string text = _document.GetText(line.Offset, line.Length);
            if (rowAt < region.Rows.Count && region.Rows[rowAt].StartOffset == line.Offset)
            {
                var row = region.Rows[rowAt++];
                current = [text];
                groups[(row.Side, row.Index)] = current;
            }
            else
            {
                current!.Add(text);
            }
        }

        foreach (var row in region.Rows)
        {
            _segments.Remove(row);
        }
        region.Rows.Clear();

        var lines = new List<string>();
        var rowStarts = new List<(ConflictSide Side, int Index, int LineIndex)>();
        foreach (var (side, index) in region.Selection.Layout)
        {
            var group = groups[(side, index)];
            rowStarts.Add((side, index, lines.Count));
            lines.AddRange(group);
        }

        _document.Replace(start, end - start, string.Join(region.Delimiter, lines) + trailing);

        foreach (var (side, index, lineIndex) in rowStarts)
        {
            var line = _document.GetLineByNumber(region.StartLine + lineIndex + 1);
            var row = new RowSegment { Region = region, Side = side, Index = index, StartOffset = line.Offset, Length = line.TotalLength, AttachedLength = line.TotalLength };
            region.Rows.Add(row);
            _segments.Add(row);
        }
    }

    private (int Start, int End) RegionRange(ManagedRegion region)
    {
        var first = _document.GetLineByNumber(region.StartLine + 1);
        var last = _document.GetLineByNumber(region.EndLine + 1);
        return (first.Offset, last.EndOffset + last.DelimiterLength);
    }

    private void DropRegion(ManagedRegion region)
    {
        foreach (var row in region.Rows)
        {
            _segments.Remove(row);
        }
        region.Rows.Clear();
        _regions.Remove(region);
    }

    private void InsertRegionSorted(ManagedRegion region)
    {
        int start = region.Rows[0].StartOffset;
        int at = _regions.FindIndex(r => r.Rows[0].StartOffset > start);
        _regions.Insert(at < 0 ? _regions.Count : at, region);
    }

    private string FirstDelimiter()
    {
        foreach (var line in _document.Lines)
        {
            if (line.DelimiterLength > 0)
            {
                return _document.GetText(line.EndOffset, line.DelimiterLength);
            }
        }
        return "\n";
    }

    private Snapshot Capture()
    {
        var regions = new RegionState[_regions.Count];
        for (int i = 0; i < regions.Length; i++)
        {
            var r = _regions[i];
            var rows = new RowState[r.Rows.Count];
            for (int j = 0; j < rows.Length; j++)
            {
                var row = r.Rows[j];
                rows[j] = new RowState(row.StartOffset, row.Length, row.Side, row.Index, row.AttachedLength);
            }
            regions[i] = new RegionState(r.Selection.Clone(), r.Original, r.OriginalBlockText, r.Delimiter, r.Analysis, rows);
        }
        return new Snapshot(regions);
    }

    private void Install(Snapshot snapshot)
    {
        _segments.Clear();
        _regions.Clear();
        foreach (var state in snapshot.Regions)
        {
            var region = new ManagedRegion(state.Selection.Clone(), state.Original, state.OriginalBlockText, state.Delimiter, state.Analysis);
            foreach (var rs in state.Rows)
            {
                var row = new RowSegment { Region = region, Side = rs.Side, Index = rs.Index, StartOffset = rs.Start, Length = rs.Length, AttachedLength = rs.AttachedLength };
                region.Rows.Add(row);
                _segments.Add(row);
            }
            _regions.Add(region);
        }
        foreach (var region in _regions)
        {
            Reattach(region);
        }
    }

    /// <summary>Rebuilds the per-line lookups and spans, then raises <see cref="Changed"/>.</summary>
    private void RebuildIndex()
    {
        _lines.Clear();
        var omitted = new List<int>();
        var spans = new List<ConflictManagedSpan>(_regions.Count);

        foreach (var region in _regions)
        {
            if (region.IsBroken)
            {
                continue;
            }

            var selection = region.Selection;
            region.OursRowLines = new int[selection.RowCount(ConflictSide.Ours)];
            region.TheirsRowLines = new int[selection.RowCount(ConflictSide.Theirs)];

            var origins = new ConflictLineOrigin[region.EndLine - region.StartLine + 1];
            int rowAt = 0;
            for (int ln = region.StartLine; ln <= region.EndLine; ln++)
            {
                var visual = ln == region.StartLine ? ConflictRowVisual.RegionTop : ConflictRowVisual.None;
                if (ln == region.EndLine)
                {
                    visual |= ConflictRowVisual.RegionBottom;
                }

                int lineOffset = _document.GetLineByNumber(ln + 1).Offset;
                if (rowAt < region.Rows.Count && region.Rows[rowAt].StartOffset == lineOffset)
                {
                    var row = region.Rows[rowAt++];
                    bool included = selection.IsIncluded(row.Side, row.Index);
                    bool isDeletion = selection.IsDeletionRow(row.Side, row.Index);
                    var origin = row.Side == ConflictSide.Ours ? ConflictRowOrigin.Ours : ConflictRowOrigin.Theirs;
                    visual |= row.Side == ConflictSide.Ours ? ConflictRowVisual.Ours : ConflictRowVisual.Theirs;
                    if (isDeletion)
                    {
                        visual |= ConflictRowVisual.Deletion;
                    }

                    if (!included)
                    {
                        visual |= ConflictRowVisual.Excluded;
                    }

                    if (!included || isDeletion)
                    {
                        omitted.Add(ln);
                    }

                    string sourceText = selection.RowText(row.Side, row.Index);
                    origins[ln - region.StartLine] = new ConflictLineOrigin(origin, row.Index, sourceText, isDeletion);
                    _lines[ln] = new LineInfo(region, origin, row.Index, !included, visual);
                    (row.Side == ConflictSide.Ours ? region.OursRowLines : region.TheirsRowLines)[row.Index] = ln;
                }
                else
                {
                    visual |= ConflictRowVisual.Added;
                    origins[ln - region.StartLine] = new ConflictLineOrigin(ConflictRowOrigin.Added, -1, string.Empty);
                    _lines[ln] = new LineInfo(region, ConflictRowOrigin.Added, -1, false, visual);
                }
            }
            spans.Add(new ConflictManagedSpan(region.StartLine, origins));
        }

        _omittedLines = [.. omitted];
        _spans = spans;
        RebuildHunkGlyphs();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildHunkGlyphs()
    {
        _oursHunkGlyphs.Clear();
        _theirsHunkGlyphs.Clear();

        foreach (var region in _markerRegions)
        {
            // A side with no lines still gets a box, on its deletion row, when the other side has lines.
            if (region.OursLineCount + region.TheirsLineCount > 0)
            {
                _oursHunkGlyphs[region.OursStartLine] = ConflictToggleGlyph.Unchecked;
                _theirsHunkGlyphs[region.TheirsStartLine] = ConflictToggleGlyph.Unchecked;
            }
        }

        foreach (var region in _regions)
        {
            if (region.IsBroken)
            {
                continue;
            }
            AddHunkGlyph(_oursHunkGlyphs, region, ConflictSide.Ours, region.OursRowLines);
            AddHunkGlyph(_theirsHunkGlyphs, region, ConflictSide.Theirs, region.TheirsRowLines);
        }
    }

    private static void AddHunkGlyph(Dictionary<int, ConflictToggleGlyph> map, ManagedRegion region, ConflictSide side, int[] rowLines)
    {
        if (rowLines.Length == 0)
        {
            return;
        }

        map[rowLines.Min()] = region.Selection.HunkState(side) switch
        {
            ConflictHunkState.Checked => ConflictToggleGlyph.Checked,
            ConflictHunkState.Partial => ConflictToggleGlyph.Partial,
            _ => ConflictToggleGlyph.Unchecked,
        };
    }

    #endregion

    #region Undo operations

    /// <summary>Records the session state to install once playback ends (see <see cref="Normalize"/>).</summary>
    private sealed class Reinstall(ConflictSelectionSession owner, Snapshot? undo, Snapshot? redo) : IUndoableOperation
    {
        public void Undo()
        {
            if (undo != null)
            {
                owner._pending = undo;
            }
        }

        public void Redo()
        {
            if (redo != null)
            {
                owner._pending = redo;
            }
        }
    }

    /// <summary>A toggle that changed no text: offsets are unchanged, so the selection is swapped
    /// immediately.</summary>
    private sealed class SelectionSwap(ConflictSelectionSession owner, Snapshot before, Snapshot after) : IUndoableOperation
    {
        public void Undo() => owner.InstallNow(before);

        public void Redo() => owner.InstallNow(after);
    }

    private void InstallNow(Snapshot snapshot)
    {
        Install(snapshot);
        _lastGood = Capture();
        RebuildIndex();
    }

    #endregion
}
