namespace Historia.Tests;

[Trait("tier", "unit")]

public class UnifiedDiffParserTests
{
    private const string Patch =
        "diff --git a/f.txt b/f.txt\n" +
        "index 111..222 100644\n" +
        "--- a/f.txt\n" +
        "+++ b/f.txt\n" +
        "@@ -1,3 +1,4 @@\n" +
        " context one\n" +
        "-removed line\n" +
        "+added line A\n" +
        "+added line B\n" +
        " context two\n";

    [Fact]
    public void Parses_single_hunk_with_correct_line_numbers()
    {
        var hunks = UnifiedDiffParser.Parse(Patch);
        var hunk = Assert.Single(hunks);

        Assert.Equal(1, hunk.OldStart);
        Assert.Equal(1, hunk.NewStart);

        var types = hunk.Lines.Select(l => l.Type).ToArray();
        Assert.Equal(
            [DiffLineType.Context, DiffLineType.Deletion, DiffLineType.Addition, DiffLineType.Addition, DiffLineType.Context],
            types);

        var firstContext = hunk.Lines[0];
        Assert.Equal(1, firstContext.OldLineNumber);
        Assert.Equal(1, firstContext.NewLineNumber);

        var deletion = hunk.Lines[1];
        Assert.Equal(2, deletion.OldLineNumber);
        Assert.Null(deletion.NewLineNumber);

        var addition = hunk.Lines[2];
        Assert.Null(addition.OldLineNumber);
        Assert.Equal(2, addition.NewLineNumber);
    }

    [Fact]
    public void Empty_patch_yields_no_hunks()
        => Assert.Empty(UnifiedDiffParser.Parse(""));

    [Fact]
    public void Preamble_before_first_hunk_is_ignored()
    {
        var hunks = UnifiedDiffParser.Parse(Patch);
        Assert.DoesNotContain(hunks.SelectMany(h => h.Lines), l => l.Content.StartsWith("diff --git"));
    }
}
