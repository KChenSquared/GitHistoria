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
    // v10.1.2: bumped 1 -> 2. Each .dat record now also carries its ParentCount parent SHAs (20
    // bytes each) so the store can seed TryOptimisticSnapshot's first paint on its own, without
    // depending on the bounded, less-reliable .gz walk cache. A v1 file has no parent bytes and
    // is rejected outright (TryOpen/Probe/ProbeAny all gate on Version) rather than misread.
    // v10.1.3: bumped 2 -> 3 to add WalkEngine to the header — the CLI streaming walk and the
    // libgit2 revwalk can order the same ref set's history differently, so a store the background
    // spill keeps appending to must never mix rows from both. A mismatch (or a pre-marker v2 file)
    // is rejected outright, forcing a one-time full re-spill per repo.
    // Phase 0.5 (v10 graph memory footprint plan): bumped 3 -> 4, in lockstep with
    // GraphWalkCache.Version — the CLI prime resume-skip fix and the canonical sorted tip order
    // both change a repo's walk output for an unchanged fingerprint, so a v3 store is rejected
    // outright and re-spills once.
    internal const int Version = 4;
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
        public int WalkEngine { get; set; }
    }

    /// <summary>
    /// Opens the on-disk store for <paramref name="repoPath"/> if it is present, matches
    /// <paramref name="fingerprint"/> / <paramref name="includeRemoteBranches"/> / <paramref
    /// name="engine"/>, and already holds at least <paramref name="wantCount"/> rows (or is
    /// exhausted). Returns null on any miss — a fresh walk then re-spills.
    /// </summary>
    public static DiskCommitStore? TryOpen(
        string repoPath, long fingerprint, bool includeRemoteBranches, int wantCount, WalkEngine engine)
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
                || hdr.IncludeRemoteBranches != includeRemoteBranches
                || (WalkEngine)hdr.WalkEngine != engine)
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
    /// match this fingerprint / remote-branch flag / engine? Returns its row count and exhausted
    /// flag. No file handle is opened (unlike <see cref="TryOpen"/>).</summary>
    public static (bool Ok, int RowCount, bool Exhausted) Probe(
        string repoPath, long fingerprint, bool includeRemoteBranches, WalkEngine engine)
    {
        try
        {
            var hdrPath = GraphCachePaths.File(repoPath, HdrExt);
            if (!File.Exists(hdrPath))
                return (false, 0, false);
            var hdr = JsonSerializer.Deserialize<Header>(File.ReadAllBytes(hdrPath), Json);
            if (hdr is null || hdr.Version != Version || hdr.Fingerprint != fingerprint
                || hdr.IncludeRemoteBranches != includeRemoteBranches || (WalkEngine)hdr.WalkEngine != engine)
                return (false, 0, false);
            return (true, hdr.RowCount, hdr.Exhausted);
        }
        catch
        {
            return (false, 0, false);
        }
    }

    /// <summary>
    /// v10.1.2: like <see cref="Probe"/> but WITHOUT a fingerprint check — for
    /// <c>TryOptimisticSnapshot</c>'s stale-tolerant first paint, which runs before the fresh
    /// ref-fingerprint check (a full <c>repo.Refs.ToList()</c>) even starts. The caller must
    /// treat the result as provisional, exactly like <see cref="GraphWalkCache.TryLoadStale"/>.
    /// v10.1.3: still gated on <paramref name="engine"/> — the engine that WOULD run right now is
    /// known cheaply (locator + query, no repo access), so an optimistic frame is never seeded
    /// from a store the other engine wrote.
    /// </summary>
    public static (bool Ok, int RowCount) ProbeAny(string repoPath, bool includeRemoteBranches, WalkEngine engine)
    {
        try
        {
            var hdrPath = GraphCachePaths.File(repoPath, HdrExt);
            if (!File.Exists(hdrPath))
                return (false, 0);
            var hdr = JsonSerializer.Deserialize<Header>(File.ReadAllBytes(hdrPath), Json);
            if (hdr is null || hdr.Version != Version || hdr.IncludeRemoteBranches != includeRemoteBranches
                || (WalkEngine)hdr.WalkEngine != engine || hdr.RowCount <= 0)
                return (false, 0);
            return (true, hdr.RowCount);
        }
        catch
        {
            return (false, 0);
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
internal sealed class GraphMetaWriter(string repoPath, long fingerprint, bool includeRemoteBranches, WalkEngine engine)
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
    /// <c>.dat</c>/<c>.idx</c>/<c>.search</c>. No real parent SHAs are available from a bare
    /// <see cref="CommitMetadata"/> list — each parent slot is written as a zero SHA (v10.1.2's
    /// <see cref="ReadRaw"/> only ever reads real spill data via the <see cref="RawCommit"/>
    /// overload below, so this is harmless; kept for existing callers/tests).</summary>
    public void Append(IReadOnlyList<CommitMetadata> rows, int start, int count)
        => Append(i => rows[i], _ => null, start, count);

    /// <summary>Overload that reads metadata + real parent SHAs straight off the walk records
    /// (no projection copy) — this is what the §7.6 spill uses, and what makes the store usable
    /// as an optimistic-snapshot source (v10.1.2).</summary>
    public void Append(IReadOnlyList<RawCommit> rows, int start, int count)
        => Append(i => rows[i].Metadata, i => rows[i].ParentIds, start, count);

    private void Append(Func<int, CommitMetadata> get, Func<int, IReadOnlyList<ObjectId>?> getParents, int start, int count)
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
            Span<byte> zeroSha = stackalloc byte[20];

            for (var i = 0; i < count; i++)
            {
                var m = get(start + i);
                var parents = getParents(start + i);
                BitConverter.TryWriteBytes(idxBuf, dat.Position);
                idx.Write(idxBuf);

                WriteSha(bw, m.Id);
                bw.Write(m.MessageShort);
                bw.Write(m.AuthorName);
                bw.Write(m.AuthorEmail);
                WriteWhen(bw, m.AuthorWhen);
                WriteWhen(bw, m.CommitWhen);
                bw.Write(m.ParentCount);
                for (var p = 0; p < m.ParentCount; p++)
                {
                    if (parents is not null && p < parents.Count)
                        bw.Write(parents[p].RawId);
                    else
                        bw.Write(zeroSha); // no real parent SHA available for this record
                }

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
                WalkEngine = (int)engine,
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

    /// <summary>Adopt rows appended since <see cref="Open"/> (called after an Extend spill).
    /// Phase 3a: reads only the <c>.idx</c> bytes appended since the last call instead of
    /// re-reading the whole file (11.8 MB on linux, previously re-read on every spill batch and
    /// every Extend) — the existing offsets are kept and only the new tail is appended.</summary>
    public void SetCount(int rowCount)
    {
        lock (_sync)
        {
            if (_closed || rowCount <= _count)
                return;
            var tail = ReadOffsetsFrom(GraphCachePaths.File(_repoPath, GraphMetaStore.IdxExt), _offsets.Length, rowCount);
            if (tail.Length > 0)
            {
                var merged = new long[_offsets.Length + tail.Length];
                Array.Copy(_offsets, merged, _offsets.Length);
                Array.Copy(tail, 0, merged, _offsets.Length, tail.Length);
                _offsets = merged;
            }
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
                    var (m, _) = Decode(index);
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

    /// <summary>Phase 3a: reads rows [<paramref name="first"/>, <paramref name="first"/> +
    /// <paramref name="count"/>) with full parent SHAs and timestamps — the batch form of
    /// <see cref="ReadRaw"/>, used to serve a whole already-spilled range straight from disk
    /// instead of keeping it resident in <c>RepoCache.Commits</c> (see
    /// <c>GraphLayoutService.ReadWindow</c>). One <see cref="RandomAccess.Read"/> covering every
    /// record in range, then per-row decode from the buffer — same shape as
    /// <see cref="Prefetch"/>. Faults the same way as every other reader here: a concurrent
    /// from-row-0 re-spill returns whatever was already decoded rather than throwing.</summary>
    public IReadOnlyList<RawCommit> ReadRawRange(int first, int count)
    {
        if (count <= 0)
            return [];
        lock (_sync)
        {
            if (_closed)
                return [];
            first = Math.Max(0, first);
            var last = Math.Min(first + count, _count) - 1;
            if (last < first)
                return [];
            try
            {
                var start = _offsets[first];
                var end = last + 1 < _offsets.Length ? _offsets[last + 1] : _datLength;
                var span = (int)(end - start);
                if (span <= 0)
                    return [];
                var buf = new byte[span];
                var read = RandomAccess.Read(_dat, buf, start);
                var result = new List<RawCommit>(last - first + 1);
                for (var row = first; row <= last; row++)
                {
                    var rs = _offsets[row] - start;
                    var re = (row + 1 < _offsets.Length ? _offsets[row + 1] : _datLength) - start;
                    if (rs < 0 || re > read || re <= rs)
                        break;
                    var (meta, parents) = DecodeFrom(buf.AsSpan((int)rs, (int)(re - rs)));
                    Cache(row, meta);
                    result.Add(new RawCommit(meta.Id, parents, meta));
                }
                return result;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                                       or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                return [];
            }
        }
    }

    /// <summary>v10.1.2: decodes row <paramref name="index"/> WITH its parent SHAs (persisted
    /// since <see cref="GraphMetaStore.Version"/> 2) — used by <c>TryOptimisticSnapshot</c> to
    /// seed a first paint straight from this store. Same fault handling as the indexer: a
    /// concurrent spill rewrite serves a childless stub rather than throwing off the render
    /// thread's caller.</summary>
    public RawCommit ReadRaw(int index)
    {
        lock (_sync)
        {
            if (_closed)
                return new RawCommit(ClosedStoreStub.Id, [], ClosedStoreStub);
            try
            {
                var (m, parents) = Decode(index);
                Cache(index, m);
                return new RawCommit(m.Id, parents, m);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                                       or ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                return new RawCommit(ClosedStoreStub.Id, [], ClosedStoreStub);
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
                    Cache(row, DecodeFrom(buf.AsSpan((int)rs, (int)(re - rs))).Meta);
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

    /// <summary>Phase 3a: reads only the offsets for rows [<paramref name="fromRow"/>,
    /// <paramref name="rowCount"/>) — the tail appended since the caller last read this file —
    /// instead of <see cref="ReadOffsets"/>'s whole-file read.</summary>
    private static long[] ReadOffsetsFrom(string idxPath, int fromRow, int rowCount)
    {
        using var fs = new FileStream(idxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var startByte = (long)fromRow * 8;
        if (startByte >= fs.Length)
            return [];
        fs.Position = startByte;
        var wantBytes = (int)Math.Min(fs.Length - startByte, (long)(rowCount - fromRow) * 8);
        var buf = new byte[wantBytes];
        var readTotal = 0;
        while (readTotal < buf.Length)
        {
            var r = fs.Read(buf, readTotal, buf.Length - readTotal);
            if (r <= 0)
                break;
            readTotal += r;
        }
        var n = readTotal / 8;
        var offsets = new long[n];
        for (var i = 0; i < n; i++)
            offsets[i] = BitConverter.ToInt64(buf, i * 8);
        return offsets;
    }

    private (CommitMetadata Meta, ObjectId[] Parents) Decode(int index)
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

    /// <summary>Parses one <c>.dat</c> record. Records written before <see cref="GraphMetaStore.Version"/>
    /// 2 have no parent-SHA bytes; that format is rejected wholesale by the header version check
    /// before any record is ever read, so a mismatch here would only mean real corruption.</summary>
    private static (CommitMetadata Meta, ObjectId[] Parents) DecodeFrom(ReadOnlySpan<byte> record)
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
        var parents = new ObjectId[parentCount];
        for (var i = 0; i < parentCount; i++)
            parents[i] = new ObjectId(br.ReadBytes(20));
        var meta = new CommitMetadata
        {
            Id = id,
            MessageShort = msg,
            AuthorName = author,
            AuthorEmail = email,
            AuthorWhen = authorWhen,
            CommitWhen = commitWhen,
            ParentCount = parentCount,
        };
        return (meta, parents);
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
