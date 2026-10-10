namespace Historia.Tests;

[Trait("tier", "unit")]
public sealed class ObjectFormatParsingTests
{
    private const string Sha1Commit1 = "19aedeebdf3e9a82a19b9e3848360b798fe3901a";
    private const string Sha1Commit2 = "ea7bb757e4da92cd30321a9c587e05fc926c633b";
    private const string Sha1Commit3 = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private const string Sha1Zero = "0000000000000000000000000000000000000000";

    private const string Sha256Commit1 = "a1b2c3d4e5f60718293a4b5c6d7e8f901a2b3c4d5e6f708192a3b4c5d6e7f801";
    private const string Sha256Commit2 = "b2c3d4e5f60718293a4b5c6d7e8f901a2b3c4d5e6f708192a3b4c5d6e7f801a2";
    private const string Sha256Commit3 = "c3d4e5f60718293a4b5c6d7e8f901a2b3c4d5e6f708192a3b4c5d6e7f801a2b3";
    private const string Sha256Zero = "0000000000000000000000000000000000000000000000000000000000000000";

    private const char LogFieldSep = '\x1f';

    private static string RefRecord(params string[] fields)
    {
        Assert.Equal(ForEachRefParser.FieldCount, fields.Length);
        return string.Concat(fields.Select(f => f + "\0")) + "\n";
    }

    private static byte[] CatFileBatchRecord(string sha, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        return [.. Encoding.UTF8.GetBytes($"{sha} {type} {bytes.Length}\n"), .. bytes, (byte)'\n'];
    }

    [Fact]
    public void GitLogStream_parses_sha256_root_commit()
    {
        var sha1Record = string.Join(LogFieldSep, Sha1Commit1, "", "2026-01-01T00:00:00-08:00", "2026-01-01T00:01:00-08:00",
            "Ada Lovelace", "ada@example.com", "root commit");
        var sha256Record = string.Join(LogFieldSep, Sha256Commit1, "", "2026-01-01T00:00:00-08:00", "2026-01-01T00:01:00-08:00",
            "Ada Lovelace", "ada@example.com", "root commit");

        var sha1Commit = GitLogStream.ParseRecord(sha1Record);
        var sha256Commit = GitLogStream.ParseRecord(sha256Record);

        Assert.Equal(20, sha1Commit.Id.HashSize);
        Assert.Equal(32, sha256Commit.Id.HashSize);
        Assert.Equal(Sha256Commit1, sha256Commit.Id.ToString());

        Assert.Empty(sha256Commit.ParentIds);
        Assert.Equal(sha1Commit.Metadata.ParentCount, sha256Commit.Metadata.ParentCount);
        Assert.Equal(sha1Commit.Metadata.MessageShort, sha256Commit.Metadata.MessageShort);
        Assert.Equal(sha1Commit.Metadata.AuthorName, sha256Commit.Metadata.AuthorName);
        Assert.Equal(sha1Commit.Metadata.AuthorEmail, sha256Commit.Metadata.AuthorEmail);
        Assert.Equal(sha1Commit.Metadata.AuthorWhen, sha256Commit.Metadata.AuthorWhen);
        Assert.Equal(sha1Commit.Metadata.CommitWhen, sha256Commit.Metadata.CommitWhen);
    }

    [Fact]
    public void GitLogStream_parses_sha256_merge_commit()
    {
        var sha1Record = string.Join(LogFieldSep, Sha1Commit3, $"{Sha1Commit1} {Sha1Commit2}", "2026-01-02T00:00:00Z", "2026-01-02T00:01:00Z",
            "Grace Hopper", "grace@example.com", "merge branch");
        var sha256Record = string.Join(LogFieldSep, Sha256Commit3, $"{Sha256Commit1} {Sha256Commit2}", "2026-01-02T00:00:00Z", "2026-01-02T00:01:00Z",
            "Grace Hopper", "grace@example.com", "merge branch");

        var sha1Commit = GitLogStream.ParseRecord(sha1Record);
        var sha256Commit = GitLogStream.ParseRecord(sha256Record);

        Assert.Equal(32, sha256Commit.Id.HashSize);
        Assert.Equal(sha1Commit.ParentIds.Count, sha256Commit.ParentIds.Count);
        Assert.Equal(32, sha256Commit.ParentIds[0].HashSize);
        Assert.Equal(32, sha256Commit.ParentIds[1].HashSize);
        Assert.Equal(Sha256Commit1, sha256Commit.ParentIds[0].ToString());
        Assert.Equal(Sha256Commit2, sha256Commit.ParentIds[1].ToString());
        Assert.Equal(sha1Commit.Metadata.ParentCount, sha256Commit.Metadata.ParentCount);
    }

    [Fact]
    public void CatFileParser_ParseBatchCheck_parses_sha256_records()
    {
        var stdoutSha1 = $"{Sha1Commit1} commit\n{Sha1Commit2} tree\n{Sha1Commit3}^{{}} missing\n";
        var stdoutSha256 = $"{Sha256Commit1} commit\n{Sha256Commit2} tree\n{Sha256Commit3}^{{}} missing\n";

        var answersSha1 = CatFileParser.ParseBatchCheck(stdoutSha1, 3);
        var answersSha256 = CatFileParser.ParseBatchCheck(stdoutSha256, 3);

        Assert.Equal(3, answersSha256.Count);
        Assert.Equal(32, answersSha256[0].Id!.Value.HashSize);
        Assert.Equal(Sha256Commit1, answersSha256[0].Id!.Value.ToString());
        Assert.Equal(answersSha1[0].Type, answersSha256[0].Type);

        Assert.Equal(32, answersSha256[1].Id!.Value.HashSize);
        Assert.Equal(Sha256Commit2, answersSha256[1].Id!.Value.ToString());
        Assert.Equal(answersSha1[1].Type, answersSha256[1].Type);

        Assert.Equal(answersSha1[2], answersSha256[2]);
    }

    [Fact]
    public void CatFileParser_ParseTagObjects_parses_sha256_records()
    {
        var crlfTag = $"object {Sha256Commit1}\ntype commit\ntag crlf\ntagger Tëster Ü <t@example.com> 1700000000 -0130\n\nsubject\r\n\r\nbody\r\n";
        byte[] stdout = [
            .. CatFileBatchRecord(Sha256Commit2, "tag", crlfTag),
            .. Encoding.UTF8.GetBytes($"{Sha256Commit3} missing\n"),
        ];

        var tags = CatFileParser.ParseTagObjects(stdout);

        var key = GitObjectId.Parse(Sha256Commit2);
        Assert.Equal(32, key.HashSize);
        Assert.True(tags.ContainsKey(key));

        var tag = tags[key];
        Assert.Equal("Tëster Ü", tag.TaggerName);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 20, 43, 20, TimeSpan.FromMinutes(-90)), tag.TaggerWhen);
        Assert.Equal("subject\r\n\r\nbody\r\n", tag.Message);
    }

    [Fact]
    public void ForEachRefParser_Parse_parses_sha256_refs_with_equivalent_structure()
    {
        var stdoutSha1 =
            RefRecord("refs/tags/v1", Sha1Commit2, "tag", Sha1Commit1, "commit", "", "", "Tëster Ü", "1700000000 -0130", "line one\r\n\r\nline three\r\n")
            + RefRecord("refs/heads/main", Sha1Commit1, "commit", "", "", "", "", "", "", "")
            + RefRecord("refs/remotes/origin/HEAD", Sha1Commit1, "commit", "", "", "refs/remotes/origin/main", "", "", "", "")
            + RefRecord("refs/tags/light-ünï", Sha1Commit1, "commit", "", "", "", "", "", "", "");

        var stdoutSha256 =
            RefRecord("refs/tags/v1", Sha256Commit2, "tag", Sha256Commit1, "commit", "", "", "Tëster Ü", "1700000000 -0130", "line one\r\n\r\nline three\r\n")
            + RefRecord("refs/heads/main", Sha256Commit1, "commit", "", "", "", "", "", "", "")
            + RefRecord("refs/remotes/origin/HEAD", Sha256Commit1, "commit", "", "", "refs/remotes/origin/main", "", "", "", "")
            + RefRecord("refs/tags/light-ünï", Sha256Commit1, "commit", "", "", "", "", "", "", "");

        var refsSha1 = ForEachRefParser.Parse(stdoutSha1);
        var refsSha256 = ForEachRefParser.Parse(stdoutSha256);

        Assert.Equal(refsSha1.Count, refsSha256.Count);
        Assert.Equal(refsSha1.Select(r => r.CanonicalName), refsSha256.Select(r => r.CanonicalName));

        var mainSha1 = refsSha1[0];
        var mainSha256 = refsSha256[0];
        Assert.Equal(20, mainSha1.Target!.Value.HashSize);
        Assert.Equal(32, mainSha256.Target!.Value.HashSize);
        Assert.Equal(Sha256Commit1, mainSha256.Target!.Value.ToString());
        Assert.Equal(32, mainSha256.PeeledCommit!.Value.HashSize);
        Assert.Equal(mainSha1.SymrefTarget, mainSha256.SymrefTarget);
        Assert.Equal(mainSha1.IsAnnotatedTag, mainSha256.IsAnnotatedTag);

        var symrefSha256 = refsSha256[1];
        Assert.Equal("refs/remotes/origin/main", symrefSha256.SymrefTarget);

        var lightSha256 = refsSha256[2];
        Assert.False(lightSha256.IsAnnotatedTag);
        Assert.Null(lightSha256.Message);

        var tagSha1 = refsSha1[3];
        var tagSha256 = refsSha256[3];
        Assert.True(tagSha256.IsAnnotatedTag);
        Assert.Equal(32, tagSha256.Target!.Value.HashSize);
        Assert.Equal(Sha256Commit2, tagSha256.Target!.Value.ToString());
        Assert.Equal(32, tagSha256.PeeledCommit!.Value.HashSize);
        Assert.Equal(Sha256Commit1, tagSha256.PeeledCommit!.Value.ToString());
        Assert.Equal(tagSha1.TaggerName, tagSha256.TaggerName);
        Assert.Equal(tagSha1.Message, tagSha256.Message);
        Assert.Equal(tagSha1.TaggerWhen, tagSha256.TaggerWhen);
    }

    [Fact]
    public void ForEachRefParser_ParseList_parses_sha256_names_and_ids()
    {
        var stdout = $"refs/heads/main\0{Sha256Commit1}\0\0\0\nrefs/remotes/origin/HEAD\0{Sha256Commit1}\0refs/remotes/origin/main\0\0\n";
        var parsed = ForEachRefParser.ParseList(stdout);

        List<(string Name, string ObjectName, string? SymrefTarget, string? Upstream)> expected =
            [("refs/heads/main", Sha256Commit1, null, null), ("refs/remotes/origin/HEAD", Sha256Commit1, "refs/remotes/origin/main", null)];
        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void ForEachRefParser_ParseId_accepts_both_widths()
    {
        var idSha1 = ForEachRefParser.ParseId(Sha1Commit1);
        Assert.NotNull(idSha1);
        Assert.Equal(20, idSha1!.Value.HashSize);
        Assert.Equal(Sha1Commit1, idSha1.Value.ToString());

        var idSha256 = ForEachRefParser.ParseId(Sha256Commit1);
        Assert.NotNull(idSha256);
        Assert.Equal(32, idSha256!.Value.HashSize);
        Assert.Equal(Sha256Commit1, idSha256.Value.ToString());

        Assert.Null(ForEachRefParser.ParseId(Sha256Commit1[..63]));
        Assert.Null(ForEachRefParser.ParseId(Sha256Commit1 + "a"));
        Assert.Null(ForEachRefParser.ParseId(new string('z', 64)));
    }

    [Fact]
    public void GitStatusParser_parses_sha256_porcelain_v2_status()
    {
        var inputSha1 = $"# branch.oid {Sha1Commit1}\0# branch.head main\01 A. N... 000000 100644 100644 {Sha1Zero} {Sha1Commit2} staged.txt\01 .M N... 100644 100644 100644 {Sha1Commit2} {Sha1Commit3} unstaged.txt\0";
        var inputSha256 = $"# branch.oid {Sha256Commit1}\0# branch.head main\01 A. N... 000000 100644 100644 {Sha256Zero} {Sha256Commit2} staged.txt\01 .M N... 100644 100644 100644 {Sha256Commit2} {Sha256Commit3} unstaged.txt\0";

        var parsedSha1 = GitStatusParser.Parse(inputSha1);
        var parsedSha256 = GitStatusParser.Parse(inputSha256);

        Assert.Equal(20, parsedSha1.HeadCommitId!.Value.HashSize);
        Assert.Equal(32, parsedSha256.HeadCommitId!.Value.HashSize);
        Assert.Equal(Sha256Commit1, parsedSha256.HeadCommitId.Value.ToString());

        Assert.Equal(parsedSha1.Staged.Count, parsedSha256.Staged.Count);
        Assert.Equal(parsedSha1.Staged[0].Path, parsedSha256.Staged[0].Path);
        Assert.Equal(parsedSha1.Staged[0].Kind, parsedSha256.Staged[0].Kind);

        Assert.Equal(parsedSha1.Unstaged.Count, parsedSha256.Unstaged.Count);
        Assert.Equal(parsedSha1.Unstaged[0].Path, parsedSha256.Unstaged[0].Path);
        Assert.Equal(parsedSha1.Unstaged[0].Kind, parsedSha256.Unstaged[0].Kind);
        Assert.Equal(parsedSha1.IsDirty, parsedSha256.IsDirty);
    }

    [Fact]
    public void GitStatusParser_EmptyBlobHash_recognizes_sha1_and_sha256()
    {
        const string sha1Empty = "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391";
        const string sha256Empty = "473a0f4c3be8a93681a267e3b1e9a7dcda1185436fe141f7749120a303721813";

        Assert.Equal(sha1Empty, GitStatusParser.EmptyBlobHash);
        Assert.Equal(sha256Empty, GitStatusParser.EmptyBlobSha256Hash);

        Assert.True(GitStatusParser.IsEmptyBlobHash(sha1Empty));
        Assert.True(GitStatusParser.IsEmptyBlobHash(sha1Empty.ToUpperInvariant()));
        Assert.True(GitStatusParser.IsEmptyBlobHash(sha256Empty));
        Assert.True(GitStatusParser.IsEmptyBlobHash(sha256Empty.ToUpperInvariant()));
        Assert.False(GitStatusParser.IsEmptyBlobHash(Sha256Commit1));

        Assert.True(GitStatusParser.IsEmptyBlobHash(GitObjectId.Parse(sha1Empty)));
        Assert.True(GitStatusParser.IsEmptyBlobHash(GitObjectId.Parse(sha256Empty)));
        Assert.False(GitStatusParser.IsEmptyBlobHash(GitObjectId.Parse(Sha256Commit1)));

        var input = $"1 .D N... 100644 100644 000000 {sha256Empty} {sha256Empty} empty.txt\0? new_empty.txt\0";
        var parsed = GitStatusParser.Parse(input);
        var hashes = new Dictionary<string, string> { ["new_empty.txt"] = sha256Empty };
        var status = GitStatusParser.PairExactMoves(parsed, hashes);

        Assert.Single(status.Unstaged, e => e.Path == "empty.txt" && e.Kind == FileChangeKind.Deleted);
        Assert.Single(status.Untracked, e => e.Path == "new_empty.txt");
    }

    [Fact]
    public void GitStatusParser_zero_oids_recognized_at_both_widths()
    {
        Assert.True(GitStatusParser.IsZeroOid(Sha1Zero));
        Assert.True(GitStatusParser.IsZeroOid(Sha256Zero));
        Assert.False(GitStatusParser.IsZeroOid(Sha1Commit1));
        Assert.False(GitStatusParser.IsZeroOid(Sha256Commit1));

        Assert.Null(GitStatusParser.ParseId(Sha1Zero));
        Assert.Null(GitStatusParser.ParseId(Sha256Zero));
        Assert.NotNull(GitStatusParser.ParseId(Sha1Commit1));
        Assert.NotNull(GitStatusParser.ParseId(Sha256Commit1));

        var parsedInitial = GitStatusParser.Parse("# branch.oid (initial)\0");
        Assert.Null(parsedInitial.HeadCommitId);

        var parsedZero40 = GitStatusParser.Parse($"# branch.oid {Sha1Zero}\0");
        Assert.Null(parsedZero40.HeadCommitId);

        var parsedZero64 = GitStatusParser.Parse($"# branch.oid {Sha256Zero}\0");
        Assert.Null(parsedZero64.HeadCommitId);

        var delInput = $"1 .D N... 100644 100644 000000 {Sha256Zero} {Sha256Zero} zero.txt\0? new_zero.txt\0";
        var parsedDel = GitStatusParser.Parse(delInput);
        var zeroDel = Assert.Single(parsedDel.WorktreeDeletions);
        Assert.Null(zeroDel.IndexId);

        var hashes = new Dictionary<string, string> { ["new_zero.txt"] = Sha256Zero };
        var status = GitStatusParser.PairExactMoves(parsedDel, hashes);
        Assert.Single(status.Unstaged, e => e.Path == "zero.txt" && e.Kind == FileChangeKind.Deleted);
        Assert.Single(status.Untracked, e => e.Path == "new_zero.txt");
    }

    [Fact]
    public void GitStatusParser_PairExactMoves_pairs_sha256_exact_moves()
    {
        var input = $"1 .D N... 100644 100644 000000 {Sha256Commit1} {Sha256Commit1} old.txt\0? new.txt\0";
        var parsed = GitStatusParser.Parse(input);

        var del = Assert.Single(parsed.WorktreeDeletions);
        Assert.Equal(32, del.IndexId!.Value.HashSize);
        Assert.Equal(Sha256Commit1, del.IndexId.Value.ToString());

        var hashes = new Dictionary<string, string> { ["new.txt"] = Sha256Commit1 };
        var status = GitStatusParser.PairExactMoves(parsed, hashes);

        Assert.Empty(status.Untracked);
        var u = Assert.Single(status.Unstaged);
        Assert.Equal("new.txt", u.Path);
        Assert.Equal("old.txt", u.OldPath);
        Assert.Equal(FileChangeKind.Renamed, u.Kind);
    }
}
