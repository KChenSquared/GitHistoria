namespace Historia.Tests;

public class ConflictRegionAnalyzerTests
{
    [Fact]
    public void Identical_ours_theirs_lines_produce_no_spans()
    {
        string text = "<<<<<<< HEAD\nhello world\n=======\nhello world\n>>>>>>> feature";
        var parse = ConflictMarkerParser.Parse(text);
        Assert.Single(parse.Regions);

        var analysis = ConflictRegionAnalyzer.Analyze(parse.Regions[0], text);

        Assert.Single(analysis.OursHighlights);
        Assert.Single(analysis.TheirsHighlights);
        Assert.Empty(analysis.OursHighlights[0].Spans);
        Assert.Empty(analysis.TheirsHighlights[0].Spans);
    }

    [Fact]
    public void One_word_change_produces_span_on_exactly_that_word()
    {
        string text = "<<<<<<< HEAD\nhello world\n=======\nhello there\n>>>>>>> feature";
        var parse = ConflictMarkerParser.Parse(text);
        Assert.Single(parse.Regions);

        var analysis = ConflictRegionAnalyzer.Analyze(parse.Regions[0], text);

        Assert.Single(analysis.OursHighlights);
        Assert.Single(analysis.TheirsHighlights);

        Assert.Single(analysis.OursHighlights[0].Spans);
        var oursSpan = analysis.OursHighlights[0].Spans[0];
        Assert.Equal(6, oursSpan.Start);
        Assert.Equal(5, oursSpan.Length); // "world"

        Assert.Single(analysis.TheirsHighlights[0].Spans);
        var theirsSpan = analysis.TheirsHighlights[0].Spans[0];
        Assert.Equal(6, theirsSpan.Start);
        Assert.Equal(5, theirsSpan.Length); // "there"
    }

    [Fact]
    public void Base_changed_flags_with_zdiff3_fixture()
    {
        string text = "<<<<<<< HEAD\nint a = 99;\nint b = 2;\n||||||| base\nint a = 1;\nint b = 2;\n=======\nint a = 1;\nint b = 42;\n>>>>>>> feature";
        var parse = ConflictMarkerParser.Parse(text);
        Assert.Single(parse.Regions);
        var region = parse.Regions[0];
        Assert.True(region.HasBase);

        var analysis = ConflictRegionAnalyzer.Analyze(region, text);

        // Ours changed line 0 ("int a = 99;"), kept line 1 ("int b = 2;")
        Assert.Equal(2, analysis.OursHighlights.Count);
        Assert.True(analysis.OursHighlights[0].ChangedFromBase);
        Assert.False(analysis.OursHighlights[1].ChangedFromBase);

        // Theirs kept line 0 ("int a = 1;"), changed line 1 ("int b = 42;")
        Assert.Equal(2, analysis.TheirsHighlights.Count);
        Assert.False(analysis.TheirsHighlights[0].ChangedFromBase);
        Assert.True(analysis.TheirsHighlights[1].ChangedFromBase);
    }
}
