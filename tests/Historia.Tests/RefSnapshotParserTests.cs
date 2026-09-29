namespace Historia.Tests;

/// <summary>
/// <see cref="ForEachRefParser"/> reads NUL-delimited <c>for-each-ref</c> output exactly as git
/// wrote it (<see cref="GitCliRequest.RawStandardOutput"/>): each record ends in <c>\n</c>, and a
/// tag message keeps its own line breaks, CRLF and lone CR included. <see cref="CatFileParser"/>
/// reads the <c>cat-file</c> output the scan falls back to when a ref's object is missing.
/// </summary>
[Trait("tier", "unit")]
public sealed class RefSnapshotParserTests
{
    private const string Commit = "19aedeebdf3e9a82a19b9e3848360b798fe3901a";
    private const string TagObj = "ea7bb757e4da92cd30321a9c587e05fc926c633b";

    private static string Record(params string[] fields)
    {
        Assert.Equal(ForEachRefParser.FieldCount, fields.Length);
        return string.Concat(fields.Select(f => f + "\0")) + "\n";
    }

    [Fact]
    public void Parses_branches_symrefs_and_both_tag_kinds_sorted_by_name()
    {
        var stdout =
            Record("refs/tags/v1", TagObj, "tag", Commit, "commit", "", "", "Tëster Ü", "1700000000 -0130", "line one\r\n\r\nline three\r\n")
            + Record("refs/heads/main", Commit, "commit", "", "", "", "origin/main", "", "", "")
            + Record("refs/remotes/origin/HEAD", Commit, "commit", "", "", "refs/remotes/origin/main", "", "", "", "")
            + Record("refs/tags/light-ünï", Commit, "commit", "", "", "", "", "", "", "");

        var refs = ForEachRefParser.Parse(stdout);

        Assert.Equal(
            ["refs/heads/main", "refs/remotes/origin/HEAD", "refs/tags/light-ünï", "refs/tags/v1"],
            refs.Select(r => r.CanonicalName));

        var main = refs[0];
        Assert.Equal(new GitObjectId(Commit), main.Target);
        Assert.Equal(new GitObjectId(Commit), main.PeeledCommit);
        Assert.Null(main.SymrefTarget);
        Assert.Equal("origin/main", main.Upstream);
        Assert.False(main.IsAnnotatedTag);

        Assert.Equal("refs/remotes/origin/main", refs[1].SymrefTarget);

        var light = refs[2];
        Assert.False(light.IsAnnotatedTag);
        Assert.Null(light.Message);
        Assert.Null(light.TaggerName);

        var annotated = refs[3];
        Assert.True(annotated.IsAnnotatedTag);
        Assert.Equal(new GitObjectId(TagObj), annotated.Target);
        Assert.Equal(new GitObjectId(Commit), annotated.PeeledCommit);
        Assert.Equal("Tëster Ü", annotated.TaggerName);
        Assert.Equal("line one\r\n\r\nline three\r\n", annotated.Message);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 20, 43, 20, TimeSpan.FromMinutes(-90)), annotated.TaggerWhen);
        Assert.Equal(TimeSpan.FromMinutes(-90), annotated.TaggerWhen!.Value.Offset);
    }

    [Fact]
    public void A_ref_to_a_tree_reaches_no_commit()
    {
        var refs = ForEachRefParser.Parse(Record("refs/tags/tree", "4b825dc642cb6eb9a060e54bf8d69288fbee4904", "tree", "", "", "", "", "", "", ""));
        Assert.Null(Assert.Single(refs).PeeledCommit);
    }

    [Fact]
    public void Empty_output_is_an_empty_list() =>
        Assert.Empty(ForEachRefParser.Parse(""));

    [Fact]
    public void A_truncated_record_is_rejected_not_dropped()
    {
        var full = Record("refs/heads/main", Commit, "commit", "", "", "", "", "", "", "");
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
    [InlineData("99999999999999 +0000")]
    [InlineData("1700000000 +9900")]
    public void Malformed_tagger_dates_are_null(string raw) =>
        Assert.Null(ForEachRefParser.ParseRawDate(raw));

    [Fact]
    public void Tag_message_keeps_lf_crlf_and_lone_cr()
    {
        const string message = "plain\ncrlf\r\nlone\rend\n";
        var refs = ForEachRefParser.Parse(
            Record("refs/tags/v1", TagObj, "tag", Commit, "commit", "", "", "T", "1700000000 +0000", message));
        Assert.Equal(message, Assert.Single(refs).Message);
    }

    [Fact]
    public void The_object_free_listing_parses_names_ids_and_symrefs()
    {
        var stdout = $"refs/heads/main\0{Commit}\0\0origin/main\0\nrefs/remotes/origin/HEAD\0{Commit}\0refs/remotes/origin/main\0\0\n";
        List<(string Name, string ObjectName, string? SymrefTarget, string? Upstream)> expected =
            [("refs/heads/main", Commit, null, "origin/main"), ("refs/remotes/origin/HEAD", Commit, "refs/remotes/origin/main", null)];
        Assert.Equal(expected, ForEachRefParser.ParseList(stdout));
    }

    [Fact]
    public void Batch_check_answers_in_input_order_with_missing_as_null()
    {
        var answers = CatFileParser.ParseBatchCheck($"{Commit} commit\n1111111111111111111111111111111111111111 missing\n{TagObj}^{{}} missing\n", 3);
        List<(GitObjectId? Id, string? Type)> expected = [(new GitObjectId(Commit), "commit"), (null, null), (null, null)];
        Assert.Equal(expected, answers);
    }

    [Fact]
    public void A_short_batch_check_answer_is_rejected() =>
        Assert.Throws<FormatException>(() => CatFileParser.ParseBatchCheck($"{Commit} commit\n", 2));

    private static byte[] BatchRecord(string sha, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return [.. Encoding.UTF8.GetBytes($"{sha} {type} {bytes.Length}\n"), .. bytes, (byte)'\n'];
    }

    [Fact]
    public void Tag_objects_give_tagger_date_and_the_verbatim_body()
    {
        var crlfTag = $"object {Commit}\ntype commit\ntag crlf\ntagger Tëster Ü <t@example.com> 1700000000 -0130\n\nsubject\r\n\r\nbody\r\n";
        var noTagger = $"object {Commit}\ntype commit\ntag old\n\nold style\n";
        var empty = $"object {Commit}\ntype commit\ntag empty\ntagger A <a@b> 1700000000 +0000\n";
        const string second = "0123456789abcdef0123456789abcdef01234567";
        const string third = "89abcdef0123456789abcdef0123456789abcdef";
        byte[] stdout = [
            .. BatchRecord(TagObj, "tag", crlfTag),
            .. Encoding.UTF8.GetBytes("1111111111111111111111111111111111111111 missing\n"),
            .. BatchRecord(second, "tag", noTagger),
            .. BatchRecord(third, "tag", empty),
        ];

        var tags = CatFileParser.ParseTagObjects(stdout);

        Assert.Equal(3, tags.Count);
        var crlf = tags[new GitObjectId(TagObj)];
        Assert.Equal("Tëster Ü", crlf.TaggerName);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 20, 43, 20, TimeSpan.FromMinutes(-90)), crlf.TaggerWhen);
        Assert.Equal("subject\r\n\r\nbody\r\n", crlf.Message);

        var old = tags[new GitObjectId(second)];
        Assert.Null(old.TaggerName);
        Assert.Null(old.TaggerWhen);
        Assert.Equal("old style\n", old.Message);

        Assert.Equal("", tags[new GitObjectId(third)].Message);
    }

    [Fact]
    public void A_batch_record_running_past_the_output_is_rejected()
    {
        var record = BatchRecord(TagObj, "tag", "object x\n\nmsg\n");
        Assert.Throws<FormatException>(() => CatFileParser.ParseTagObjects(record.AsSpan(0, record.Length - 5)));
    }
}
