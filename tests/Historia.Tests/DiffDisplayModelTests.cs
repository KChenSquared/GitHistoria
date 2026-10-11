namespace Historia.Tests;

public class DiffDisplayModelTests
{
    private static FileDiff CreateSampleDiff()
    {
        var hunk = new DiffHunk
        {
            OldStart = 1,
            OldLineCount = 3,
            NewStart = 1,
            NewLineCount = 3,
            Header = "@@ -1,3 +1,3 @@",
            Lines =
            [
                new DiffLine
                {
                    Type = DiffLineType.Context,
                    Content = "context before",
                    OldLineNumber = 1,
                    NewLineNumber = 1,
                },
                new DiffLine
                {
                    Type = DiffLineType.Deletion,
                    Content = "hello old world",
                    OldLineNumber = 2,
                    NewLineNumber = null,
                },
                new DiffLine
                {
                    Type = DiffLineType.Addition,
                    Content = "hello new world",
                    OldLineNumber = null,
                    NewLineNumber = 2,
                },
                new DiffLine
                {
                    Type = DiffLineType.Context,
                    Content = "context after",
                    OldLineNumber = 3,
                    NewLineNumber = 3,
                },
            ],
        };

        return new FileDiff
        {
            Path = "test.txt",
            Kind = DiffKind.Modified,
            IsBinary = false,
            IsTruncated = false,
            Hunks = [hunk],
            AddedLines = 1,
            DeletedLines = 1,
        };
    }

    [Fact]
    public void Combined_row_count_equals_headers_plus_lines()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);

        // 1 indicator (header) + 4 lines = 5 combined rows
        Assert.Equal(5, model.CombinedRows.Count);
        Assert.Equal(DiffDisplayLineType.Indicator, model.CombinedRows[0].Type);
        Assert.Equal(DiffDisplayLineType.Context, model.CombinedRows[1].Type);
        Assert.Equal(DiffDisplayLineType.Deletion, model.CombinedRows[2].Type);
        Assert.Equal(DiffDisplayLineType.Addition, model.CombinedRows[3].Type);
        Assert.Equal(DiffDisplayLineType.Context, model.CombinedRows[4].Type);
    }

    [Fact]
    public void Side_by_side_sides_equal_length_with_padding_in_the_right_places()
    {
        var hunk = new DiffHunk
        {
            OldStart = 1,
            OldLineCount = 3,
            NewStart = 1,
            NewLineCount = 4,
            Header = "@@ -1,3 +1,4 @@",
            Lines =
            [
                new DiffLine
                {
                    Type = DiffLineType.Deletion,
                    Content = "del 1",
                    OldLineNumber = 1,
                    NewLineNumber = null,
                },
                new DiffLine
                {
                    Type = DiffLineType.Addition,
                    Content = "add 1",
                    OldLineNumber = null,
                    NewLineNumber = 1,
                },
                new DiffLine
                {
                    Type = DiffLineType.Addition,
                    Content = "add 2",
                    OldLineNumber = null,
                    NewLineNumber = 2,
                },
                new DiffLine
                {
                    Type = DiffLineType.Context,
                    Content = "ctx",
                    OldLineNumber = 2,
                    NewLineNumber = 3,
                },
            ],
        };

        var diff = new FileDiff
        {
            Path = "asym.txt",
            Kind = DiffKind.Modified,
            IsBinary = false,
            IsTruncated = false,
            Hunks = [hunk],
            AddedLines = 2,
            DeletedLines = 1,
        };

        var model = DiffDisplayModel.Create(diff);

        // Both sides must have equal length
        Assert.Equal(model.SideBySideOldRows.Count, model.SideBySideNewRows.Count);

        // Indicator at 0
        Assert.Equal(DiffDisplayLineType.Indicator, model.SideBySideOldRows[0].Type);
        Assert.Equal(DiffDisplayLineType.Indicator, model.SideBySideNewRows[0].Type);

        // Row 1: del 1 on left, add 1 on right
        Assert.Equal(DiffDisplayLineType.Deletion, model.SideBySideOldRows[1].Type);
        Assert.Equal(DiffDisplayLineType.Addition, model.SideBySideNewRows[1].Type);

        // Row 2: padding (None) on left, add 2 on right
        Assert.Equal(DiffDisplayLineType.None, model.SideBySideOldRows[2].Type);
        Assert.Equal(DiffDisplayLineType.Addition, model.SideBySideNewRows[2].Type);

        // Row 3: ctx on both
        Assert.Equal(DiffDisplayLineType.Context, model.SideBySideOldRows[3].Type);
        Assert.Equal(DiffDisplayLineType.Context, model.SideBySideNewRows[3].Type);
    }

    [Fact]
    public void Paired_lines_get_inline_spans_on_the_changed_word_only()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);

        var delRow = model.CombinedRows[2];
        var addRow = model.CombinedRows[3];

        Assert.NotEmpty(delRow.Highlights);
        Assert.NotEmpty(addRow.Highlights);

        // "hello old world" vs "hello new world" -> "old" (pos 6, len 3) and "new" (pos 6, len 3)
        var delHl = delRow.Highlights[0];
        Assert.Equal(6, delHl.Start);
        Assert.Equal(3, delHl.Length);
        Assert.Equal("old", delRow.Content.Substring(delHl.Start, delHl.Length));

        var addHl = addRow.Highlights[0];
        Assert.Equal(6, addHl.Start);
        Assert.Equal(3, addHl.Length);
        Assert.Equal("new", addRow.Content.Substring(addHl.Start, addHl.Length));
    }

    [Fact]
    public void Lone_carriage_return_replaced_and_unescaped()
    {
        var rawWithCr = "line with lone\rreturn";
        var sanitized = DiffDisplayModel.SanitizeLoneCarriageReturn(rawWithCr);

        Assert.DoesNotContain('\r', sanitized);
        Assert.Contains(DiffDisplayModel.CarriageReturnSymbol, sanitized);
        Assert.Equal("line with lone\u240Dreturn", sanitized);

        var unescaped = DiffDisplayModel.UnescapeCarriageReturn(sanitized);
        Assert.Equal(rawWithCr, unescaped);
    }

    [Fact]
    public void Blocks_boundaries_correctly_identified()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);

        // Combined lines: 1 (indicator), 2 (context), 3 (del), 4 (add), 5 (context)
        // Change block is lines 3..4 (1-based)
        Assert.Single(model.CombinedBlocks);
        var block = model.CombinedBlocks[0];
        Assert.Equal(3, block.StartLine);
        Assert.Equal(4, block.EndLine);
        Assert.True(block.Contains(3));
        Assert.True(block.Contains(4));
        Assert.False(block.Contains(2));
        Assert.False(block.Contains(5));
    }

    [Fact]
    public void Row_to_DiffLineRef_round_trip()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);

        // CombinedRows[0] is Indicator (no line ref)
        Assert.False(model.CombinedRowToRefMap.ContainsKey(0));

        // CombinedRows[1] is context (hunk 0, line 0)
        Assert.Equal(new DiffLineRef(0, 0), model.CombinedRowToRefMap[1]);

        // CombinedRows[2] is deletion (hunk 0, line 1)
        Assert.Equal(new DiffLineRef(0, 1), model.CombinedRowToRefMap[2]);

        // CombinedRows[3] is addition (hunk 0, line 2)
        Assert.Equal(new DiffLineRef(0, 2), model.CombinedRowToRefMap[3]);

        // CombinedRows[4] is context (hunk 0, line 3)
        Assert.Equal(new DiffLineRef(0, 3), model.CombinedRowToRefMap[4]);

        // Selection mapping for combined: lines 2..4 (context + del + add) -> should return del and add refs only
        var selected = model.GetSelectedLinesForCombined(2, 4);
        Assert.Equal(2, selected.Count);
        Assert.Equal(new DiffLineRef(0, 1), selected[0]);
        Assert.Equal(new DiffLineRef(0, 2), selected[1]);
    }

    [Fact]
    public void Sbs_right_pane_selection_returns_additions_only()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);

        var selected = model.GetSelectedLinesForSideBySide(1, model.SideBySideNewRows.Count, oldSide: false);
        Assert.Single(selected);
        Assert.Equal(new DiffLineRef(0, 2), selected[0]);
    }

    [Fact]
    public void Sbs_left_pane_selection_returns_deletions_only()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);

        var selected = model.GetSelectedLinesForSideBySide(1, model.SideBySideOldRows.Count, oldSide: true);
        Assert.Single(selected);
        Assert.Equal(new DiffLineRef(0, 1), selected[0]);
    }

    [Fact]
    public void SelectedLines_are_unique_and_in_row_order()
    {
        var diff = CreateSampleDiff();
        var model = DiffDisplayModel.Create(diff);

        // Combined selection spanning indicator (line 1), context (line 2), del (3), add (4), context (5)
        var combined = model.GetSelectedLinesForCombined(1, 5);
        Assert.Equal(2, combined.Count);
        Assert.Equal(new DiffLineRef(0, 1), combined[0]);
        Assert.Equal(new DiffLineRef(0, 2), combined[1]);

        // SBS old side spanning context, deletion, padding, context
        var sbsOld = model.GetSelectedLinesForSideBySide(1, model.SideBySideOldRows.Count, oldSide: true);
        Assert.Single(sbsOld);
        Assert.Equal(new DiffLineRef(0, 1), sbsOld[0]);

        // SBS new side spanning context, padding, addition, context
        var sbsNew = model.GetSelectedLinesForSideBySide(1, model.SideBySideNewRows.Count, oldSide: false);
        Assert.Single(sbsNew);
        Assert.Equal(new DiffLineRef(0, 2), sbsNew[0]);
    }

    [Fact]
    public void SelectedLines_over_20k_changed_rows_is_linear()
    {
        int lineCount = 20_000;
        var lines = new List<DiffLine>(lineCount);
        for (int i = 0; i < lineCount; i++)
        {
            lines.Add(new DiffLine
            {
                Type = DiffLineType.Addition,
                Content = $"line {i}",
                OldLineNumber = null,
                NewLineNumber = i + 1,
            });
        }
        var hunk = new DiffHunk
        {
            OldStart = 0,
            OldLineCount = 0,
            NewStart = 1,
            NewLineCount = lineCount,
            Header = $"@@ -0,0 +1,{lineCount} @@",
            Lines = lines,
        };
        var diff = new FileDiff
        {
            Path = "large.txt",
            Kind = DiffKind.Added,
            IsBinary = false,
            IsTruncated = false,
            AddedLines = lineCount,
            DeletedLines = 0,
            Hunks = [hunk],
        };
        var model = DiffDisplayModel.Create(diff);

        var sw = Stopwatch.StartNew();
        var selected = model.GetSelectedLinesForCombined(1, model.CombinedRows.Count);
        sw.Stop();

        Assert.Equal(lineCount, selected.Count);
        Assert.True(sw.ElapsedMilliseconds < 250, $"Tripwire triggered: selection took {sw.ElapsedMilliseconds} ms (bound: 250 ms)");
    }

    private static FileDiff TwoHunkDiff()
    {
        static DiffLine Ctx(int o, int n) => new() { Type = DiffLineType.Context, Content = "c", OldLineNumber = o, NewLineNumber = n };

        var first = new DiffHunk
        {
            OldStart = 1,
            OldLineCount = 2,
            NewStart = 1,
            NewLineCount = 2,
            Header = "@@ -1,2 +1,2 @@",
            Lines =
            [
                Ctx(1, 1),
                new DiffLine { Type = DiffLineType.Deletion, Content = "old", OldLineNumber = 2, NewLineNumber = null },
                new DiffLine { Type = DiffLineType.Addition, Content = "new", OldLineNumber = null, NewLineNumber = 2 },
            ],
        };
        var second = new DiffHunk
        {
            OldStart = 20,
            OldLineCount = 3,
            NewStart = 20,
            NewLineCount = 2,
            Header = "@@ -20,3 +20,2 @@",
            Lines =
            [
                Ctx(20, 20),
                new DiffLine { Type = DiffLineType.Deletion, Content = "gone", OldLineNumber = 21, NewLineNumber = null },
                Ctx(22, 21),
            ],
        };

        return new FileDiff
        {
            Path = "two.txt",
            Kind = DiffKind.Modified,
            IsBinary = false,
            IsTruncated = false,
            AddedLines = 1,
            DeletedLines = 2,
            Hunks = [first, second],
        };
    }

    [Fact]
    public void FindDisplayLineForNewLine_returns_the_exact_row_in_both_modes()
    {
        var model = DiffDisplayModel.Create(TwoHunkDiff());

        // Rows: 1 header, 2 ctx(1), 3 del, 4 add(2), 5 header, 6 ctx(20), 7 del, 8 ctx(21).
        Assert.Equal(4, model.FindDisplayLineForNewLine(2, sideBySide: false));
        Assert.Equal(8, model.FindDisplayLineForNewLine(21, sideBySide: false));

        // Side-by-side pairs the deletion with the addition on one row.
        var sbsAdd = model.FindDisplayLineForNewLine(2, sideBySide: true)!.Value;
        Assert.Equal(2, model.SideBySideNewRows[sbsAdd - 1].NewLineNumber);
        var sbsLast = model.FindDisplayLineForNewLine(21, sideBySide: true)!.Value;
        Assert.Equal(21, model.SideBySideNewRows[sbsLast - 1].NewLineNumber);
    }

    [Fact]
    public void FindDisplayLineForNewLine_falls_back_to_the_nearest_numbered_row()
    {
        var model = DiffDisplayModel.Create(TwoHunkDiff());

        Assert.Equal(4, model.FindDisplayLineForNewLine(5, sideBySide: false));
        Assert.Equal(6, model.FindDisplayLineForNewLine(18, sideBySide: false));
        Assert.Equal(8, model.FindDisplayLineForNewLine(500, sideBySide: false));
        Assert.Equal(2, model.FindDisplayLineForNewLine(1, sideBySide: false));
    }

    [Fact]
    public void FindDisplayLineForNewLine_prefers_the_earlier_row_on_a_tie()
    {
        var model = DiffDisplayModel.Create(TwoHunkDiff());

        // Line 11 is nine from new line 2 (row 4) and nine from new line 20 (row 6).
        Assert.Equal(4, model.FindDisplayLineForNewLine(11, sideBySide: false));
    }

    [Fact]
    public void FindDisplayLineForNewLine_handles_a_deletion_only_diff_and_an_empty_model()
    {
        var deletionOnly = new FileDiff
        {
            Path = "d.txt",
            Kind = DiffKind.Deleted,
            IsBinary = false,
            IsTruncated = false,
            AddedLines = 0,
            DeletedLines = 1,
            Hunks =
            [
                new DiffHunk
                {
                    OldStart = 1, OldLineCount = 1, NewStart = 0, NewLineCount = 0, Header = "@@ -1 +0,0 @@",
                    Lines = [new DiffLine { Type = DiffLineType.Deletion, Content = "x", OldLineNumber = 1, NewLineNumber = null }],
                },
            ],
        };
        var empty = new FileDiff
        {
            Path = "e.txt",
            Kind = DiffKind.Modified,
            IsBinary = false,
            IsTruncated = false,
            AddedLines = 0,
            DeletedLines = 0,
            Hunks = [],
        };

        Assert.Null(DiffDisplayModel.Create(deletionOnly).FindDisplayLineForNewLine(1, sideBySide: false));
        Assert.Null(DiffDisplayModel.Create(empty).FindDisplayLineForNewLine(1, sideBySide: true));
    }
}
