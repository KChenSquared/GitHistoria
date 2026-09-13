namespace Historia.Tests;

/// <summary>
/// §3 linux cold-prime plan: <see cref="GitLogStream"/>'s NUL-record framing and per-field
/// parsing, exercised directly with fixture bytes — no real git process involved. See
/// <see cref="GitLogStreamWalkEquivalenceTests"/> for the end-to-end CLI-vs-libgit2 comparison
/// against a real repo.
/// </summary>
public sealed class GitLogStreamTests
{
    private const char Sep = '\x1f';

    private static string Sha(int n) => n.ToString("x40");

    private static string Record(string sha, string parents, string authorWhen, string commitWhen, string author, string email, string subject)
        => string.Join(Sep, sha, parents, authorWhen, commitWhen, author, email, subject);

    private static byte[] BuildStream(params string[] records)
    {
        using var ms = new MemoryStream();
        foreach (var r in records)
        {
            var bytes = Encoding.UTF8.GetBytes(r);
            ms.Write(bytes, 0, bytes.Length);
            ms.WriteByte(0); // git log -z record terminator
        }
        return ms.ToArray();
    }

    [Fact]
    public void Parses_a_root_commit_with_no_parents()
    {
        var record = Record(Sha(1), "", "2026-01-01T00:00:00-08:00", "2026-01-01T00:01:00-08:00",
            "Ada Lovelace", "ada@example.com", "root commit");

        var raw = GitLogStream.ParseRecord(record);

        Assert.Equal(new ObjectId(Sha(1)), raw.Id);
        Assert.Empty(raw.ParentIds);
        Assert.Equal(0, raw.Metadata.ParentCount);
        Assert.Equal("root commit", raw.Metadata.MessageShort);
        Assert.Equal("Ada Lovelace", raw.Metadata.AuthorName);
        Assert.Equal("ada@example.com", raw.Metadata.AuthorEmail);
    }

    [Fact]
    public void Parses_a_merge_commit_with_two_parents()
    {
        var record = Record(Sha(3), $"{Sha(1)} {Sha(2)}", "2026-01-02T00:00:00-08:00", "2026-01-02T00:01:00-08:00",
            "Grace Hopper", "grace@example.com", "merge branch 'feature'");

        var raw = GitLogStream.ParseRecord(record);

        Assert.Equal(2, raw.ParentIds.Count);
        Assert.Equal(new ObjectId(Sha(1)), raw.ParentIds[0]);
        Assert.Equal(new ObjectId(Sha(2)), raw.ParentIds[1]);
        Assert.Equal(2, raw.Metadata.ParentCount);
    }

    [Fact]
    public void Parses_non_UTC_author_and_commit_offsets_independently()
    {
        // Author and committer can legitimately sit in different zones (a rebase, a patch
        // applied by someone else) — the two fields must parse independently.
        var record = Record(Sha(4), Sha(1), "2026-03-01T10:15:00+05:30", "2026-03-01T11:00:00+09:00",
            "Dev", "dev@example.com", "tz test");

        var raw = GitLogStream.ParseRecord(record);

        Assert.Equal(TimeSpan.FromMinutes(5 * 60 + 30), raw.Metadata.AuthorWhen.Offset);
        Assert.Equal(TimeSpan.FromHours(9), raw.Metadata.CommitWhen.Offset);
        Assert.Equal(10, raw.Metadata.AuthorWhen.Hour);
        Assert.Equal(11, raw.Metadata.CommitWhen.Hour);
    }

    [Fact]
    public void Parses_a_unicode_subject()
    {
        var subject = "fix(ui): naïve café résumé — 日本語 emoji 🎉";
        var record = Record(Sha(5), Sha(1), "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z",
            "Dev", "dev@example.com", subject);

        var raw = GitLogStream.ParseRecord(record);

        Assert.Equal(subject, raw.Metadata.MessageShort);
    }

    [Fact]
    public void Rejects_a_record_with_too_few_fields()
    {
        Assert.ThrowsAny<Exception>(() => GitLogStream.ParseRecord("only-two" + Sep + "fields"));
    }

    [Fact]
    public void NulRecordReader_splits_multiple_records_from_one_buffered_read()
    {
        var r1 = Record(Sha(1), "", "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", "A", "a@example.com", "first");
        var r2 = Record(Sha(2), Sha(1), "2026-01-02T00:00:00Z", "2026-01-02T00:00:00Z", "B", "b@example.com", "second");
        using var reader = new NulRecordReader(new MemoryStream(BuildStream(r1, r2)));

        Assert.Equal(r1, reader.ReadRecord());
        Assert.Equal(r2, reader.ReadRecord());
        Assert.Null(reader.ReadRecord());
    }

    [Fact]
    public void NulRecordReader_reassembles_a_record_delivered_one_byte_at_a_time()
    {
        // Exercises the "NUL not yet seen in this chunk, keep accumulating" path with the
        // smallest possible reads.
        var r1 = Record(Sha(9), "", "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", "A", "a@example.com", "one byte at a time");
        using var reader = new NulRecordReader(new OneByteAtATimeStream(BuildStream(r1)));

        Assert.Equal(r1, reader.ReadRecord());
        Assert.Null(reader.ReadRecord());
    }

    /// <summary>Returns at most one byte per <see cref="Read"/> call, to force
    /// <see cref="NulRecordReader"/> through its multi-chunk accumulation path deterministically.</summary>
    private sealed class OneByteAtATimeStream(byte[] data) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length)
                return 0;
            buffer[offset] = data[_pos++];
            return 1;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
