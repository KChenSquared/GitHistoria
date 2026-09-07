using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibGit2Sharp;
using Microsoft.Win32.SafeHandles;
using Historia.Services.Storage;

namespace Historia.Services.Graph;

/// <summary>
/// Shared naming for the machine-local graph cache files. A repo maps to one stem
/// <c>graph-&lt;sha1(normalised path)&gt;</c>; <see cref="GraphWalkCache"/> owns
/// <c>.json.gz</c> and <see cref="GraphMetaStore"/> owns the <c>.meta.*</c> siblings.
/// </summary>
internal static class GraphCachePaths
{
    public static string Stem(string repoPath)
    {
        var norm = repoPath.Replace('\\', '/').TrimEnd('/');
        // Case-fold only where the filesystem does (cheap 99%-correct proxy — see GraphWalkCache).
        if (OperatingSystem.IsWindows())
            norm = norm.ToLowerInvariant();
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(norm)));
        return $"graph-{hash}";
    }

    public static string File(string repoPath, string ext)
        => Path.Combine(AppPaths.CacheDirectory, Stem(repoPath) + ext);
}

/// <summary>
/// On-disk, random-access store for the graph's per-commit metadata (§7.6). When the
/// "Read commit details from disk" setting is on, <see cref="GraphLayoutService"/> spills the
/// laid-out rows here and points <see cref="GraphLayout.Commits"/> at a
/// <see cref="DiskCommitStore"/>, so only the fixed-size layout arrays stay resident on a
/// large history. Four siblings per repo:
/// <list type="bullet">
/// <item><c>.meta.hdr</c> — JSON: version, fingerprint, remote-branch flag, row count, exhausted.</item>
/// <item><c>.meta.dat</c> — one binary record per row (sha, three strings, two timestamps, parent count).</item>
/// <item><c>.meta.idx</c> — one <see cref="long"/> per row: the record's start offset in <c>.meta.dat</c>.</item>
/// <item><c>.meta.search</c> — one lowercased line per row (<c>sha ␟ message ␟ author</c>) for Ctrl+F.</item>
/// </list>
/// All writes are append-only and best-effort; any read fault downgrades the caller to the
/// in-memory path.
/// </summary>
internal sealed class GraphMetaStore
{
    internal const int Version = 1;
    internal const string HdrExt = ".meta.hdr";
    internal const string DatExt = ".meta.dat";
    internal const string IdxExt = ".meta.idx";
    internal const string SearchExt = ".meta.search";

    /// <summary>Field separator in <c>.meta.search</c> lines — US (unit separator).</summary>
    internal const char Sep = (char)0x1f;

    internal static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    internal sealed class Header
    {
        public int Version { get; set; }
        public long Fingerprint { get; set; }
        public bool IncludeRemoteBranches { get; set; }
        public int RowCount { get; set; }
        public bool Exhausted { get; set; }
    }

    /// <summary>
    /// Opens the on-disk store for <paramref name="repoPath"/> if it is present, matches
    /// <paramref name="fingerprint"/> / <paramref name="includeRemoteBranches"/>, and already
    /// holds at least <paramref name="wantCount"/> rows (or is exhausted). Returns null on any
    /// miss — a fresh walk then re-spills.
    /// </summary>
    public static DiskCommitStore? TryOpen(
        string repoPath, long fingerprint, bool includeRemoteBranches, int wantCount)
    {
        try
        {
            var hdrPath = GraphCachePaths.File(repoPath, HdrExt);
            if (!File.Exists(hdrPath))
                return null;

            var hdr = JsonSerializer.Deserialize<Header>(File.ReadAllBytes(hdrPath), Json);
            if (hdr is null
                || hdr.Version != Version
                || hdr.Fingerprint != fingerprint
                || hdr.IncludeRemoteBranches != includeRemoteBranches)
                return null;
            if (!hdr.Exhausted && hdr.RowCount < wantCount)
                return null;

            return DiskCommitStore.Open(repoPath, hdr.RowCount);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Cheap header-only probe: does a store for <paramref name="repoPath"/> exist and
    /// match this fingerprint / remote-branch flag? Returns its row count and exhausted flag.
    /// No file handle is opened (unlike <see cref="TryOpen"/>).</summary>
    public static (bool Ok, int RowCount, bool Exhausted) Probe(
        string repoPath, long fingerprint, bool includeRemoteBranches)
    {
        try
        {
            var hdrPath = GraphCachePaths.File(repoPath, HdrExt);
            if (!File.Exists(hdrPath))
                return (false, 0, false);
            var hdr = JsonSerializer.Deserialize<Header>(File.ReadAllBytes(hdrPath), Json);
            if (hdr is null || hdr.Version != Version || hdr.Fingerprint != fingerprint
                || hdr.IncludeRemoteBranches != includeRemoteBranches)
                return (false, 0, false);
            return (true, hdr.RowCount, hdr.Exhausted);
        }
        catch
        {
            return (false, 0, false);
        }
    }

    /// <summary>Deletes every <c>graph-*.meta.*</c> file. Returns the count removed.</summary>
    public static int Wipe()
    {
        var n = 0;
        try
        {
            var dir = new DirectoryInfo(AppPaths.CacheDirectory);
            if (!dir.Exists)
                return 0;
            foreach (var f in dir.GetFiles("graph-*.meta.*"))
            {
                try { f.Delete(); n++; } catch { /* skip locked */ }
            }
        }
        catch { }
        return n;
    }
}

/// <summary>
/// Append-only writer for a repo's <c>.meta.*</c> set. <see cref="Reset"/> truncates for a
/// fresh walk; <see cref="Append"/> adds laid-out rows; <see cref="Commit"/> rewrites the
/// header. All operations swallow IO errors — the cache is an optimisation, never a
/// correctness dependency.
/// </summary>
internal sealed class GraphMetaWriter(string repoPath, long fingerprint, bool includeRemoteBranches)
{
    private readonly string _hdr = GraphCachePaths.File(repoPath, GraphMetaStore.HdrExt);
    private readonly string _dat = GraphCachePaths.File(repoPath, GraphMetaStore.DatExt);
    private readonly string _idx = GraphCachePaths.File(repoPath, GraphMetaStore.IdxExt);
    private readonly string _search = GraphCachePaths.File(repoPath, GraphMetaStore.SearchExt);

    /// <summary>Truncates every sibling file. Call before appending row 0 of a fresh walk.</summary>
    public void Reset()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDirectory);
            foreach (var p in new[] { _hdr, _dat, _idx, _search })
                try { File.Delete(p); } catch { }
        }
        catch { }
    }

    /// <summary>Appends <paramref name="count"/> rows starting at <paramref name="start"/> to
    /// <c>.dat</c>/<c>.idx</c>/<c>.search</c>.</summary>
    public void Append(IReadOnlyList<CommitMetadata> rows, int start, int count)
        => Append(i => rows[i], start, count);

    /// <summary>Overload that reads metadata straight off the walk records (no projection copy).</summary>
    public void Append(IReadOnlyList<RawCommit> rows, int start, int count)
        => Append(i => rows[i].Metadata, start, count);

    private void Append(Func<int, CommitMetadata> get, int start, int count)
    {
        if (count <= 0)
            return;
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDirectory);
            using var dat = new FileStream(_dat, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var idx = new FileStream(_idx, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var search = new StreamWriter(new FileStream(_search, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            var bw = new BinaryWriter(dat, new UTF8Encoding(false), leaveOpen: true);
            var idxBuf = new byte[8];

            for (var i = 0; i < count; i++)
            {
                var m = get(start + i);
                BitConverter.TryWriteBytes(idxBuf, dat.Position);
                idx.Write(idxBuf);

                WriteSha(bw, m.Id);
                bw.Write(m.MessageShort);
                bw.Write(m.AuthorName);
                bw.Write(m.AuthorEmail);
                WriteWhen(bw, m.AuthorWhen);
                WriteWhen(bw, m.CommitWhen);
                bw.Write(m.ParentCount);

                // Full 40-char SHA so a Ctrl+F prefix of any length matches exactly as the
                // in-memory store does.
                search.Write(m.Id.Sha);
                search.Write(GraphMetaStore.Sep);
                search.Write(Clean(m.MessageShort).ToLowerInvariant());
                search.Write(GraphMetaStore.Sep);
                search.Write(Clean(m.AuthorName).ToLowerInvariant());
                search.Write('\n');
            }
            bw.Flush();
        }
        catch { }
    }

    /// <summary>Rewrites the header. Call after the matching <see cref="Append"/>(s).</summary>
    public void Commit(int rowCount, bool exhausted)
    {
        try
        {
            var hdr = new GraphMetaStore.Header
            {
                Version = GraphMetaStore.Version,
                Fingerprint = fingerprint,
                IncludeRemoteBranches = includeRemoteBranches,
                RowCount = rowCount,
                Exhausted = exhausted,
            };
            var tmp = $"{_hdr}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(hdr, GraphMetaStore.Json));
            File.Move(tmp, _hdr, overwrite: true);
        }
        catch { }
    }

    private static void WriteSha(BinaryWriter bw, ObjectId id) => bw.Write(id.RawId);

    private static void WriteWhen(BinaryWriter bw, DateTimeOffset when)
    {
        bw.Write(when.Ticks);                             // local-clock ticks
        bw.Write((short)when.Offset.TotalMinutes);        // zone offset
    }

    private static string Clean(string s)
        => s.Replace('\n', ' ').Replace('\r', ' ').Replace(GraphMetaStore.Sep, ' ');
}

/// <summary>
/// Read side of <see cref="GraphMetaStore"/>. Keeps the <c>.idx</c> offset table resident
/// (~8 bytes/row) and pages <c>.dat</c> on demand behind a bounded decode cache. Grows in
/// place via <see cref="SetCount"/> after an <see cref="GraphMetaWriter.Append"/>.
/// </summary>
internal sealed class DiskCommitStore : ICommitStore, IDisposable
{
    private const int DecodeCacheCap = 8192;

    private readonly string _repoPath;
    private readonly SafeFileHandle _dat;
    private long[] _offsets;            // [row] -> start byte in .dat
    private long _datLength;
    private int _count;

    private readonly Dictionary<int, CommitMetadata> _decoded = new(DecodeCacheCap);
    private readonly Queue<int> _decodedOrder = new(DecodeCacheCap);
    private readonly object _sync = new();

    // Lease count (§7.6): the RepoCache that opened the store holds the first lease; every
    // consumer that parks a reference to read from later takes another via AddRef(). The file
    // handle closes only when the last lease is released, so a RefsChanged re-walk dropping the
    // cache's lease can't pull the handle out from under an in-flight Render. Reads no-op once
    // closed rather than throwing ObjectDisposedException off the render thread.
    // Both fields are only mutated under _sync so AddRef and Dispose can't race a lease back in
    // after the count has already dropped to zero and committed to closing (that race handed a
    // caller a live-looking lease on a store whose handle was gone, blanking every row).
    private int _leases = 1;
    private volatile bool _closed;

    private static readonly CommitMetadata ClosedStoreStub = new()
    {
        Id = ObjectId.Zero,
        MessageShort = "",
        AuthorName = "",
        AuthorEmail = "",
        AuthorWhen = default,
        CommitWhen = default,
        ParentCount = 0,
    };

    private DiskCommitStore(string repoPath, SafeFileHandle dat, long[] offsets, long datLength, int count)
    {
        _repoPath = repoPath;
        _dat = dat;
        _offsets = offsets;
        _datLength = datLength;
        _count = count;
    }

    public static DiskCommitStore Open(string repoPath, int rowCount)
    {
        var datPath = GraphCachePaths.File(repoPath, GraphMetaStore.DatExt);
        var idxPath = GraphCachePaths.File(repoPath, GraphMetaStore.IdxExt);
        var offsets = ReadOffsets(idxPath, rowCount);
        var dat = File.OpenHandle(datPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var len = RandomAccess.GetLength(dat);
        return new DiskCommitStore(repoPath, dat, offsets, len, Math.Min(rowCount, offsets.Length));
    }

    /// <summary>Adopt rows appended since <see cref="Open"/> (called after an Extend spill).</summary>
    public void SetCount(int rowCount)
    {
        lock (_sync)
        {
            if (_closed || rowCount <= _count)
                return;
            _offsets = ReadOffsets(GraphCachePaths.File(_repoPath, GraphMetaStore.IdxExt), rowCount);
            _datLength = RandomAccess.GetLength(_dat);
            _count = Math.Min(rowCount, _offsets.Length);
        }
    }

    public int Count => Volatile.Read(ref _count);

    public CommitMetadata this[int index]
    {
        get
        {
            lock (_sync)
            {
                if (_closed)
                    return ClosedStoreStub;
                if (_decoded.TryGetValue(index, out var hit))
                    return hit;
                try
                {
                    var m = Decode(index);
                    Cache(index, m);
                    return m;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException
                                           or ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    // A background spill restart (SpillRange from == 0) is rewriting .dat/.idx
                    // under us. Serve a stub for this frame rather than throw off the render
                    // thread; the offset table is re-read on the next SetCount and reads recover.
                    return ClosedStoreStub;
                }
            }
        }
    }

    public void Prefetch(int firstRow, int lastRow)
    {
        firstRow = Math.Max(0, firstRow);
        lastRow = Math.Min(Count - 1, lastRow);
        if (lastRow < firstRow)
            return;
        lock (_sync)
        {
            if (_closed)
                return;
            try
            {
                var start = _offsets[firstRow];
                var end = lastRow + 1 < _offsets.Length ? _offsets[lastRow + 1] : _datLength;
                var span = (int)Math.Min(end - start, 1 << 22); // cap a pathological single read at 4 MB
                var buf = new byte[span];
                var read = RandomAccess.Read(_dat, buf, start);
                for (var row = firstRow; row <= lastRow; row++)
                {
                    if (_decoded.ContainsKey(row))
                        continue;
                    var rs = _offsets[row] - start;
                    var re = (row + 1 < _offsets.Length ? _offsets[row + 1] : _datLength) - start;
                    if (rs < 0 || re > read || re <= rs)
                        break;
                    Cache(row, DecodeFrom(buf.AsSpan((int)rs, (int)(re - rs))));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                                       or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // Concurrent .dat/.idx rewrite (see the indexer) — the prefetch is only a hint,
                // so drop it; the per-row indexer path serves stubs until the store settles.
            }
        }
    }

    public IEnumerator<CommitMetadata> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return this[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Ctrl+F backing scan (§7.5): streams <c>.meta.search</c>, one line per row. Matches the
    /// in-memory find exactly — substring on message or author, prefix on the SHA.
    /// </summary>
    public IReadOnlyList<int> Search(string term)
    {
        var hits = new List<int>();
        var t = term.Trim().ToLowerInvariant();
        if (t.Length == 0 || _closed)
            return hits;
        try
        {
            var path = GraphCachePaths.File(_repoPath, GraphMetaStore.SearchExt);
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8);
            var row = -1;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                row++;
                if (row >= _count)
                    break;
                var sep = line.IndexOf(GraphMetaStore.Sep);
                if (sep < 0)
                    continue;
                var sha = line.AsSpan(0, sep);
                var rest = line.AsSpan(sep + 1);
                if (rest.Contains(t, StringComparison.Ordinal) || sha.StartsWith(t, StringComparison.Ordinal))
                    hits.Add(row);
            }
        }
        catch
        {
            return hits;
        }
        return hits;
    }

    /// <inheritdoc/>
    public ICommitStore? AddRef()
    {
        lock (_sync)
        {
            if (_closed)
                return null;
            _leases++;
            return this;
        }
    }

    /// <summary>Releases one lease; closes the file handle when the last one goes. Idempotent and
    /// tolerant of an over-release (a consumer disposing twice) — the handle is never closed
    /// more than once, and never while a lease is still outstanding.</summary>
    public void Dispose()
    {
        bool close;
        lock (_sync)
        {
            if (_leases <= 0 || _closed)
                return;                     // already fully released / over-released
            close = --_leases == 0;
            if (close)
                _closed = true;
        }
        if (close)
            _dat.Dispose();
    }

    // ── internals ──

    private static long[] ReadOffsets(string idxPath, int rowCount)
    {
        var bytes = File.ReadAllBytes(idxPath);
        var n = Math.Min(rowCount, bytes.Length / 8);
        var offsets = new long[n];
        for (var i = 0; i < n; i++)
            offsets[i] = BitConverter.ToInt64(bytes, i * 8);
        return offsets;
    }

    private CommitMetadata Decode(int index)
    {
        if (index < 0 || index >= _count)
            throw new ArgumentOutOfRangeException(nameof(index));
        var start = _offsets[index];
        var end = index + 1 < _offsets.Length ? _offsets[index + 1] : _datLength;
        var len = (int)(end - start);
        if (len <= 0 || start < 0 || end > _datLength)
            throw new InvalidDataException("graph meta record out of range");
        var buf = new byte[len];
        var read = RandomAccess.Read(_dat, buf, start);
        if (read < len)
            throw new InvalidDataException("graph meta record truncated");
        return DecodeFrom(buf);
    }

    private static CommitMetadata DecodeFrom(ReadOnlySpan<byte> record)
    {
        using var ms = new MemoryStream(record.ToArray(), writable: false);
        using var br = new BinaryReader(ms, new UTF8Encoding(false));
        var sha = br.ReadBytes(20);
        var id = new ObjectId(sha);
        var msg = br.ReadString();
        var author = br.ReadString();
        var email = br.ReadString();
        var authorWhen = ReadWhen(br);
        var commitWhen = ReadWhen(br);
        var parentCount = br.ReadInt32();
        return new CommitMetadata
        {
            Id = id,
            MessageShort = msg,
            AuthorName = author,
            AuthorEmail = email,
            AuthorWhen = authorWhen,
            CommitWhen = commitWhen,
            ParentCount = parentCount,
        };
    }

    private static DateTimeOffset ReadWhen(BinaryReader br)
    {
        var ticks = br.ReadInt64();
        var offsetMinutes = br.ReadInt16();
        try { return new DateTimeOffset(ticks, TimeSpan.FromMinutes(offsetMinutes)); }
        catch { return DateTimeOffset.UnixEpoch; }
    }

    private void Cache(int index, CommitMetadata m)
    {
        if (_decoded.ContainsKey(index))
            return;
        if (_decoded.Count >= DecodeCacheCap && _decodedOrder.Count > 0)
            _decoded.Remove(_decodedOrder.Dequeue());
        _decoded[index] = m;
        _decodedOrder.Enqueue(index);
    }
}
