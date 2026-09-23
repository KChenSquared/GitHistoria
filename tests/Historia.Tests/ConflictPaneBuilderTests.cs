namespace Historia.Tests;

public class ConflictPaneBuilderTests
{
    [Fact]
    public void All_builds_match_line_count_and_align_1_to_1()
    {
        string conflictText = "common top\n<<<<<<< HEAD\nours line 1\nours line 2\n=======\ntheirs line 1\n>>>>>>> feature\ncommon bottom";
        var parse = ConflictMarkerParser.Parse(conflictText);

        var oursLines = ConflictPaneBuilder.Build(parse, conflictText, ConflictSide.Ours);
        var theirsLines = ConflictPaneBuilder.Build(parse, conflictText, ConflictSide.Theirs);

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
        var resolvedPane = ConflictPaneBuilder.Build(resolvedParse, resolvedText, ConflictSide.Ours);

        Assert.Equal(resolvedParse.LineCount, resolvedPane.Count);
        Assert.All(resolvedPane, line => Assert.Equal(ConflictPaneLineType.Common, line.Type));
        Assert.Equal("resolved line", resolvedPane[1].Text);
    }
}
