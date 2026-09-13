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
/// 2026-09-12 memoryshrink review, finding #1: one lock object per repo cache stem, shared by
/// every <see cref="GraphMetaWriter"/> for that repo (the active tab's own spill, an inactive
/// tab's headless spool/reorder). Every <c>.meta.*</c> mutation — <see cref="GraphMetaWriter.Reset"/>,
/// an append + <see cref="GraphMetaWriter.Commit"/>, <see cref="GraphMetaWriter.WriteOrder"/>,
/// <see cref="GraphMetaWriter.WriteTips"/>, <see cref="GraphMetaWriter.CommitReorder"/> — takes
/// this lock and re-checks the on-disk header before writing, so two writers racing for the same
/// repo's store can never interleave (see <see cref="GraphMetaWriter.AppendSpillBatch"/> and
/// <see cref="GraphMetaWriter.TryCommitReorder"/>). Keyed by stem, not repo path, since the stem
/// is already the case-folded, slash-normalised identity the files themselves are named after.
/// </summary>
internal static class GraphCacheLocks
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Locks =
        new(StringComparer.Ordinal);

    public static object For(string repoPath) =>
        Locks.GetOrAdd(GraphCachePaths.Stem(repoPath), static _ => new object());
}

/// <summary>
/// On-disk, random-access store for the graph's per-commit metadata (§7.6). When the
/// "Read commit details from disk" setting is on, <see cref="GraphLayoutService"/> spills the
/// laid-out rows here and points <see cref="GraphLayout.Commits"/> at a
/// <see cref="DiskCommitStore"/>, so only the fixed-size layout arrays stay resident on a
/// large history. Siblings per repo:
/// <list type="bullet">
/// <item><c>.meta.hdr</c> — JSON: version, walk key, remote-branch flag, row count, record
/// count, order version, exhausted.</item>
/// <item><c>.meta.dat</c> — one binary RECORD per distinct commit (sha, three strings, two
/// timestamps, parent count) — append-only, never rewritten by a ref change (Phase 3c).</item>
/// <item><c>.meta.idx</c> — one <see cref="long"/> per RECORD: its start offset in
/// <c>.meta.dat</c>.</item>
/// <item><c>.meta.search</c> — one lowercased line per RECORD (<c>sha ␟ message ␟ author</c>)
/// for Ctrl+F.</item>
/// <item><c>.meta.order.&lt;n&gt;</c> (Phase 3c, only once a ref change has actually reordered
/// history) — one <see cref="int"/> per ROW: which record occupies that row. Absent
/// (<c>OrderVersion == 0</c>) means the identity order — row <c>i</c> IS record <c>i</c>, exactly
/// as before Phase 3c; the common case, and the only one before any reorder has ever run.</item>
/// <item><c>.meta.tips</c> (Phase 3c) — the sorted tip SHAs (20 bytes each) the current order was
/// built from; not versioned, always describes the CURRENT order.</item>
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
    // Phase 3c (v10 graph memory footprint plan): bumped 4 -> 5 to add RecordCount and
    // OrderVersion to the header — a v4 file has no order indirection (every row IS its record)
    // and no RecordCount field; rejected outright, one more one-time re-spill per repo.
    internal const int Version = 5;
    internal const string HdrExt = ".meta.hdr";
    internal const string DatExt = ".meta.dat";
    internal const string IdxExt = ".meta.idx";
    internal const string SearchExt = ".meta.search";
    internal const string TipsExt = ".meta.tips";

    /// <summary>Field separator in <c>.meta.search</c> lines — US (unit separator).</summary>
    internal const char Sep = (char)0x1f;

    internal static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    /// <summary>Phase 3c: the versioned order file for this repo — <c>version == 0</c> never
    /// exists on disk (0 means "identity order", encoded by the header alone).</summary>
    internal static string OrderFile(string repoPath, int version) =>
        GraphCachePaths.File(repoPath, $".meta.order.{version}");

    internal sealed class Header
    {
        public int Version { get; set; }
        public long Fingerprint { get; set; }
        public bool IncludeRemoteBranches { get; set; }
        public int RowCount { get; set; }
        public bool Exhausted { get; set; }
        public int WalkEngine { get; set; }

        /// <summary>Phase 3c: total distinct commit records ever appended to <c>.meta.dat</c> —
        /// equals <see cref="RowCount"/> until the first reorder, after which it can exceed it
        /// (new commits get new records; unreachable ones' old records just aren't referenced by
        /// the current order any more, until compaction).</summary>
        public int RecordCount { get; set; }

        /// <summary>Phase 3c: which <c>.meta.order.&lt;n&gt;</c> file maps rows to records.
        /// <c>0</c> means identity — row <c>i</c> is record <c>i</c>, no order file exists.</summary>
        public int OrderVersion { get; set; }
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

            return DiskCommitStore.Open(repoPath, hdr.RowCount, hdr.RecordCount, hdr.OrderVersion);
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

    /// <summary>
    /// Phase 4: like <see cref="ProbeAny"/> but returns the full header, including the WALK KEY
    /// it was written under — used by the headless per-tab pipeline to tell "no store at all"
    /// apart from "a store exists, but its walk key has moved" (a reorder opportunity, Phase 3c)
    /// without needing to know the current walk key up front.
    /// </summary>
    public static (bool Ok, long WalkKey, int RowCount, int RecordCount, int OrderVersion, bool Exhausted) ProbeExisting(
        string repoPath, bool includeRemoteBranches, WalkEngine engine)
    {
        try
        {
            var hdrPath = GraphCachePaths.File(repoPath, HdrExt);
            if (!File.Exists(hdrPath))
                return (false, 0, 0, 0, 0, false);
            var hdr = JsonSerializer.Deserialize<Header>(File.ReadAllBytes(hdrPath), Json);
            if (hdr is null || hdr.Version != Version || hdr.IncludeRemoteBranches != includeRemoteBranches
                || (WalkEngine)hdr.WalkEngine != engine)
                return (false, 0, 0, 0, 0, false);
            return (true, hdr.Fingerprint, hdr.RowCount, hdr.RecordCount, hdr.OrderVersion, hdr.Exhausted);
        }
        catch
        {
            return (false, 0, 0, 0, 0, false);
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

    /// <summary>Finding #6: deletes every <c>.meta.order.&lt;n&gt;</c> file for this repo strictly
    /// older than <paramref name="currentVersion"/> — call right after a successful
    /// <see cref="GraphMetaWriter.TryCommitReorder"/>. A reader loads the whole order file once,
    /// at <see cref="DiskCommitStore.Open"/>, and never re-reads it later, so once the header has
    /// switched to <paramref name="currentVersion"/> no live reader can still be holding a
    /// reference to an older one.</summary>
    public static void DeleteSupersededOrderFiles(string repoPath, int currentVersion)
    {
        try
        {
            var dir = new DirectoryInfo(AppPaths.CacheDirectory);
            if (!dir.Exists)
                return;
            var stem = GraphCachePaths.Stem(repoPath);
            var prefix = $"{stem}.meta.order.";
            foreach (var f in dir.GetFiles($"{prefix}*"))
            {
                var suffix = f.Name[prefix.Length..];
                if (int.TryParse(suffix, out var version) && version < currentVersion)
                    try { f.Delete(); } catch { /* skip locked */ }
            }
        }
        catch { }
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
    private readonly string _tips = GraphCachePaths.File(repoPath, GraphMetaStore.TipsExt);

    /// <summary>Truncates every sibling file, including any Phase 3c order files (a from-row-0
    /// re-spill starts the order over at identity too). Call before appending row 0 of a fresh
    /// walk.</summary>
    public void Reset()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDirectory);
            foreach (var p in new[] { _hdr, _dat, _idx, _search, _tips })
                try { File.Delete(p); } catch { }
            DeleteOrderFiles(repoPath);
        }
        catch { }
    }

    private static void DeleteOrderFiles(string repoPath)
    {
        try
        {
            var dir = new DirectoryInfo(AppPaths.CacheDirectory);
            if (!dir.Exists)
                return;
            foreach (var f in dir.GetFiles($"{GraphCachePaths.Stem(repoPath)}.meta.order.*"))
                try { f.Delete(); } catch { }
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

    /// <summary>Rewrites the header for the IDENTITY order — row <c>i</c> is record <c>i</c>,
    /// <c>RecordCount == rowCount</c>, no order file. Call after the matching
    /// <see cref="Append"/>(s). This is every writer before Phase 3c's reorder ever runs.</summary>
    public void Commit(int rowCount, bool exhausted) =>
        WriteHeader(rowCount, recordCount: rowCount, orderVersion: 0, exhausted);

    /// <summary>Phase 3c: rewrites the header to point at a new order version, after
    /// <see cref="WriteOrder"/> has written it and any genuinely-new commits have already been
    /// appended via <see cref="Append(IReadOnlyList{RawCommit},int,int)"/>. <paramref
    /// name="recordCount"/> is the TOTAL distinct records now in <c>.meta.dat</c> (can exceed
    /// <paramref name="rowCount"/> once history has been rewritten). Call after
    /// <see cref="WriteOrder"/> and <see cref="WriteTips"/> — this is the atomic switch-over: a
    /// reader that opened the store under the old header keeps reading the old order until it
    /// reopens.</summary>
    public void CommitReorder(int rowCount, int recordCount, int orderVersion, bool exhausted) =>
        WriteHeader(rowCount, recordCount, orderVersion, exhausted);

    private void WriteHeader(int rowCount, int recordCount, int orderVersion, bool exhausted)
    {
        try
        {
            var hdr = new GraphMetaStore.Header
            {
                Version = GraphMetaStore.Version,
                Fingerprint = fingerprint,
                IncludeRemoteBranches = includeRemoteBranches,
                RowCount = rowCount,
                RecordCount = recordCount,
                OrderVersion = orderVersion,
                Exhausted = exhausted,
                WalkEngine = (int)engine,
            };
            var tmp = $"{_hdr}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(hdr, GraphMetaStore.Json));
            File.Move(tmp, _hdr, overwrite: true);
        }
        catch { }
    }

    /// <summary>Phase 3c: writes a new, versioned row -&gt; record order file. Doesn't touch the
    /// header — call <see cref="CommitReorder"/> after this (and after <see cref="WriteTips"/>)
    /// to switch over. Versioned so a reader mid-read of the current order is never affected;
    /// stale versions are removed by <see cref="Reset"/> or (compaction) explicit cleanup.</summary>
    public bool WriteOrder(int version, int[] order)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDirectory);
            var path = GraphMetaStore.OrderFile(repoPath, version);
            var bytes = new byte[order.Length * 4];
            Buffer.BlockCopy(order, 0, bytes, 0, bytes.Length);
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Phase 3c: writes the sorted tip SHAs the CURRENT order was built from — read back
    /// by a future reorder/splice to decide whether it can trust the existing order as a
    /// starting point. Not versioned; always describes whatever order the header currently
    /// points at.</summary>
    public void WriteTips(IReadOnlyList<ObjectId> sortedTips)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDirectory);
            var bytes = new byte[sortedTips.Count * 20];
            for (var i = 0; i < sortedTips.Count; i++)
                sortedTips[i].RawId.CopyTo(bytes.AsSpan(i * 20, 20));
            var tmp = $"{_tips}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, _tips, overwrite: true);
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

    /// <summary>Finding #1: appends a spill batch under this repo's <see cref="GraphCacheLocks"/>
    /// lock, re-checking the on-disk header first rather than trusting the caller's own idea of
    /// <paramref name="from"/> — another writer for this same repo (the inactive-tab headless
    /// spool/reorder, or another cache instance) may have moved the store underneath it.
    /// <list type="bullet">
    /// <item>Header absent, an old version, or a different walk key: only a legitimate reset
    /// (<paramref name="from"/> == 0) proceeds — <see cref="Reset"/> then append. Any other
    /// mismatch (<paramref name="from"/> &gt; 0) is a <see cref="SpillOutcome.Conflict"/>: this
    /// writer expected to continue a store that no longer exists at the key it started from.</item>
    /// <item>Header matches this walk key and its <c>RowCount</c> equals <paramref name="from"/>:
    /// the normal case — append and commit.</item>
    /// <item>Header matches and its <c>RowCount</c> is already &gt;= <paramref name="to"/>:
    /// another writer already durably wrote this exact range (the same deterministic walk) —
    /// <see cref="SpillOutcome.Adopted"/>, caller adopts the existing rows instead of appending.</item>
    /// <item>Header matches but its <c>RowCount</c> falls strictly between <paramref name="from"/>
    /// and <paramref name="to"/>: a gap — <see cref="SpillOutcome.Conflict"/>.</item>
    /// </list>
    /// </summary>
    public (SpillOutcome Outcome, int RowCount) AppendSpillBatch(IReadOnlyList<RawCommit> rows, int from, int to, bool exhausted)
    {
        lock (GraphCacheLocks.For(repoPath))
        {
            var existing = GraphMetaStore.ProbeExisting(repoPath, includeRemoteBranches, engine);
            var matchesKey = existing.Ok && existing.WalkKey == fingerprint;

            if (!matchesKey)
            {
                if (from != 0)
                    return (SpillOutcome.Conflict, existing.RowCount);
                Reset();
            }
            else if (existing.RowCount >= to)
            {
                return (SpillOutcome.Adopted, existing.RowCount);
            }
            else if (existing.RowCount != from)
            {
                return (SpillOutcome.Conflict, existing.RowCount);
            }

            Append(rows, 0, to - from);
            Commit(to, exhausted);
            return (SpillOutcome.Appended, to);
        }
    }

    /// <summary>Finding #5 (streaming reorder): appends one batch of brand-new records a reorder
    /// discovered mid-walk, under this repo's <see cref="GraphCacheLocks"/> lock, re-checking that
    /// the on-disk header's walk key is still <paramref name="expectedWalkKey"/> — the key the
    /// reorder is building on top of. These records are not yet referenced by any RowCount/
    /// OrderVersion (only <see cref="TryCommitReorder"/>'s final header write makes them count),
    /// so if the reorder is later abandoned they simply become harmless dead records for the next
    /// compaction pass to clean up. Returns false (and appends nothing) if the header moved —
    /// the caller must abandon the whole reorder.</summary>
    public bool AppendReorderBatch(IReadOnlyList<RawCommit> newRecords, long expectedWalkKey)
    {
        if (newRecords.Count == 0)
            return true;
        lock (GraphCacheLocks.For(repoPath))
        {
            var existing = GraphMetaStore.ProbeExisting(repoPath, includeRemoteBranches, engine);
            if (!existing.Ok || existing.WalkKey != expectedWalkKey)
                return false;
            Append(newRecords, 0, newRecords.Count);
            return true;
        }
    }

    /// <summary>Finding #1 + #5: the reorder's atomic switch-over, under this repo's
    /// <see cref="GraphCacheLocks"/> lock. Re-reads the header and requires it to still match
    /// exactly what the reorder started from (<paramref name="expectedOldWalkKey"/>, <paramref
    /// name="expectedOldExhausted"/>, <paramref name="expectedOldOrderVersion"/>, <paramref
    /// name="expectedOldRecordCount"/>) — every new record <paramref name="newRecordCount"/>
    /// counts was already durably appended by <see cref="AppendReorderBatch"/> batch by batch, off
    /// this lock, so another writer (a live foreground open of this same repo, or a second
    /// headless job) could have moved the header in the meantime. Any mismatch abandons the whole
    /// attempt; the old order stays authoritative and the already-appended records become dead
    /// (harmless — the next compaction pass reclaims them). Returns whether the commit actually
    /// happened.</summary>
    public bool TryCommitReorder(
        long expectedOldWalkKey, bool expectedOldExhausted, int expectedOldOrderVersion, int expectedOldRecordCount,
        int newRecordCount, int[] order, IReadOnlyList<ObjectId> sortedTips)
    {
        lock (GraphCacheLocks.For(repoPath))
        {
            var probe = GraphMetaStore.ProbeExisting(repoPath, includeRemoteBranches, engine);
            if (!probe.Ok || probe.WalkKey != expectedOldWalkKey || probe.Exhausted != expectedOldExhausted
                || probe.OrderVersion != expectedOldOrderVersion || probe.RecordCount != expectedOldRecordCount)
                return false;

            var newOrderVersion = expectedOldOrderVersion + 1;
            if (!WriteOrder(newOrderVersion, order))
                return false;
            WriteTips(sortedTips);
            CommitReorder(
                rowCount: order.Length,
                recordCount: expectedOldRecordCount + newRecordCount,
                orderVersion: newOrderVersion,
                exhausted: true);
            return true;
        }
    }
}

/// <summary>Outcome of <see cref="GraphMetaWriter.AppendSpillBatch"/> — finding #1.</summary>
internal enum SpillOutcome
{
    /// <summary>The batch was appended and the header committed normally.</summary>
    Appended,

    /// <summary>Another writer already durably wrote this exact range; the caller should adopt
    /// the existing rows (<see cref="GraphMetaStore.ProbeExisting"/>'s returned row count) instead
    /// of appending anything.</summary>
    Adopted,

    /// <summary>The on-disk header no longer matches what this writer expected to continue from —
    /// the caller must stop spilling for this cache and let the next compute resync.</summary>
    Conflict,
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
    private long[] _offsets;            // [record] -> start byte in .dat
    private long _datLength;
    private int _count;                 // row count (== record count until the first reorder)

    /// <summary>Phase 3c: [row] -&gt; record index, or null for the identity order (row IS
    /// record — every store before its first reorder, and the overwhelming majority always).
    /// Loaded once at <see cref="Open"/> and never mutated in place — a reorder always produces a
    /// freshly-opened store (see <c>GraphLayoutService</c>'s reorder job), so a reader never needs
    /// to see an order file change under it.</summary>
    private readonly int[]? _order;

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

    private DiskCommitStore(string repoPath, SafeFileHandle dat, long[] offsets, long datLength, int count, int[]? order)
    {
        _repoPath = repoPath;
        _dat = dat;
        _offsets = offsets;
        _datLength = datLength;
        _count = count;
        _order = order;
    }

    /// <summary>Opens a store at the IDENTITY order — row <c>i</c> IS record <c>i</c>. Every
    /// caller before Phase 3c's reorder ever runs, and the only shape <see cref="SetCount"/>
    /// supports growing in place.</summary>
    public static DiskCommitStore Open(string repoPath, int rowCount) => Open(repoPath, rowCount, rowCount, orderVersion: 0);

    /// <summary>Phase 3c: opens a store whose current header names an explicit order version.
    /// <paramref name="recordCount"/> sizes the <c>.idx</c>/offset table (records, not rows);
    /// <paramref name="orderVersion"/> 0 means identity (no order file to load).</summary>
    public static DiskCommitStore Open(string repoPath, int rowCount, int recordCount, int orderVersion)
    {
        var datPath = GraphCachePaths.File(repoPath, GraphMetaStore.DatExt);
        var idxPath = GraphCachePaths.File(repoPath, GraphMetaStore.IdxExt);
        var offsets = ReadOffsets(idxPath, recordCount);
        var dat = File.OpenHandle(datPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var len = RandomAccess.GetLength(dat);
        int[]? order = null;
        if (orderVersion > 0)
        {
            var orderPath = GraphMetaStore.OrderFile(repoPath, orderVersion);
            order = ReadOrder(orderPath, rowCount);
        }
        var count = order is not null ? Math.Min(rowCount, order.Length) : Math.Min(rowCount, offsets.Length);
        return new DiskCommitStore(repoPath, dat, offsets, len, count, order);
    }

    private static int[] ReadOrder(string orderPath, int rowCount)
    {
        var bytes = File.ReadAllBytes(orderPath);
        var n = Math.Min(rowCount, bytes.Length / 4);
        var order = new int[n];
        Buffer.BlockCopy(bytes, 0, order, 0, n * 4);
        return order;
    }

    /// <summary>Adopt rows appended since <see cref="Open"/> (called after an Extend spill).
    /// Phase 3a: reads only the <c>.idx</c> bytes appended since the last call instead of
    /// re-reading the whole file (11.8 MB on linux, previously re-read on every spill batch and
    /// every Extend) — the existing offsets are kept and only the new tail is appended.
    /// Phase 3c: only valid for an identity-order store (<see cref="_order"/> null) — a store
    /// opened at an explicit order version never grows in place; the layout service reopens a
    /// fresh store after a reorder instead.</summary>
    public void SetCount(int rowCount)
    {
        lock (_sync)
        {
            if (_closed || _order is not null || rowCount <= _count)
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

    /// <summary>Phase 3c: total distinct commit records in <c>.meta.dat</c> right now (can exceed
    /// <see cref="Count"/> once history has been reordered — old records superseded by a reorder
    /// stay in the file, unreferenced, until compaction).</summary>
    public int RecordCount => _offsets.Length;

    /// <summary>Row <paramref name="row"/>'s record index — identity unless an order file is
    /// loaded.</summary>
    private int RecordOf(int row) => _order is null ? row : _order[row];

    /// <summary>Phase 3c: builds a SHA -&gt; record-index table by reading just the leading
    /// 20-byte SHA of every existing record (skips message/author/parent bytes entirely) — used
    /// by the reorder job to find which commits in a freshly walked history already have a
    /// durable record, so it only appends genuinely new ones. One small read per record; this is
    /// a background, gated, lowest-priority job (never on the render thread), so it trades read
    /// count for simplicity rather than batching — the plan's own repo-scale numbers (measured on
    /// linux/vscode) are for the reorder as a whole, not this specific pass.
    /// <para>
    /// Finding #5: keyed by <see cref="CompactShaIndex"/> (an 8-byte prefix per entry, verified
    /// against the full id on any hit) instead of the 40-char hex string this used to key by — at
    /// 1.5M records on linux the old table held ~150-180 MB of hex strings; this holds meaningfully
    /// less while staying exactly as correct (every lookup still verifies the full id).
    /// </para>
    /// </summary>
    public CompactShaIndex BuildShaIndex()
    {
        var index = new CompactShaIndex(RecordCount);
        lock (_sync)
        {
            if (_closed)
                return index;
            Span<byte> shaBuf = stackalloc byte[20];
            for (var rec = 0; rec < _offsets.Length; rec++)
            {
                if (RandomAccess.Read(_dat, shaBuf, _offsets[rec]) != 20)
                    continue;
                index.Add(new ObjectId(shaBuf.ToArray()), rec);
            }
        }
        return index;
    }

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
                // Phase 3c: an explicit order means consecutive ROWS need not be consecutive
                // RECORDS (or even increasing ones) — the one-big-read trick below only holds for
                // the identity order. Reordered stores fall back to one read per row; simpler and
                // correct, at the cost of the batching this method exists for. A reorder is rare
                // (a fetch/branch-create/branch-delete, not every extend), so this only bites the
                // rows read right after one, before the layout's own decode cache warms up.
                if (_order is not null)
                {
                    // ReadRaw re-enters this same lock (Monitor is reentrant per-thread) — safe.
                    var perRow = new List<RawCommit>(last - first + 1);
                    for (var row = first; row <= last; row++)
                        perRow.Add(ReadRaw(row));
                    return perRow;
                }

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
                // Phase 3c: same "identity order only" restriction as ReadRawRange — a reordered
                // store falls back to one decode per row via the indexer's own path.
                if (_order is not null)
                {
                    for (var row = firstRow; row <= lastRow; row++)
                    {
                        if (_decoded.ContainsKey(row))
                            continue;
                        Cache(row, Decode(row).Meta);
                    }
                    return;
                }

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
    /// Ctrl+F backing scan (§7.5): streams <c>.meta.search</c>, one line per RECORD (Phase 3c —
    /// the file is record-indexed, like <c>.dat</c>/<c>.idx</c>, so it is never rewritten by a
    /// reorder). Matches the in-memory find exactly — substring on message or author, prefix on
    /// the SHA. For an identity-order store a record index IS the row; otherwise the hits are
    /// mapped back to rows via the inverse of the current order, built once per call (no
    /// persisted reverse map — the plan's own recommendation), and a record no longer referenced
    /// by the current order (superseded by a reorder, not yet compacted) is dropped.
    /// </summary>
    public IReadOnlyList<int> Search(string term)
    {
        var t = term.Trim().ToLowerInvariant();
        if (t.Length == 0 || _closed)
            return [];

        Dictionary<int, int>? recordToRow = null;
        if (_order is not null)
        {
            recordToRow = new Dictionary<int, int>(_order.Length);
            for (var row = 0; row < _order.Length; row++)
                recordToRow[_order[row]] = row;
        }

        var hits = new List<int>();
        try
        {
            var path = GraphCachePaths.File(_repoPath, GraphMetaStore.SearchExt);
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8);
            var record = -1;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                record++;
                if (record >= RecordCount)
                    break;
                var sep = line.IndexOf(GraphMetaStore.Sep);
                if (sep < 0)
                    continue;
                var sha = line.AsSpan(0, sep);
                var rest = line.AsSpan(sep + 1);
                if (!rest.Contains(t, StringComparison.Ordinal) && !sha.StartsWith(t, StringComparison.Ordinal))
                    continue;

                if (recordToRow is null)
                    hits.Add(record);
                else if (recordToRow.TryGetValue(record, out var row))
                    hits.Add(row);
            }
            if (recordToRow is not null)
                hits.Sort();
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

    /// <summary>Decodes ROW <paramref name="row"/> — translated to its record via
    /// <see cref="RecordOf"/> (identity unless an order file is loaded). A record's byte length
    /// is always bounded by the NEXT RECORD's offset regardless of row order, since
    /// <c>.meta.idx</c>/<c>.dat</c> are append-ordered by record, never by row.</summary>
    private (CommitMetadata Meta, ObjectId[] Parents) Decode(int row)
    {
        if (row < 0 || row >= _count)
            throw new ArgumentOutOfRangeException(nameof(row));
        var rec = RecordOf(row);
        if (rec < 0 || rec >= _offsets.Length)
            throw new InvalidDataException("graph meta order points at an unknown record");
        var start = _offsets[rec];
        var end = rec + 1 < _offsets.Length ? _offsets[rec + 1] : _datLength;
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

/// <summary>
/// Phase 3c (v10 graph memory footprint plan): the pure, storage-agnostic half of "reorder
/// instead of re-spill" — given the existing store's SHA -&gt; record-index table and a freshly
/// walked history in the new order, decides which rows can reuse an existing record and which
/// commits genuinely need a new one. Deliberately has no file or git access: the caller
/// (<see cref="GraphLayoutService"/>'s reorder job) is responsible for building
/// <paramref name="existingRecords"/> (<see cref="DiskCommitStore.BuildShaIndex"/>), streaming
/// the new walk, and afterwards appending <c>NewRecords</c> and writing the order — this class
/// only computes what those steps should be, so it can be unit-tested without a live git process
/// or real files, the same way <see cref="GraphMetaStore"/>'s own tests work against synthetic
/// records.
/// </summary>
internal static class GraphMetaReorder
{
    public readonly record struct Plan(int[] Order, IReadOnlyList<RawCommit> NewRecords);

    /// <summary>
    /// One-shot convenience for callers (and tests) that already have the whole new walk
    /// resident — equivalent to a single <see cref="GraphMetaReorderBuilder.MapBatch"/> call.
    /// <see cref="GraphLayoutService.RunHeadlessReorderAsync"/> uses the streaming builder
    /// directly instead (finding #5), so it never has to materialize the whole new history at
    /// once. <paramref name="existingRecords"/>: every commit sha already durable in the store,
    /// mapped to its record index (<see cref="DiskCommitStore.RecordCount"/> is the count BEFORE
    /// this call — new records are numbered starting there). <paramref name="newWalkCommits"/>:
    /// the new history, in the new walk's real row order. Returns the row -&gt; record order to
    /// write and the commits (in walk order) that need a brand-new record appended — a commit
    /// whose sha already has a record, whether from <paramref name="existingRecords"/> or from
    /// earlier in this very walk (a defensive case; the walk itself should never revisit a sha),
    /// is never duplicated.
    /// </summary>
    public static Plan Build(
        CompactShaIndex existingRecords, int existingRecordCount, IReadOnlyList<RawCommit> newWalkCommits)
    {
        var builder = new GraphMetaReorderBuilder(existingRecords, existingRecordCount);
        var newRecords = builder.MapBatch(newWalkCommits);
        return new Plan(builder.Order, newRecords);
    }
}

/// <summary>
/// Finding #5: a SHA -&gt; record-index table keyed on the first 8 bytes of the commit id (a
/// <c>long</c>) rather than its 40-char hex string — at scale the hex-string table was the actual
/// memory cost the review flagged (~150-180 MB at 1.5M records on linux), not the record indices
/// themselves. Every lookup still verifies the full id, so a prefix collision (two different
/// commits sharing the same first 8 bytes — astronomically unlikely for real SHA-1s, but not
/// provably impossible) is always resolved correctly via a small side list rather than silently
/// returning the wrong record.
/// </summary>
internal sealed class CompactShaIndex(int capacity = 0)
{
    private readonly Dictionary<long, (ObjectId Id, int Record)> _byPrefix = new(capacity);
    private List<(ObjectId Id, int Record)>? _collisions;

    public int Count { get; private set; }

    private static long Prefix(ObjectId id) => BitConverter.ToInt64(id.RawId, 0);

    public void Add(ObjectId id, int record)
    {
        Count++;
        var prefix = Prefix(id);
        if (_byPrefix.TryAdd(prefix, (id, record)))
            return;
        (_collisions ??= []).Add((id, record));
    }

    public bool TryGetValue(ObjectId id, out int record)
    {
        var prefix = Prefix(id);
        if (_byPrefix.TryGetValue(prefix, out var primary) && primary.Id.Equals(id))
        {
            record = primary.Record;
            return true;
        }
        if (_collisions is not null)
        {
            foreach (var (cid, crecord) in _collisions)
            {
                if (cid.Equals(id))
                {
                    record = crecord;
                    return true;
                }
            }
        }
        record = 0;
        return false;
    }
}

/// <summary>
/// Finding #5: the streaming half of "reorder instead of re-spill" — maps successive BATCHES of a
/// freshly walked history to records, so a caller never has to hold the whole new walk resident
/// (the old, non-streaming shape held ~490 MB of <see cref="RawCommit"/>s on linux plus the SHA
/// table finding #5 also fixes). Deliberately has no file or git access, like the one-shot
/// <see cref="GraphMetaReorder.Build"/> it generalizes — the caller
/// (<see cref="GraphLayoutService.RunHeadlessReorderAsync"/>) is responsible for calling
/// <see cref="MapBatch"/> once per walked batch, durably appending each batch's returned new
/// records (<see cref="GraphMetaWriter.AppendReorderBatch"/>) before asking for the next batch,
/// and finally writing <see cref="Order"/> once the walk is exhausted.
/// </summary>
internal sealed class GraphMetaReorderBuilder
{
    private readonly CompactShaIndex _existingRecords;
    private readonly CompactShaIndex _assignedThisWalk = new();
    private readonly List<int> _order = [];
    private int _nextRecord;

    public GraphMetaReorderBuilder(CompactShaIndex existingRecords, int existingRecordCount)
    {
        _existingRecords = existingRecords;
        _nextRecord = existingRecordCount;
    }

    /// <summary>Total new records assigned across every <see cref="MapBatch"/> call so far.</summary>
    public int NewRecordCount { get; private set; }

    /// <summary>The row -&gt; record order accumulated across every <see cref="MapBatch"/> call so
    /// far, in walk order. Only meaningful once the whole walk is exhausted.</summary>
    public int[] Order => [.. _order];

    /// <summary>Maps one batch of freshly walked commits (in walk order) to records — reusing an
    /// existing record by id where possible (from <paramref name="batch"/>'s own earlier commits
    /// in this same walk, or from the store this reorder started from), otherwise assigning a new
    /// one — and returns just the commits in THIS batch that need a brand-new record (the caller
    /// appends those; an already-known commit needs nothing written).</summary>
    public List<RawCommit> MapBatch(IReadOnlyList<RawCommit> batch)
    {
        var newRecords = new List<RawCommit>();
        foreach (var commit in batch)
        {
            if (_existingRecords.TryGetValue(commit.Id, out var rec) || _assignedThisWalk.TryGetValue(commit.Id, out rec))
            {
                _order.Add(rec);
                continue;
            }
            rec = _nextRecord++;
            _assignedThisWalk.Add(commit.Id, rec);
            _order.Add(rec);
            newRecords.Add(commit);
        }
        NewRecordCount += newRecords.Count;
        return newRecords;
    }
}
