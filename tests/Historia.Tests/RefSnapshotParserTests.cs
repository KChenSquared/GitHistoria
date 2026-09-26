namespace Historia.Tests;

/// <summary>
/// <see cref="ForEachRefParser"/> reads NUL-delimited <c>for-each-ref</c> output as
/// <see cref="GitCliService"/> hands it back: captured line by line and rejoined with CRLF, so a
/// record's trailing newline and every line break in a tag message arrive as <c>\r\n</c>.
/// </summary>
[Trait("tier", "unit")]
public sealed class RefSnapshotParserTests
{
    private const string Commit = "19aedeebdf3e9a82a19b9e3848360b798fe3901a";
    private const string TagObj = "ea7bb757e4da92cd30321a9c587e05fc926c633b";

    private static string Record(params string[] fields)
    {
        Assert.Equal(ForEachRefParser.FieldCount, fields.Length);
        return string.Concat(fields.Select(f => f + "\0")) + "\r\n";
    }

    [Fact]
    public void Parses_branches_symrefs_and_both_tag_kinds_sorted_by_name()
    {
        var stdout =
            Record("refs/tags/v1", TagObj, "tag", Commit, "commit", "", "Tëster Ü", "1700000000 -0130", "line one\r\n\r\nline three\r\n")
            + Record("refs/heads/main", Commit, "commit", "", "", "", "", "", "")
            + Record("refs/remotes/origin/HEAD", Commit, "commit", "", "", "refs/remotes/origin/main", "", "", "")
            + Record("refs/tags/light-ünï", Commit, "commit", "", "", "", "", "", "");

        var refs = ForEachRefParser.Parse(stdout);

        Assert.Equal(
            ["refs/heads/main", "refs/remotes/origin/HEAD", "refs/tags/light-ünï", "refs/tags/v1"],
            refs.Select(r => r.CanonicalName));

        var main = refs[0];
        Assert.Equal(new ObjectId(Commit), main.Target);
        Assert.Equal(new ObjectId(Commit), main.PeeledCommit);
        Assert.Null(main.SymrefTarget);
        Assert.False(main.IsAnnotatedTag);

        Assert.Equal("refs/remotes/origin/main", refs[1].SymrefTarget);

        var light = refs[2];
        Assert.False(light.IsAnnotatedTag);
        Assert.Null(light.Message);
        Assert.Null(light.TaggerName);

        var annotated = refs[3];
        Assert.True(annotated.IsAnnotatedTag);
        Assert.Equal(new ObjectId(TagObj), annotated.Target);
        Assert.Equal(new ObjectId(Commit), annotated.PeeledCommit);
        Assert.Equal("Tëster Ü", annotated.TaggerName);
        Assert.Equal("line one\n\nline three\n", annotated.Message);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 20, 43, 20, TimeSpan.FromMinutes(-90)), annotated.TaggerWhen);
        Assert.Equal(TimeSpan.FromMinutes(-90), annotated.TaggerWhen!.Value.Offset);
    }

    [Fact]
    public void A_ref_to_a_tree_reaches_no_commit()
    {
        var refs = ForEachRefParser.Parse(Record("refs/tags/tree", "4b825dc642cb6eb9a060e54bf8d69288fbee4904", "tree", "", "", "", "", "", ""));
        Assert.Null(Assert.Single(refs).PeeledCommit);
    }

    [Fact]
    public void Empty_output_is_an_empty_list() =>
        Assert.Empty(ForEachRefParser.Parse(""));

    [Fact]
    public void A_truncated_record_is_rejected_not_dropped()
    {
        var full = Record("refs/heads/main", Commit, "commit", "", "", "", "", "", "");
        var truncated = full + "refs/heads/next\0" + Commit + "\0commit\0";
        Assert.Throws<FormatException>(() => ForEachRefParser.Parse(truncated));
    }

    [Theory]
    [InlineData("1700000000 +0000", 0)]
    [InlineData("1700000000 +0530", 330)]
    [InlineData("1700000000 -0800", -480)]
    public void Raw_tagger_dates_keep_their_offset(string raw, int offsetMinutes)
    {
        var when = ForEachRefParser.ParseRawDate(raw);
        Assert.NotNull(when);
        Assert.Equal(1700000000, when!.Value.ToUnixTimeSeconds());
        Assert.Equal(TimeSpan.FromMinutes(offsetMinutes), when.Value.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a date")]
    [InlineData("1700000000 0100")]
    public void Malformed_tagger_dates_are_null(string raw) =>
        Assert.Null(ForEachRefParser.ParseRawDate(raw));
}
