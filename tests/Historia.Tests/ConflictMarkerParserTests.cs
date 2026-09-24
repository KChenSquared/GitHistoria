namespace Historia.Tests;

public class ConflictMarkerParserTests
{
    [Fact]
    public void Merge_style_parses_cleanly()
    {
        string text = "common 1\n<<<<<<< HEAD\nours 1\nours 2\n=======\ntheirs 1\n>>>>>>> feature\ncommon 2";
        var parse = ConflictMarkerParser.Parse(text);

        Assert.False(parse.IsMalformed);
        Assert.Empty(parse.MalformedAt);
        Assert.Single(parse.Regions);

        var r = parse.Regions[0];
        Assert.Equal(1, r.StartLine);
        Assert.Equal(6, r.EndLine);
        Assert.Equal("HEAD", r.OursLabel);
        Assert.Equal("feature", r.TheirsLabel);
        Assert.False(r.HasBase);
        Assert.Equal(["ours 1", "ours 2"], r.OursLines);
        Assert.Equal(["theirs 1"], r.TheirsLines);
        Assert.Null(r.BaseLines);
        Assert.Equal(8, parse.LineCount);
    }

    [Fact]
    public void Diff3_and_zdiff3_base_captured()
    {
        string text = "<<<<<<< HEAD\nours\n||||||| 1234567 base\nbase text\n=======\ntheirs\n>>>>>>> feature";
        var parse = ConflictMarkerParser.Parse(text);

        Assert.False(parse.IsMalformed);
        Assert.Single(parse.Regions);

        var r = parse.Regions[0];
        Assert.True(r.HasBase);
        Assert.Equal("1234567 base", r.BaseLabel);
        Assert.Equal(["base text"], r.BaseLines);
        Assert.Equal(["ours"], r.OursLines);
        Assert.Equal(["theirs"], r.TheirsLines);
    }

    [Fact]
    public void Crlf_file_line_indices_ignore_cr()
    {
        string text = "line 0\r\n<<<<<<< HEAD\r\nours\r\n=======\r\ntheirs\r\n>>>>>>> branch\r\nline 6";
        var parse = ConflictMarkerParser.Parse(text);

        Assert.False(parse.IsMalformed);
        Assert.Single(parse.Regions);

        var r = parse.Regions[0];
        Assert.Equal(1, r.StartLine);
        Assert.Equal(5, r.EndLine);
        Assert.Equal("HEAD", r.OursLabel);
        Assert.Equal(["ours"], r.OursLines);
        Assert.Equal(["theirs"], r.TheirsLines);
        Assert.Equal(7, parse.LineCount);
    }

    [Fact]
    public void Lone_cr_counts_as_line_break()
    {
        string text = "line 0\r<<<<<<< HEAD\rours\r=======\rtheirs\r>>>>>>> branch\rline 6";
        var parse = ConflictMarkerParser.Parse(text);

        Assert.False(parse.IsMalformed);
        Assert.Single(parse.Regions);

        var r = parse.Regions[0];
        Assert.Equal(1, r.StartLine);
        Assert.Equal(5, r.EndLine);
        Assert.Equal(7, parse.LineCount);
    }

    [Fact]
    public void Longer_marker_run_is_content_when_size_is_7()
    {
        string text = "<<<<<<<< 8-markers-content\nhello world";
        var parse = ConflictMarkerParser.Parse(text, markerSize: 7);

        Assert.Empty(parse.Regions);
        Assert.False(parse.IsMalformed);
        Assert.Equal(2, parse.LineCount);
    }

    [Fact]
    public void Custom_size_10_parses_when_10_characters()
    {
        string text = "<<<<<<<<<< HEAD\nours\n==========\ntheirs\n>>>>>>>>>> branch";

        var parse7 = ConflictMarkerParser.Parse(text, markerSize: 7);
        Assert.Empty(parse7.Regions);

        var parse10 = ConflictMarkerParser.Parse(text, markerSize: 10);
        Assert.Single(parse10.Regions);
        Assert.Equal("HEAD", parse10.Regions[0].OursLabel);
        Assert.Equal("branch", parse10.Regions[0].TheirsLabel);
    }

    [Fact]
    public void Labels_captured_properly()
    {
        string text = "<<<<<<<  branch-name (ours) \nours\n=======\ntheirs\n>>>>>>> branch-name (theirs)";
        var parse = ConflictMarkerParser.Parse(text);

        Assert.Single(parse.Regions);
        Assert.Equal(" branch-name (ours) ", parse.Regions[0].OursLabel);
        Assert.Equal("branch-name (theirs)", parse.Regions[0].TheirsLabel);
    }

    [Fact]
    public void Two_adjacent_regions_parse_sequentially()
    {
        string text = "<<<<<<< A\nours 1\n=======\ntheirs 1\n>>>>>>> B\n<<<<<<< C\nours 2\n=======\ntheirs 2\n>>>>>>> D";
        var parse = ConflictMarkerParser.Parse(text);

        Assert.False(parse.IsMalformed);
        Assert.Equal(2, parse.Regions.Count);
        Assert.Equal(0, parse.Regions[0].StartLine);
        Assert.Equal(4, parse.Regions[0].EndLine);
        Assert.Equal(5, parse.Regions[1].StartLine);
        Assert.Equal(9, parse.Regions[1].EndLine);
    }

    [Fact]
    public void Unterminated_region_marked_malformed()
    {
        string text = "preamble\n<<<<<<< HEAD\nours\n=======\ntheirs";
        var parse = ConflictMarkerParser.Parse(text);

        Assert.True(parse.IsMalformed);
        Assert.Equal([1], parse.MalformedAt);
        Assert.Empty(parse.Regions);
    }

    [Fact]
    public void Region_missing_separator_is_malformed_and_subsequent_region_still_parses()
    {
        string text = "<<<<<<< bad\nmissing separator\n>>>>>>> bad\n<<<<<<< good\nours\n=======\ntheirs\n>>>>>>> good";
        var parse = ConflictMarkerParser.Parse(text);

        Assert.True(parse.IsMalformed);
        Assert.Equal([0], parse.MalformedAt);
        Assert.Single(parse.Regions);
        Assert.Equal("good", parse.Regions[0].OursLabel);
        Assert.Equal(3, parse.Regions[0].StartLine);
    }

    [Fact]
    public void Lines_and_delimiters_round_trip_crlf_lf_and_lone_cr()
    {
        string text = "first line\r\nsecond line\nthird line\rlast line without newline";
        var parse = ConflictMarkerParser.Parse(text);

        Assert.Equal(4, parse.LineCount);
        Assert.Equal(["first line", "second line", "third line", "last line without newline"], parse.Lines);
        Assert.Equal(["\r\n", "\n", "\r", ""], parse.Delimiters);

        var reconstructed = string.Concat(parse.Lines.Zip(parse.Delimiters, (line, delim) => line + delim));
        Assert.Equal(text, reconstructed);

        // Also test with trailing newline
        string textWithTrailing = "a\r\nb\n";
        var parseTrailing = ConflictMarkerParser.Parse(textWithTrailing);
        var reconstructedTrailing = string.Concat(parseTrailing.Lines.Zip(parseTrailing.Delimiters, (line, delim) => line + delim));
        Assert.Equal(textWithTrailing, reconstructedTrailing);
    }

    [Fact]
    public void Parse_honours_marker_size_9()
    {
        string text = "<<<<<<<<< HEAD\nours\n=========\ntheirs\n>>>>>>>>> branch\n";
        var parse7 = ConflictMarkerParser.Parse(text, markerSize: 7);
        Assert.Empty(parse7.Regions);

        var parse9 = ConflictMarkerParser.Parse(text, markerSize: 9);
        Assert.Single(parse9.Regions);
        Assert.Equal("HEAD", parse9.Regions[0].OursLabel);
        Assert.Equal("branch", parse9.Regions[0].TheirsLabel);
    }
}
