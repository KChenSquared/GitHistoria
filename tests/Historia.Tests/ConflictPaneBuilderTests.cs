namespace Historia.Tests;

public class ConflictPaneBuilderTests
{
    [Fact]
    public void All_builds_match_line_count_and_align_1_to_1()
    {
        string conflictText = "common top\n<<<<<<< HEAD\nours line 1\nours line 2\n=======\ntheirs line 1\n>>>>>>> feature\ncommon bottom";
        var parse = ConflictMarkerParser.Parse(conflictText);

        var oursLines = ConflictPaneBuilder.Build(parse, ConflictSide.Ours);
        var theirsLines = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs);

        // Build 1 and Build 2 have exact document line count (8 lines)
        Assert.Equal(parse.LineCount, oursLines.Count);
        Assert.Equal(parse.LineCount, theirsLines.Count);

        // Common lines identical on both sides
        Assert.Equal(ConflictPaneLineType.Common, oursLines[0].Type);
        Assert.Equal(ConflictPaneLineType.Common, theirsLines[0].Type);
        Assert.Equal("common top", oursLines[0].Text);
        Assert.Equal("common top", theirsLines[0].Text);
        Assert.Equal(1, oursLines[0].LineNumber);
        Assert.Equal(1, theirsLines[0].LineNumber);

        // Markers are blank on both sides
        Assert.Equal(ConflictPaneLineType.Marker, oursLines[1].Type);
        Assert.Equal(string.Empty, oursLines[1].Text);
        Assert.Null(oursLines[1].LineNumber);

        Assert.Equal(ConflictPaneLineType.Marker, theirsLines[1].Type);
        Assert.Equal(string.Empty, theirsLines[1].Text);
        Assert.Null(theirsLines[1].LineNumber);

        // Ours pane shows ours lines, Theirs pane pads them
        Assert.Equal(ConflictPaneLineType.Ours, oursLines[2].Type);
        Assert.Equal("ours line 1", oursLines[2].Text);
        Assert.Equal(2, oursLines[2].LineNumber);

        Assert.Equal(ConflictPaneLineType.Padding, theirsLines[2].Type);
        Assert.Equal(string.Empty, theirsLines[2].Text);
        Assert.Null(theirsLines[2].LineNumber);

        Assert.Equal(ConflictPaneLineType.Ours, oursLines[3].Type);
        Assert.Equal("ours line 2", oursLines[3].Text);
        Assert.Equal(3, oursLines[3].LineNumber);

        Assert.Equal(ConflictPaneLineType.Padding, theirsLines[3].Type);
        Assert.Equal(string.Empty, theirsLines[3].Text);
        Assert.Null(theirsLines[3].LineNumber);

        // Separator
        Assert.Equal(ConflictPaneLineType.Marker, oursLines[4].Type);
        Assert.Equal(ConflictPaneLineType.Marker, theirsLines[4].Type);

        // Theirs pane shows theirs lines, Ours pane pads them
        Assert.Equal(ConflictPaneLineType.Padding, oursLines[5].Type);
        Assert.Equal(string.Empty, oursLines[5].Text);
        Assert.Null(oursLines[5].LineNumber);

        Assert.Equal(ConflictPaneLineType.Theirs, theirsLines[5].Type);
        Assert.Equal("theirs line 1", theirsLines[5].Text);
        Assert.Equal(2, theirsLines[5].LineNumber);

        // End marker
        Assert.Equal(ConflictPaneLineType.Marker, oursLines[6].Type);
        Assert.Equal(ConflictPaneLineType.Marker, theirsLines[6].Type);

        // Common bottom
        Assert.Equal(ConflictPaneLineType.Common, oursLines[7].Type);
        Assert.Equal(ConflictPaneLineType.Common, theirsLines[7].Type);
        Assert.Equal("common bottom", oursLines[7].Text);
        Assert.Equal("common bottom", theirsLines[7].Text);
        Assert.Equal(4, oursLines[7].LineNumber);
        Assert.Equal(3, theirsLines[7].LineNumber);

        // Build 3: After replacing region with plain lines, region disappears and lines become Common
        string resolvedText = "common top\nresolved line\ncommon bottom";
        var resolvedParse = ConflictMarkerParser.Parse(resolvedText);
        var resolvedPane = ConflictPaneBuilder.Build(resolvedParse, ConflictSide.Ours);

        Assert.Equal(resolvedParse.LineCount, resolvedPane.Count);
        Assert.All(resolvedPane, line => Assert.Equal(ConflictPaneLineType.Common, line.Type));
        Assert.Equal("resolved line", resolvedPane[1].Text);
    }

    [Fact]
    public void Build_uses_parse_lines_without_text()
    {
        string text = "top\n<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> branch\nbottom";
        var parse = ConflictMarkerParser.Parse(text);

        var ours = ConflictPaneBuilder.Build(parse, ConflictSide.Ours);
        var theirs = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs);

        Assert.Equal(parse.Lines.Count, ours.Count);
        Assert.Equal("top", ours[0].Text);
        Assert.Equal("ours", ours[2].Text);
        Assert.Equal("", ours[4].Text); // theirs line padded in ours
        Assert.Equal("theirs", theirs[4].Text);
    }

    private static ConflictLineOrigin O(int i, string t) => new(ConflictRowOrigin.Ours, i, t);
    private static ConflictLineOrigin T(int i, string t) => new(ConflictRowOrigin.Theirs, i, t);
    private static ConflictLineOrigin A() => new(ConflictRowOrigin.Added, -1, string.Empty);

    [Fact]
    public void Managed_span_rows_show_on_their_side_and_pad_the_other()
    {
        // Lines 1-3 are a managed region: theirs row, then two ours rows (one edited in the document).
        var parse = ConflictMarkerParser.Parse("top\ntheirs a\nours a EDITED\nours b\nbottom");
        var span = new ConflictManagedSpan(1, [T(0, "theirs a"), O(0, "ours a"), O(1, "ours b")]);

        var ours = ConflictPaneBuilder.Build(parse, ConflictSide.Ours, [span]);
        var theirs = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs, [span]);

        Assert.Equal(ConflictPaneLineType.Padding, ours[1].Type);
        Assert.Equal(ConflictPaneLineType.Ours, ours[2].Type);
        Assert.Equal("ours a", ours[2].Text); // the side's original text, not the edited line
        Assert.Equal("ours b", ours[3].Text);
        Assert.Equal(ConflictPaneLineType.Theirs, theirs[1].Type);
        Assert.Equal("theirs a", theirs[1].Text);
        Assert.Equal(ConflictPaneLineType.Padding, theirs[2].Type);
        Assert.Equal(ConflictPaneLineType.Padding, theirs[3].Type);
        Assert.Equal(2, ours[2].DocumentLineIndex);
    }

    [Fact]
    public void Added_lines_pad_both_sides()
    {
        var parse = ConflictMarkerParser.Parse("top\nours a\ntyped\nbottom");
        var span = new ConflictManagedSpan(1, [O(0, "ours a"), A()]);

        var ours = ConflictPaneBuilder.Build(parse, ConflictSide.Ours, [span]);
        var theirs = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs, [span]);

        Assert.Equal(ConflictPaneLineType.Padding, ours[2].Type);
        Assert.Null(ours[2].LineNumber);
        Assert.Equal(ConflictPaneLineType.Padding, theirs[2].Type);
    }

    [Fact]
    public void Side_line_numbers_continue_through_managed_spans()
    {
        var parse = ConflictMarkerParser.Parse("top\nours a\ntheirs a\ntheirs b\nbottom");
        var span = new ConflictManagedSpan(1, [O(0, "ours a"), T(0, "theirs a"), T(1, "theirs b")]);

        var ours = ConflictPaneBuilder.Build(parse, ConflictSide.Ours, [span]);
        var theirs = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs, [span]);

        Assert.Equal(new int?[] { 1, 2, null, null, 3 }, ours.Select(l => l.LineNumber).ToArray());
        Assert.Equal(new int?[] { 1, null, 2, 3, 4 }, theirs.Select(l => l.LineNumber).ToArray());
    }

    [Fact]
    public void Null_overlay_matches_two_argument_build()
    {
        string[] fixtures =
        [
            "common top\n<<<<<<< HEAD\nours line 1\nours line 2\n=======\ntheirs line 1\n>>>>>>> feature\ncommon bottom",
            "a\n<<<<<<< HEAD\no\n||||||| base\nb\n=======\nt\n>>>>>>> x\nz\n",
            "no conflicts\nhere",
            "",
        ];
        foreach (var text in fixtures)
        {
            var parse = ConflictMarkerParser.Parse(text);
            foreach (var side in new[] { ConflictSide.Ours, ConflictSide.Theirs })
            {
                Assert.Equal(ConflictPaneBuilder.Build(parse, side), ConflictPaneBuilder.Build(parse, side, null));
                Assert.Equal(ConflictPaneBuilder.Build(parse, side), ConflictPaneBuilder.Build(parse, side, []));
            }
        }
    }

    [Fact]
    public void Mixed_marker_regions_and_managed_spans_stay_aligned()
    {
        // Line 1-2: managed region (ours row kept, theirs row struck); then a marker region.
        const string text = "top\nours 1\ntheirs 1\nmiddle\n<<<<<<< HEAD\nours 2\n=======\ntheirs 2\n>>>>>>> b\nbottom";
        var parse = ConflictMarkerParser.Parse(text);
        Assert.Single(parse.Regions);
        var span = new ConflictManagedSpan(1, [O(0, "ours 1"), T(0, "theirs 1")]);

        var ours = ConflictPaneBuilder.Build(parse, ConflictSide.Ours, [span]);
        var theirs = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs, [span]);

        Assert.Equal(parse.LineCount, ours.Count);
        Assert.Equal(parse.LineCount, theirs.Count);
        Assert.Equal(Enumerable.Range(0, parse.LineCount), ours.Select(l => l.DocumentLineIndex));
        Assert.Equal("ours 1", ours[1].Text);
        Assert.Equal("theirs 1", theirs[2].Text);
        Assert.Equal(ConflictPaneLineType.Marker, ours[4].Type);
        Assert.Equal("ours 2", ours[5].Text);
        Assert.Equal(ConflictPaneLineType.Padding, ours[7].Type);
        Assert.Equal("theirs 2", theirs[7].Text);
        Assert.Equal(ConflictPaneLineType.Common, ours[9].Type);
        Assert.Equal(5, ours[9].LineNumber); // top, ours 1, middle, ours 2, bottom
    }
}
