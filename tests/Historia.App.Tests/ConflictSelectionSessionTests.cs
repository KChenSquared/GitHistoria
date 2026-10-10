namespace HistoriaApp.Tests;

/// <summary>
/// Managed conflict regions over a plain Result document: rows, toggles, read-only struck lines,
/// user edits, freezing, undo/redo and the composed save text. The session is wired to the
/// document the way the resolver wires it: <c>Normalize</c> at the top of every <c>TextChanged</c>.
/// </summary>
[Trait("tier", "ui")]
public sealed class ConflictSelectionSessionTests
{
    private const string OneConflict =
        "top\n" +
        "<<<<<<< HEAD\n" +
        "ours 1\n" +
        "ours 2\n" +
        "=======\n" +
        "theirs 1\n" +
        ">>>>>>> branch\n" +
        "bottom\n";

    private static (TextDocument Doc, ConflictSelectionSession Session) Make(string text = OneConflict)
    {
        var doc = new TextDocument(text);
        var session = new ConflictSelectionSession(doc);
        doc.TextChanged += (_, _) => session.Normalize();
        return (doc, session);
    }

    private static ConflictRegion Region(TextDocument doc, int index = 0) => ConflictMarkerParser.Parse(doc.Text).Regions[index];

    /// <summary>0-based index of the first line with exactly this text.</summary>
    private static int LineOf(TextDocument doc, string text)
        => doc.Lines.First(l => doc.GetText(l.Offset, l.Length) == text).LineNumber - 1;

    private static DocumentLine Line(TextDocument doc, string text) => doc.GetLineByNumber(LineOf(doc, text) + 1);

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static ConflictSelectionSession.ManagedRegion ActivateOurs(TextDocument doc, ConflictSelectionSession session)
        => session.Activate(Region(doc), s => s.SetHunk(ConflictSide.Ours, true))!;

    [AvaloniaFact]
    public void Activate_ours_inserts_all_rows_with_theirs_excluded()
    {
        var (doc, session) = Make();
        var region = ActivateOurs(doc, session);

        Assert.Equal("top\nours 1\nours 2\ntheirs 1\nbottom\n", doc.Text);
        Assert.Equal("top\nours 1\nours 2\nbottom\n", session.ComposeOutput());
        Assert.DoesNotContain("<<<<<<<", doc.Text);
        Assert.Equal(1, region.StartLine);
        Assert.Equal(3, region.EndLine);
        Assert.True(session.IsExcluded(3));
        Assert.False(session.IsExcluded(1));
        Assert.Equal(ConflictToggleGlyph.Checked, session.RowGlyph(1));
        Assert.Equal(ConflictToggleGlyph.Unchecked, session.RowGlyph(3));
        Assert.Equal(ConflictToggleGlyph.None, session.RowGlyph(0));
        Assert.Equal(ConflictToggleGlyph.Checked, session.HunkGlyph(ConflictSide.Ours, 1));
        Assert.Equal(ConflictToggleGlyph.Unchecked, session.HunkGlyph(ConflictSide.Theirs, 3));
        Assert.True((session.RowVisual(1) & ConflictRowVisual.RegionTop) != 0);
        Assert.True((session.RowVisual(3) & ConflictRowVisual.RegionBottom) != 0);
        Assert.True((session.RowVisual(3) & ConflictRowVisual.Excluded) != 0);
    }

    [AvaloniaFact]
    public void Marker_regions_get_hunk_boxes_on_each_sides_first_line()
    {
        var (doc, session) = Make();
        session.SetMarkerRegions(ConflictMarkerParser.Parse(doc.Text).Regions);

        Assert.Equal(ConflictToggleGlyph.Unchecked, session.HunkGlyph(ConflictSide.Ours, 2));
        Assert.Equal(ConflictToggleGlyph.None, session.HunkGlyph(ConflictSide.Ours, 3));
        Assert.Equal(ConflictToggleGlyph.Unchecked, session.HunkGlyph(ConflictSide.Theirs, 5));
        Assert.Equal(ConflictToggleGlyph.None, session.RowGlyph(2)); // no line toggles before a hunk is picked
    }

    [AvaloniaFact]
    public void Unchecking_hunk_resets_its_rows()
    {
        var (doc, session) = Make();
        var region = ActivateOurs(doc, session);
        session.ToggleRow(LineOf(doc, "ours 2"));
        Assert.Equal(ConflictHunkState.Partial, region.Selection.HunkState(ConflictSide.Ours));

        session.ToggleHunk(region, ConflictSide.Ours); // partial -> every row kept
        Assert.Equal(ConflictHunkState.Checked, region.Selection.HunkState(ConflictSide.Ours));

        session.ToggleHunk(region, ConflictSide.Ours); // checked -> every row dropped
        Assert.Equal(ConflictHunkState.Unchecked, region.Selection.HunkState(ConflictSide.Ours));
        Assert.True(session.IsExcluded(LineOf(doc, "ours 1")));
        Assert.True(session.IsExcluded(LineOf(doc, "ours 2")));
    }

    [AvaloniaFact]
    public void Excluding_every_row_unchecks_hunk()
    {
        var (doc, session) = Make();
        ActivateOurs(doc, session);
        int first = LineOf(doc, "ours 1");

        session.ToggleRow(first);
        session.ToggleRow(LineOf(doc, "ours 2"));

        Assert.Equal(ConflictToggleGlyph.Unchecked, session.HunkGlyph(ConflictSide.Ours, first));
    }

    [AvaloniaFact]
    public void Partial_hunk_glyph()
    {
        var (doc, session) = Make();
        ActivateOurs(doc, session);
        int second = LineOf(doc, "ours 2");

        session.ToggleRow(second);

        Assert.Equal(ConflictToggleGlyph.Partial, session.HunkGlyph(ConflictSide.Ours, LineOf(doc, "ours 1")));
        Assert.Equal(ConflictToggleGlyph.Unchecked, session.RowGlyph(second));
        Assert.Equal("top\nours 1\nbottom\n", session.ComposeOutput());
    }

    [AvaloniaFact]
    public void Both_unchecked_composes_no_lines()
    {
        var (doc, session) = Make();
        var region = ActivateOurs(doc, session);

        session.ToggleHunk(region, ConflictSide.Ours);

        Assert.True(region.Selection.IsEmpty);
        Assert.Single(session.Regions);
        Assert.Equal(1, session.EmptyRegionCount);
        Assert.Equal("top\nbottom\n", session.ComposeOutput());
    }

    [AvaloniaFact]
    public void Order_clicked_moves_rows_and_keeps_edits()
    {
        var (doc, session) = Make();
        var region = session.Activate(Region(doc), s => s.SetHunk(ConflictSide.Theirs, true))!;
        Assert.Equal("top\ntheirs 1\nours 1\nours 2\nbottom\n", doc.Text);

        session.ToggleHunk(region, ConflictSide.Ours); // ours checked after theirs: already in place
        Assert.Equal("top\ntheirs 1\nours 1\nours 2\nbottom\n", doc.Text);

        doc.Insert(Line(doc, "ours 1").Offset + 4, "X");
        region = session.Regions[0];
        session.ToggleHunk(region, ConflictSide.Theirs); // unchecking never moves
        Assert.Equal("top\ntheirs 1\noursX 1\nours 2\nbottom\n", doc.Text);

        session.ToggleHunk(session.Regions[0], ConflictSide.Theirs); // rechecked: goes after ours
        Assert.Equal("top\noursX 1\nours 2\ntheirs 1\nbottom\n", doc.Text);
        Assert.Equal("top\noursX 1\nours 2\ntheirs 1\nbottom\n", session.ComposeOutput());
        Assert.Equal(ConflictToggleGlyph.Checked, session.RowGlyph(LineOf(doc, "oursX 1")));
    }

    [AvaloniaFact]
    public void Typed_line_is_added_and_always_kept()
    {
        var (doc, session) = Make();
        var region = session.Activate(Region(doc), s =>
        {
            s.SetHunk(ConflictSide.Ours, true);
            s.SetHunk(ConflictSide.Theirs, true);
        })!;
        doc.Insert(Line(doc, "ours 1").EndOffset, "\ntyped");
        Pump();

        int typed = LineOf(doc, "typed");
        Assert.True((session.RowVisual(typed) & ConflictRowVisual.Added) != 0);
        Assert.Equal(ConflictToggleGlyph.None, session.RowGlyph(typed));
        Assert.Contains("typed", session.ComposeOutput());

        region = session.Regions[0];
        session.ToggleHunk(region, ConflictSide.Ours); // drop ours: typed line stays kept
        Assert.Contains("typed", session.ComposeOutput());
        Assert.DoesNotContain("ours 1", session.ComposeOutput());

        session.ToggleHunk(session.Regions[0], ConflictSide.Ours); // re-keep ours: moves after theirs, typed line with it
        Assert.Equal("top\ntheirs 1\nours 1\ntyped\nours 2\nbottom\n", doc.Text);
        Assert.True((session.RowVisual(LineOf(doc, "typed")) & ConflictRowVisual.Added) != 0);
    }

    [AvaloniaFact]
    public void Line_typed_below_the_last_row_stays_outside_the_region()
    {
        var (doc, session) = Make();
        var region = session.Activate(Region(doc), s =>
        {
            s.SetHunk(ConflictSide.Ours, true);
            s.SetHunk(ConflictSide.Theirs, true);
        })!;
        int lastRow = LineOf(doc, "theirs 1");
        Assert.Equal(lastRow, region.EndLine);

        doc.Insert(Line(doc, "theirs 1").EndOffset, "\n");
        Pump();
        Assert.Equal(lastRow, session.Regions[0].EndLine);
        Assert.Equal(ConflictRowVisual.None, session.RowVisual(lastRow + 1));

        doc.Insert(doc.GetLineByNumber(lastRow + 2).Offset, "typed"); // typing on it changes nothing
        Pump();
        Assert.Equal(lastRow, session.Regions[0].EndLine);
        Assert.Equal(ConflictRowVisual.None, session.RowVisual(LineOf(doc, "typed")));
        Assert.Contains("theirs 1\ntyped\nbottom\n", session.ComposeOutput());
    }

    [AvaloniaFact]
    public void Excluded_rows_are_read_only()
    {
        var (doc, session) = Make();
        ActivateOurs(doc, session);
        var struck = Line(doc, "theirs 1");
        var kept = Line(doc, "ours 1");
        var keptAbove = Line(doc, "ours 2");
        var ro = session.ReadOnlySections;

        Assert.False(ro.CanInsert(struck.Offset));
        Assert.False(ro.CanInsert(struck.Offset + 3));
        Assert.False(ro.CanInsert(struck.EndOffset));
        Assert.True(ro.CanInsert(kept.Offset));
        Assert.True(ro.CanInsert(kept.Offset + 2));
        Assert.True(ro.CanInsert(keptAbove.EndOffset));
    }

    [AvaloniaFact]
    public void Line_breaks_around_excluded_row_are_not_deletable()
    {
        var (doc, session) = Make();
        ActivateOurs(doc, session);
        var keptAbove = Line(doc, "ours 2");
        var struck = Line(doc, "theirs 1");
        var ro = session.ReadOnlySections;

        Assert.Empty(ro.GetDeletableSegments(new SimpleSegment(keptAbove.EndOffset, keptAbove.DelimiterLength)));
        Assert.Empty(ro.GetDeletableSegments(new SimpleSegment(struck.EndOffset, struck.DelimiterLength)));

        // A range over the kept line's text and its break: only the text is deletable.
        var parts = ro.GetDeletableSegments(new SimpleSegment(keptAbove.Offset, keptAbove.TotalLength)).ToList();
        var part = Assert.Single(parts);
        Assert.Equal(keptAbove.Offset, part.Offset);
        Assert.Equal(keptAbove.Length, part.Length);
    }

    [AvaloniaFact]
    public void Typing_at_column_0_of_kept_row_keeps_toggle()
    {
        var (doc, session) = Make();
        ActivateOurs(doc, session);
        int line = LineOf(doc, "ours 2");

        doc.Insert(Line(doc, "ours 2").Offset, "X");
        Pump();

        Assert.Single(session.Regions);
        Assert.Equal(line, LineOf(doc, "Xours 2"));
        Assert.Equal(ConflictToggleGlyph.Checked, session.RowGlyph(line));
        Assert.Contains("Xours 2", session.ComposeOutput());
    }

    [AvaloniaFact]
    public void Enter_at_column_0_makes_an_added_line_above_the_row()
    {
        var (doc, session) = Make();
        ActivateOurs(doc, session);
        int line = LineOf(doc, "ours 2");

        doc.Insert(Line(doc, "ours 2").Offset, "\n");
        Pump();

        Assert.Single(session.Regions);
        Assert.True((session.RowVisual(line) & ConflictRowVisual.Added) != 0);
        Assert.Equal(line + 1, LineOf(doc, "ours 2"));
        Assert.Equal(ConflictToggleGlyph.Checked, session.RowGlyph(line + 1));
        Assert.Equal("top\nours 1\n\nours 2\nbottom\n", session.ComposeOutput());
    }

    [AvaloniaFact]
    public void Joining_two_rows_freezes_region()
    {
        var (doc, session) = Make();
        ActivateOurs(doc, session);

        doc.Remove(Line(doc, "ours 1").EndOffset, 1);
        Pump();

        Assert.Empty(session.Regions);
        Assert.Equal("top\nours 1ours 2\nbottom\n", doc.Text);
        Assert.Equal(doc.Text, session.ComposeOutput());
    }

    [AvaloniaFact]
    public void Clear_then_full_text_replace_freezes_nothing()
    {
        var (doc, session) = Make();
        ActivateOurs(doc, session);

        session.Clear();
        doc.Text = "a new file\n";
        Pump();

        Assert.Empty(session.Regions);
        Assert.Equal("a new file\n", doc.Text);
        Assert.Empty(session.Spans);
    }

    [AvaloniaFact]
    public void Undo_redo_activation_and_toggles()
    {
        var (doc, session) = Make();
        ActivateOurs(doc, session);

        doc.UndoStack.Undo();
        Assert.Equal(OneConflict, doc.Text);
        Assert.Empty(session.Regions);

        doc.UndoStack.Redo();
        var region = Assert.Single(session.Regions);
        Assert.Equal(ConflictHunkState.Checked, region.Selection.HunkState(ConflictSide.Ours));
        Assert.Equal("top\nours 1\nours 2\ntheirs 1\nbottom\n", doc.Text);
        Assert.True(session.IsExcluded(LineOf(doc, "theirs 1")));

        int theirs = LineOf(doc, "theirs 1");
        session.ToggleRow(theirs);
        Assert.False(session.IsExcluded(theirs));

        doc.UndoStack.Undo();
        Assert.True(session.IsExcluded(theirs));
        Assert.Equal("top\nours 1\nours 2\nbottom\n", session.ComposeOutput());

        doc.UndoStack.Redo();
        Assert.False(session.IsExcluded(theirs));
        Assert.Equal("top\nours 1\nours 2\ntheirs 1\nbottom\n", session.ComposeOutput());
    }

    [AvaloniaFact]
    public void Undo_redo_reorder()
    {
        var (doc, session) = Make();
        var region = ActivateOurs(doc, session);
        session.ToggleHunk(region, ConflictSide.Ours);
        session.ToggleHunk(session.Regions[0], ConflictSide.Theirs); // theirs moves first
        Assert.Equal("top\ntheirs 1\nours 1\nours 2\nbottom\n", doc.Text);

        doc.UndoStack.Undo();
        Assert.Equal("top\nours 1\nours 2\ntheirs 1\nbottom\n", doc.Text);
        Assert.True(session.IsExcluded(LineOf(doc, "theirs 1")));
        Assert.True(session.IsExcluded(LineOf(doc, "ours 1")));

        doc.UndoStack.Redo();
        Assert.Equal("top\ntheirs 1\nours 1\nours 2\nbottom\n", doc.Text);
        Assert.False(session.IsExcluded(LineOf(doc, "theirs 1")));
        Assert.Equal("top\ntheirs 1\nbottom\n", session.ComposeOutput());
    }

    [AvaloniaFact]
    public void Deleting_an_included_row_freezes_region()
    {
        var (doc, session) = Make();
        ActivateOurs(doc, session);
        var managedText = doc.Text;

        var row = Line(doc, "ours 2");
        doc.Remove(row.Offset, row.TotalLength);
        Pump();

        Assert.Empty(session.Regions);
        Assert.Equal("top\nours 1\nbottom\n", doc.Text);

        doc.UndoStack.Undo();
        Assert.Equal(managedText, doc.Text);
        var region = Assert.Single(session.Regions);
        Assert.True(session.IsExcluded(LineOf(doc, "theirs 1")));
        Assert.Equal(ConflictHunkState.Checked, region.Selection.HunkState(ConflictSide.Ours));
    }

    [AvaloniaFact]
    public void Restore_puts_original_block_back()
    {
        var (doc, session) = Make();
        var region = ActivateOurs(doc, session);
        session.ToggleRow(LineOf(doc, "ours 1"));

        session.Restore(session.Regions[0]);

        Assert.Equal(OneConflict, doc.Text);
        Assert.Empty(session.Regions);
        Assert.Equal(OneConflict, session.ComposeOutput());
        Assert.Same(region.Original, region.Original);
    }

    [AvaloniaFact]
    public void ComposeOutput_preserves_crlf_and_missing_final_newline()
    {
        const string text = "top\r\n<<<<<<< a\r\nours\r\n=======\r\ntheirs\r\n>>>>>>> b";
        var (doc, session) = Make(text);
        session.Activate(Region(doc), s => s.SetHunk(ConflictSide.Ours, true));
        Assert.Equal("top\r\nours\r\ntheirs", doc.Text);
        Assert.Equal("top\r\nours", session.ComposeOutput());

        var (doc2, session2) = Make(text);
        session2.Activate(Region(doc2), s => s.SetHunk(ConflictSide.Theirs, true));
        Assert.Equal("top\r\ntheirs\r\nours", doc2.Text);
        Assert.Equal("top\r\ntheirs", session2.ComposeOutput());

        var (doc3, session3) = Make(OneConflict.Replace("\n", "\r\n"));
        session3.Activate(Region(doc3), s => s.SetHunk(ConflictSide.Ours, true));
        Assert.Equal("top\r\nours 1\r\nours 2\r\nbottom\r\n", session3.ComposeOutput());
    }

    [AvaloniaFact]
    public void Spans_match_document_lines()
    {
        const string two = OneConflict + "<<<<<<< HEAD\nours 3\n=======\ntheirs 3\ntheirs 4\n>>>>>>> branch\n";
        var (doc, session) = Make(two);
        session.Activate(Region(doc, 1), s => s.SetHunk(ConflictSide.Theirs, true));
        session.Activate(Region(doc, 0), s => s.SetHunk(ConflictSide.Ours, true));

        var spans = session.Spans;
        Assert.Equal(2, spans.Count);
        Assert.True(spans[0].StartLine < spans[1].StartLine);
        foreach (var span in spans)
        {
            var region = session.RegionAt(span.StartLine)!;
            Assert.Equal(region.EndLine - region.StartLine + 1, span.Lines.Count);
            for (int i = 0; i < span.Lines.Count; i++)
            {
                var origin = span.Lines[i];
                Assert.NotEqual(ConflictRowOrigin.Added, origin.Origin);
                Assert.Equal(doc.GetText(doc.GetLineByNumber(span.StartLine + i + 1)), origin.SourceText);
            }
        }

        Assert.Equal(new[] { "theirs 3", "theirs 4", "ours 3" }, spans[1].Lines.Select(l => l.SourceText));
        var parse = ConflictMarkerParser.Parse(doc.Text);
        Assert.Equal(parse.LineCount, ConflictPaneBuilder.Build(parse, ConflictSide.Ours, spans).Count);
    }

    [AvaloniaFact]
    public void Activate_empty_ours_inserts_empty_deletion_row()
    {
        const string text = "top\n<<<<<<< HEAD\n=======\ntheirs 1\n>>>>>>> branch\nbottom\n";
        var (doc, session) = Make(text);
        var region = session.Activate(Region(doc), s => s.SetHunk(ConflictSide.Ours, true))!;

        Assert.Equal("top\n\ntheirs 1\nbottom\n", doc.Text);
        Assert.Equal(1, region.StartLine);
        Assert.Equal(2, region.EndLine);
        Assert.True(session.IsDeletionRow(1));
        Assert.False(session.IsDeletionRow(2));
        Assert.Equal(ConflictToggleGlyph.Checked, session.RowGlyph(1));
        Assert.Equal(ConflictToggleGlyph.Unchecked, session.RowGlyph(2));
        Assert.False(session.IsExcluded(1));
        Assert.True(session.IsExcluded(2));
        Assert.True((session.RowVisual(1) & ConflictRowVisual.Deletion) != 0);
        Assert.False((session.RowVisual(1) & ConflictRowVisual.Excluded) != 0);
        Assert.True((session.RowVisual(2) & ConflictRowVisual.Excluded) != 0);
    }

    [AvaloniaFact]
    public void Deletion_row_is_never_composed()
    {
        const string text = "top\n<<<<<<< HEAD\n=======\ntheirs 1\n>>>>>>> branch\nbottom\n";

        // 1. Deletion checked alone -> region saves no lines
        var (doc1, session1) = Make(text);
        session1.Activate(Region(doc1), s => s.SetHunk(ConflictSide.Ours, true));
        Assert.Equal("top\nbottom\n", session1.ComposeOutput());

        // 2. Checked with theirs -> only theirs lines saved
        var (doc2, session2) = Make(text);
        session2.Activate(Region(doc2), s =>
        {
            s.SetHunk(ConflictSide.Ours, true);
            s.SetHunk(ConflictSide.Theirs, true);
        });
        Assert.Equal("top\ntheirs 1\nbottom\n", session2.ComposeOutput());

        // 3. Unchecked with theirs -> only theirs lines saved
        var (doc3, session3) = Make(text);
        session3.Activate(Region(doc3), s => s.SetHunk(ConflictSide.Theirs, true));
        Assert.Equal("top\ntheirs 1\nbottom\n", session3.ComposeOutput());
    }

    [AvaloniaFact]
    public void Deletion_row_is_read_only()
    {
        const string text = "top\n<<<<<<< HEAD\n=======\ntheirs 1\n>>>>>>> branch\nbottom\n";
        var (doc, session) = Make(text);
        session.Activate(Region(doc), s => s.SetHunk(ConflictSide.Ours, true));

        var line = doc.GetLineByNumber(2); // line 1 (0-based) is deletion row
        Assert.False(session.CanInsert(line.Offset));
        Assert.False(session.CanInsert(line.EndOffset));

        var seg = new SimpleSegment(line.Offset, line.TotalLength);
        Assert.Empty(session.GetDeletableSegments(seg));
    }

    [AvaloniaFact]
    public void Deletion_checked_region_is_not_counted_as_emptied()
    {
        const string text = "top\n<<<<<<< HEAD\n=======\ntheirs 1\n>>>>>>> branch\nbottom\n";
        var (doc, session) = Make(text);
        var region = session.Activate(Region(doc), s => s.SetHunk(ConflictSide.Ours, true))!;

        Assert.Equal(0, session.EmptyRegionCount);

        session.ToggleHunk(region, ConflictSide.Ours);
        Assert.Equal(1, session.EmptyRegionCount);
    }

    [AvaloniaFact]
    public void Toggling_deletion_row_changes_no_text_and_undoes()
    {
        const string text = "top\n<<<<<<< HEAD\n=======\ntheirs 1\n>>>>>>> branch\nbottom\n";
        var (doc, session) = Make(text);
        var region = session.Activate(Region(doc), s => s.SetHunk(ConflictSide.Ours, true))!;

        string initialText = doc.Text;
        Assert.False(session.IsExcluded(1));

        session.ToggleRow(1);
        Assert.Equal(initialText, doc.Text);
        Assert.True(session.IsExcluded(1));

        doc.UndoStack.Undo();
        Pump();
        Assert.Equal(initialText, doc.Text);
        Assert.False(session.IsExcluded(1));

        doc.UndoStack.Redo();
        Pump();
        Assert.Equal(initialText, doc.Text);
        Assert.True(session.IsExcluded(1));
    }

    [AvaloniaFact]
    public void Freeze_removes_deletion_row()
    {
        const string text = "top\n<<<<<<< HEAD\n=======\ntheirs 1\n>>>>>>> branch\nbottom\n";
        var (doc, session) = Make(text);
        session.Activate(Region(doc), s =>
        {
            s.SetHunk(ConflictSide.Ours, true);
            s.SetHunk(ConflictSide.Theirs, true);
        });

        // Break the region by deleting a line of theirs row
        var theirsLine = doc.GetLineByNumber(3);
        doc.Remove(theirsLine.Offset, theirsLine.TotalLength);
        Pump();

        // After freezing, the deletion placeholder line was removed
        Assert.Empty(session.Regions);
        Assert.Equal("top\nbottom\n", doc.Text);
        Assert.Equal(doc.Text, session.ComposeOutput());
    }

    [AvaloniaFact]
    public void Freeze_keeps_deletion_row_line_holding_text()
    {
        const string text = "top\n<<<<<<< HEAD\n=======\ntheirs 1\n>>>>>>> branch\nbottom\n";
        var (doc, session) = Make(text);
        session.Activate(Region(doc), s =>
        {
            s.SetHunk(ConflictSide.Ours, true);
            s.SetHunk(ConflictSide.Theirs, true);
        });

        // The document API bypasses the read-only sections, so text can reach the deletion row.
        doc.Insert(doc.GetLineByNumber(2).Offset, "typed");
        Pump();
        Assert.Single(session.Regions);

        var theirsLine = Line(doc, "theirs 1");
        doc.Remove(theirsLine.Offset, theirsLine.TotalLength);
        Pump();

        Assert.Empty(session.Regions);
        Assert.Equal("top\ntyped\nbottom\n", doc.Text);
        Assert.Equal(doc.Text, session.ComposeOutput());
    }

    [AvaloniaFact]
    public void Deletion_row_as_last_line_without_final_newline_composes_without_trailing_break()
    {
        const string text = "top\n<<<<<<< HEAD\n=======\ntheirs\n>>>>>>> b";
        var (doc, session) = Make(text);
        session.Activate(Region(doc), s => s.SetHunk(ConflictSide.Ours, true));

        Assert.Equal("top", session.ComposeOutput());
    }
}
