namespace Historia.Tests;

public class ConflictPaneBuilderTests
{
    private const string TwoVersusOne =
        "common top\n<<<<<<< HEAD\nours line 1\nours line 2\n=======\ntheirs line 1\n>>>>>>> feature\ncommon bottom";

    private const string WithBase =
        "a\n<<<<<<< HEAD\nours\n||||||| base\nbase 1\nbase 2\n=======\ntheirs\n>>>>>>> b\nz";

    [Fact]
    public void Ours_and_theirs_start_on_the_first_content_row()
    {
        var parse = ConflictMarkerParser.Parse(TwoVersusOne);

        var ours = ConflictPaneBuilder.Build(parse, ConflictSide.Ours);
        var theirs = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs);

        Assert.Equal(ConflictPaneLineType.Common, ours[0].Type);
        Assert.Equal("common top", theirs[0].Text);
        Assert.Equal(ConflictPaneLineType.Marker, ours[1].Type);
        Assert.Equal(ConflictPaneLineType.Marker, theirs[1].Type);
        Assert.Equal(string.Empty, ours[1].Text);
        Assert.Null(ours[1].LineNumber);

        Assert.Equal(ConflictPaneLineType.Ours, ours[2].Type);
        Assert.Equal("ours line 1", ours[2].Text);
        Assert.Equal(2, ours[2].LineNumber);
        Assert.Equal(ConflictPaneLineType.Ours, ours[3].Type);
        Assert.Equal("ours line 2", ours[3].Text);
        Assert.Equal(3, ours[3].LineNumber);

        Assert.Equal(ConflictPaneLineType.Theirs, theirs[2].Type);
        Assert.Equal("theirs line 1", theirs[2].Text);
        Assert.Equal(2, theirs[2].LineNumber);

        Assert.Equal(ConflictPaneLineType.Common, ours[7].Type);
        Assert.Equal(4, ours[7].LineNumber);
        Assert.Equal(3, theirs[7].LineNumber);
    }

    [Fact]
    public void Shorter_side_pads_below()
    {
        var parse = ConflictMarkerParser.Parse(TwoVersusOne);

        var ours = ConflictPaneBuilder.Build(parse, ConflictSide.Ours);
        var theirs = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs);

        Assert.Equal(
            [ConflictPaneLineType.Marker, ConflictPaneLineType.Ours, ConflictPaneLineType.Ours, ConflictPaneLineType.Padding, ConflictPaneLineType.Padding, ConflictPaneLineType.Marker],
            ours.Skip(1).Take(6).Select(l => l.Type));
        Assert.Equal(
            [ConflictPaneLineType.Marker, ConflictPaneLineType.Theirs, ConflictPaneLineType.Padding, ConflictPaneLineType.Padding, ConflictPaneLineType.Padding, ConflictPaneLineType.Marker],
            theirs.Skip(1).Take(6).Select(l => l.Type));
        Assert.All(ours.Skip(4).Take(2).Concat(theirs.Skip(3).Take(3)), l =>
        {
            Assert.Equal(string.Empty, l.Text);
            Assert.Null(l.LineNumber);
        });
    }

    [Fact]
    public void Region_keeps_its_row_count()
    {
        foreach (var text in new[] { TwoVersusOne, WithBase })
        {
            var parse = ConflictMarkerParser.Parse(text);
            var region = Assert.Single(parse.Regions);

            foreach (var side in new[] { ConflictSide.Ours, ConflictSide.Theirs })
            {
                var pane = ConflictPaneBuilder.Build(parse, side);

                Assert.Equal(parse.LineCount, pane.Count);
                Assert.Equal(ConflictPaneLineType.Marker, pane[region.StartLine].Type);
                Assert.Equal(ConflictPaneLineType.Marker, pane[region.EndLine].Type);
                Assert.Equal(region.StartLine, pane[region.StartLine].DocumentLineIndex);
                Assert.Equal(region.EndLine, pane[region.EndLine].DocumentLineIndex);
                Assert.Equal(region.EndLine - region.StartLine + 1, pane.Skip(region.StartLine).Take(region.EndLine - region.StartLine + 1).Count());
            }
        }
    }

    [Fact]
    public void Side_rows_carry_their_source_result_line()
    {
        var parse = ConflictMarkerParser.Parse(TwoVersusOne);
        var region = Assert.Single(parse.Regions);

        var ours = ConflictPaneBuilder.Build(parse, ConflictSide.Ours);
        var theirs = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs);

        // Row 2 and 3 of the ours pane are the ours lines; the theirs pane's row 2 is the theirs
        // line, whose source is further down the Result.
        Assert.Equal(region.OursStartLine, ours[2].DocumentLineIndex);
        Assert.Equal(region.OursStartLine + 1, ours[3].DocumentLineIndex);
        Assert.Equal(region.TheirsStartLine, theirs[2].DocumentLineIndex);
        Assert.NotEqual(2, theirs[2].DocumentLineIndex);

        // A padding row carries the Result line at its own position.
        Assert.Equal(ConflictPaneLineType.Padding, theirs[3].Type);
        Assert.Equal(3, theirs[3].DocumentLineIndex);
        Assert.Equal(4, theirs[4].DocumentLineIndex);
    }

    [Fact]
    public void Base_section_is_padding_in_both_panes()
    {
        var parse = ConflictMarkerParser.Parse(WithBase);
        var region = Assert.Single(parse.Regions);
        Assert.True(region.HasBase);

        var ours = ConflictPaneBuilder.Build(parse, ConflictSide.Ours);
        var theirs = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs);

        foreach (var pane in new[] { ours, theirs })
        {
            Assert.Equal(parse.LineCount, pane.Count);
            Assert.Equal(ConflictPaneLineType.Marker, pane[region.StartLine].Type);
            Assert.Equal(ConflictPaneLineType.Marker, pane[region.EndLine].Type);
            for (var row = region.StartLine + 2; row < region.EndLine; row++)
            {
                Assert.Equal(ConflictPaneLineType.Padding, pane[row].Type);
                Assert.Equal(string.Empty, pane[row].Text);
            }
        }

        Assert.Equal("ours", ours[2].Text);
        Assert.Equal("theirs", theirs[2].Text);
        Assert.Equal(region.TheirsStartLine, theirs[2].DocumentLineIndex);
        Assert.Equal(ConflictPaneLineType.Common, ours[9].Type);
        Assert.Equal(3, ours[9].LineNumber);
        Assert.Equal(3, theirs[9].LineNumber);
    }

    [Fact]
    public void Lines_outside_regions_stay_one_to_one()
    {
        var parse = ConflictMarkerParser.Parse("one\ntwo\n<<<<<<< HEAD\no\n=======\nt\n>>>>>>> b\nthree\nfour");

        foreach (var side in new[] { ConflictSide.Ours, ConflictSide.Theirs })
        {
            var pane = ConflictPaneBuilder.Build(parse, side);
            foreach (var row in new[] { 0, 1, 7, 8 })
            {
                Assert.Equal(ConflictPaneLineType.Common, pane[row].Type);
                Assert.Equal(row, pane[row].DocumentLineIndex);
                Assert.Equal(parse.Lines[row], pane[row].Text);
            }
        }
    }

    private static ConflictLineOrigin O(int i, string t) => new(ConflictRowOrigin.Ours, i, t);
    private static ConflictLineOrigin T(int i, string t) => new(ConflictRowOrigin.Theirs, i, t);
    private static ConflictLineOrigin A() => new(ConflictRowOrigin.Added, -1, string.Empty);

    [Fact]
    public void Managed_span_rows_are_top_aligned()
    {
        // Lines 1-4 are one managed region: a theirs row first, then an ours row, a typed line and
        // a second ours row (edited in the document).
        var parse = ConflictMarkerParser.Parse("top\ntheirs a\nours a EDITED\ntyped\nours b\nbottom");
        var span = new ConflictManagedSpan(1, [T(0, "theirs a"), O(0, "ours a"), A(), O(1, "ours b")]);

        var ours = ConflictPaneBuilder.Build(parse, ConflictSide.Ours, [span]);
        var theirs = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs, [span]);

        Assert.Equal(parse.LineCount, ours.Count);
        Assert.Equal(parse.LineCount, theirs.Count);

        Assert.Equal(
            [ConflictPaneLineType.Common, ConflictPaneLineType.Ours, ConflictPaneLineType.Ours, ConflictPaneLineType.Padding, ConflictPaneLineType.Padding, ConflictPaneLineType.Common],
            ours.Select(l => l.Type));
        Assert.Equal("ours a", ours[1].Text); // the side's original text, not the edited line
        Assert.Equal("ours b", ours[2].Text);
        Assert.Equal(2, ours[1].DocumentLineIndex); // the rows' current Result lines
        Assert.Equal(4, ours[2].DocumentLineIndex);

        Assert.Equal(
            [ConflictPaneLineType.Common, ConflictPaneLineType.Theirs, ConflictPaneLineType.Padding, ConflictPaneLineType.Padding, ConflictPaneLineType.Padding, ConflictPaneLineType.Common],
            theirs.Select(l => l.Type));
        Assert.Equal("theirs a", theirs[1].Text);
        Assert.Equal(1, theirs[1].DocumentLineIndex);

        Assert.Equal(new int?[] { 1, 2, 3, null, null, 4 }, ours.Select(l => l.LineNumber).ToArray());
        Assert.Equal(new int?[] { 1, 2, null, null, null, 3 }, theirs.Select(l => l.LineNumber).ToArray());
        Assert.Equal(3, ours[3].DocumentLineIndex); // padding carries its positional line
    }

    [Fact]
    public void Added_lines_pad_both_sides()
    {
        var parse = ConflictMarkerParser.Parse("top\nours a\ntyped\nbottom");
        var span = new ConflictManagedSpan(1, [O(0, "ours a"), A()]);

        var ours = ConflictPaneBuilder.Build(parse, ConflictSide.Ours, [span]);
        var theirs = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs, [span]);

        Assert.Equal(ConflictPaneLineType.Ours, ours[1].Type);
        Assert.Equal(ConflictPaneLineType.Padding, ours[2].Type);
        Assert.Null(ours[2].LineNumber);
        Assert.Equal(ConflictPaneLineType.Padding, theirs[1].Type);
        Assert.Equal(ConflictPaneLineType.Padding, theirs[2].Type);
    }

    [Fact]
    public void Null_overlay_matches_two_argument_build()
    {
        string[] fixtures =
        [
            TwoVersusOne,
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
    public void Side_line_numbers_continue_through_managed_spans()
    {
        var parse = ConflictMarkerParser.Parse("top\nours a\ntheirs a\ntheirs b\nbottom");
        var span = new ConflictManagedSpan(1, [O(0, "ours a"), T(0, "theirs a"), T(1, "theirs b")]);

        var ours = ConflictPaneBuilder.Build(parse, ConflictSide.Ours, [span]);
        var theirs = ConflictPaneBuilder.Build(parse, ConflictSide.Theirs, [span]);

        Assert.Equal(new int?[] { 1, 2, null, null, 3 }, ours.Select(l => l.LineNumber).ToArray());
        Assert.Equal(new int?[] { 1, 2, 3, null, 4 }, theirs.Select(l => l.LineNumber).ToArray());
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
        Assert.Equal(ConflictPaneLineType.Padding, ours[2].Type);
        Assert.Equal("theirs 1", theirs[1].Text);
        Assert.Equal(ConflictPaneLineType.Padding, theirs[2].Type);
        Assert.Equal(ConflictPaneLineType.Marker, ours[4].Type);
        Assert.Equal("ours 2", ours[5].Text);
        Assert.Equal(ConflictPaneLineType.Padding, ours[7].Type);
        Assert.Equal("theirs 2", theirs[5].Text);
        Assert.Equal(7, theirs[5].DocumentLineIndex);
        Assert.Equal(ConflictPaneLineType.Padding, theirs[7].Type);
        Assert.Equal(ConflictPaneLineType.Common, ours[9].Type);
        Assert.Equal(5, ours[9].LineNumber); // top, ours 1, middle, ours 2, bottom
        Assert.Equal(5, theirs[9].LineNumber); // top, theirs 1, middle, theirs 2, bottom
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
        Assert.Equal("", ours[4].Text); // padded below the single ours line
        Assert.Equal("theirs", theirs[2].Text);
        Assert.Equal("", theirs[4].Text);
    }
}
