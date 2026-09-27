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
/// One lock object per repo cache stem, shared by
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
/// On-disk, random-access store for the graph's per-commit metadata. When the
/// "Read commit details from disk" setting is on, <see cref="GraphLayoutService"/> spills the
/// laid-out rows here and points <see cref="GraphLayout.Commits"/> at a
/// <see cref="DiskCommitStore"/>, so only the fixed-size layout arrays stay resident on a
/// large history. Siblings per repo:
/// <list type="bullet">
/// <item><c>.meta.hdr</c> — JSON: version, walk key, remote-branch flag, row count, record
/// count, order version, exhausted.</item>
/// <item><c>.meta.dat</c> — one binary RECORD per distinct commit (sha, three strings, two
/// timestamps, parent count) — append-only, never rewritten by a ref change.</item>
/// <item><c>.meta.idx</c> — one <see cref="long"/> per RECORD: its start offset in
/// <c>.meta.dat</c>.</item>
/// <item><c>.meta.search</c> — one lowercased line per RECORD (<c>sha ␟ message ␟ author</c>)
/// for Ctrl+F.</item>
/// <item><c>.meta.order.&lt;n&gt;</c> (only once a ref change has actually reordered
/// history) — one <see cref="int"/> per ROW: which record occupies that row. Absent
/// (<c>OrderVersion == 0</c>) means the identity order — row <c>i</c> IS record <c>i</c>, exactly
/// the common case, and the only one before any reorder has ever run.</item>
/// <item><c>.meta.tips</c> — the sorted tip SHAs (20 bytes each) the current order was
/// built from; not versioned, always describes the CURRENT order.</item>
/// </list>
/// All writes are append-only and best-effort; any read fault downgrades the caller to the
/// in-memory path.
/// </summary>
internal sealed class GraphMetaStore
{
    // Format version history:
    // 2: Each .dat record carries ParentCount parent SHAs so the store can seed
    //    TryOptimisticSnapshot's first paint on its own, without depending on the walk cache.
    // 3: WalkEngine in header — CLI streaming and libgit2 can order rows differently.
    // 4: Keyed on canonical sorted tip order in lockstep with GraphWalkCache.Version.
    // 5: Added RecordCount and OrderVersion to the header for order indirection.
    // 6: rows in commit-date order (`git log --date-order`); v5 stores were topo-ordered.
    internal const int Version = 6;
    internal const string HdrExt = ".meta.hdr";
    internal const string DatExt = ".meta.dat";
    internal const string IdxExt = ".meta.idx";
    internal const string SearchExt = ".meta.search";
    internal const string TipsExt = ".meta.tips";

    /// <summary>Field separator in <c>.meta.search</c> lines — US (unit separator).</summary>
    internal const char Sep = (char)0x1f;

    internal static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    /// <summary>The versioned order file for this repo — <c>version == 0</c> never
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

        /// <summary>Total distinct commit records ever appended to <c>.meta.dat</c> —
        /// equals <see cref="RowCount"/> until the first reorder, after which it can exceed it
        /// (new commits get new records; unreachable ones' old records just aren't referenced by
        /// the current order any more, until compaction).</summary>
        public int RecordCount { get; set; }

        /// <summary>Which <c>.meta.order.&lt;n&gt;</c> file maps rows to records.
        /// <c>0</c> means identity — row <c>i</c> is record <c>i</c>, no order file exists.</summary>
        public int OrderVersion { get; set; }

        /// <summary>How long the most recent walk-from-row-0 to
        /// exhaustion took, in milliseconds — <see cref="GraphMetaSplice.Evaluate"/>'s guard 4
        /// budget. <c>0</c> (the default for every store written before this field existed, or one
        /// whose disk cache was wiped) means "unknown", which guard 4 FAILS CLOSED on rather than
        /// skipping the check — see that method's doc comment. Additive field: an older reader
        /// ignores it (unknown JSON members are ignored by <c>System.Text.Json</c> by default), so
        /// no <see cref="Version"/> bump.</summary>
        public long LastFullWalkMs { get; set; }
    }

    /// <summary>
    /// Opens the on-disk store for <paramref name="repoPath"/> if it is present, was written by
    /// the current walk (<see cref="GraphWalkCache.CliWalkMarker"/>), matches <paramref
    /// name="fingerprint"/> / <paramref name="includeRemoteBranches"/>, and already holds at least
    /// <paramref name="wantCount"/> rows (or is exhausted). Returns null on any miss — a fresh
    /// walk then re-spills.
    /// </summary>
    public static DiskCommitStore? TryOpen(
        string repoPath, long fingerprint, bool includeRemoteBranches, int wantCount)
    {
        try
        {
            var hdrPath = GraphCachePaths.File(repoPath, HdrExt);
            if (!File.Exists(hdrPath))
                return null;

            var hdr = JsonSerializer.Deserialize<Header>(ReadAllBytesShared(hdrPath), Json);
            if (hdr is null
                || hdr.Version != Version
                || hdr.Fingerprint != fingerprint
                || hdr.IncludeRemoteBranches != includeRemoteBranches
                || hdr.WalkEngine != GraphWalkCache.CliWalkMarker)
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
    /// match this fingerprint / remote-branch flag, written by the current walk? Returns its row
    /// count and exhausted flag. No file handle is opened (unlike <see cref="TryOpen"/>).</summary>
    public static (bool Ok, int RowCount, bool Exhausted) Probe(
        string repoPath, long fingerprint, bool includeRemoteBranches)
    {
        try
        {
            var hdrPath = GraphCachePaths.File(repoPath, HdrExt);
            if (!File.Exists(hdrPath))
                return (false, 0, false);
            var hdr = JsonSerializer.Deserialize<Header>(ReadAllBytesShared(hdrPath), Json);
            if (hdr is null || hdr.Version != Version || hdr.Fingerprint != fingerprint
                || hdr.IncludeRemoteBranches != includeRemoteBranches || hdr.WalkEngine != GraphWalkCache.CliWalkMarker)
                return (false, 0, false);
            return (true, hdr.RowCount, hdr.Exhausted);
        }
        catch
        {
            return (false, 0, false);
        }
    }

    /// <summary>
    /// Like <see cref="Probe"/> but WITHOUT a fingerprint check — for
    /// <c>TryOptimisticSnapshot</c>'s stale-tolerant first paint, which runs before the fresh
    /// ref-fingerprint check (a ref snapshot read, possibly a <c>for-each-ref</c>) even starts. The caller must
    /// treat the result as provisional, exactly like <see cref="GraphWalkCache.TryLoadStale"/>.
    /// Still requires <see cref="GraphWalkCache.CliWalkMarker"/>, so an optimistic frame is never
    /// seeded from a store a retired walk engine wrote.
    /// </summary>
    public static (bool Ok, int RowCount) ProbeAny(string repoPath, bool includeRemoteBranches)
    {
        try
        {
            var hdrPath = GraphCachePaths.File(repoPath, HdrExt);
            if (!File.Exists(hdrPath))
                return (false, 0);
            var hdr = JsonSerializer.Deserialize<Header>(ReadAllBytesShared(hdrPath), Json);
            if (hdr is null || hdr.Version != Version || hdr.IncludeRemoteBranches != includeRemoteBranches
                || hdr.WalkEngine != GraphWalkCache.CliWalkMarker || hdr.RowCount <= 0)
                return (false, 0);
            return (true, hdr.RowCount);
        }
        catch
        {
            return (false, 0);
        }
    }

    /// <summary>
    /// Like <see cref="ProbeAny"/> but returns the full header, including the WALK KEY
    /// it was written under — used by the headless per-tab pipeline to tell "no store at all"
    /// apart from "a store exists, but its walk key has moved" (a reorder opportunity)
    /// without needing to know the current walk key up front.
    /// </summary>
    public static (bool Ok, long WalkKey, int RowCount, int RecordCount, int OrderVersion, bool Exhausted, long LastFullWalkMs) ProbeExisting(
        string repoPath, bool includeRemoteBranches)
    {
        try
        {
            var hdrPath = GraphCachePaths.File(repoPath, HdrExt);
            if (!File.Exists(hdrPath))
                return (false, 0, 0, 0, 0, false, 0);
            var hdr = JsonSerializer.Deserialize<Header>(ReadAllBytesShared(hdrPath), Json);
            if (hdr is null || hdr.Version != Version || hdr.IncludeRemoteBranches != includeRemoteBranches
                || hdr.WalkEngine != GraphWalkCache.CliWalkMarker)
                return (false, 0, 0, 0, 0, false, 0);
            return (true, hdr.Fingerprint, hdr.RowCount, hdr.RecordCount, hdr.OrderVersion, hdr.Exhausted, hdr.LastFullWalkMs);
        }
        catch
        {
            return (false, 0, 0, 0, 0, false, 0);
        }
    }

    /// <summary>Reads the sorted tip SHAs the CURRENT order was built from —
    /// <c>.meta.tips</c>, written by <see cref="GraphMetaWriter.WriteTips"/>. Returns null if the
    /// file is missing (a store that has never had its order committed with a known tip set — a
    /// never-reordered store) or unreadable; <see
    /// cref="GraphMetaSplice.Evaluate"/>'s guard 1 treats that as "tips don't match" (reject).</summary>
    public static IReadOnlyList<GitObjectId>? ReadTips(string repoPath)
    {
        try
        {
            var path = GraphCachePaths.File(repoPath, TipsExt);
            if (!File.Exists(path))
                return null;
            var bytes = ReadAllBytesShared(path);
            if (bytes.Length % 20 != 0)
                return null;
            var tips = new GitObjectId[bytes.Length / 20];
            for (var i = 0; i < tips.Length; i++)
                tips[i] = new GitObjectId(bytes.AsSpan(i * 20, 20));
            return tips;
        }
        catch
        {
            return null;
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

    /// <summary>Deletes every <c>.meta.order.&lt;n&gt;</c> file for this repo strictly
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

    /// <summary>
    /// Reads all bytes from <paramref name="path"/> sharing both ReadWrite and Delete.
    /// Every reader of a <c>.meta.*</c> file must share Delete, or <see cref="GraphMetaWriter.Reset"/>
    /// cannot retire open files away before starting a fresh from-row-0 spill.
    /// </summary>
    internal static byte[] ReadAllBytesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[fs.Length];
        var read = 0;
        while (read < bytes.Length)
        {
            var n = fs.Read(bytes, read, bytes.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return read == bytes.Length ? bytes : bytes[..read];
    }

    /// <summary>
    /// Measures the unreachable dead prefix at the front of <c>.meta.dat</c>.
    /// Record 0's offset (the first 8 bytes of <c>.meta.idx</c>) indicates how many dead bytes
    /// precede record 0. Returns <c>(0, 0)</c> on any miss, unreadable file, or empty file.
    /// </summary>
    internal static (long DeadBytes, long DatLength) ProbeDeadPrefix(string repoPath)
    {
        try
        {
            var idxPath = GraphCachePaths.File(repoPath, IdxExt);
            var datPath = GraphCachePaths.File(repoPath, DatExt);
            if (!File.Exists(idxPath) || !File.Exists(datPath))
                return (0, 0);

            var buf = new byte[8];
            using (var fs = new FileStream(idxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var read = 0;
                while (read < 8)
                {
                    var n = fs.Read(buf, read, 8 - read);
                    if (n <= 0) break;
                    read += n;
                }
                if (read < 8)
                    return (0, 0);
            }

            var deadBytes = BitConverter.ToInt64(buf, 0);
            if (deadBytes < 0)
                return (0, 0);

            long datLen;
            using (var datFs = new FileStream(datPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                datLen = datFs.Length;
            }

            return (deadBytes, datLen);
        }
        catch
        {
            return (0, 0);
        }
    }
}

/// <summary>
/// Append-only writer for a repo's <c>.meta.*</c> set. <see cref="Reset"/> retires existing
/// sibling files for a fresh walk without leaving a dead prefix; <see cref="Append"/> adds
/// laid-out rows; <see cref="Commit"/> rewrites the header. Every reader of a <c>.meta.*</c>
/// file must share <see cref="FileShare.Delete"/>, or <see cref="Reset"/> cannot retire it.
/// All operations swallow IO errors — the cache is an optimisation, never a correctness dependency.
/// </summary>
internal sealed class GraphMetaWriter(string repoPath, long fingerprint, bool includeRemoteBranches)
{
    private readonly string _hdr = GraphCachePaths.File(repoPath, GraphMetaStore.HdrExt);
    private readonly string _dat = GraphCachePaths.File(repoPath, GraphMetaStore.DatExt);
    private readonly string _idx = GraphCachePaths.File(repoPath, GraphMetaStore.IdxExt);
    private readonly string _search = GraphCachePaths.File(repoPath, GraphMetaStore.SearchExt);
    private readonly string _tips = GraphCachePaths.File(repoPath, GraphMetaStore.TipsExt);

    /// <summary>
    /// Retires every sibling file for this repository so a from-row-0 re-spill starts the store
    /// over at byte 0 without dead-prefix bloat. Every reader shares
    /// <see cref="FileShare.Delete"/>, so renaming each file to a <c>.retired</c> name and then
    /// deleting it frees the original path immediately while active readers (such as the painted
    /// graph) keep reading their open handles until they close.
    /// <para>
    /// Returns <c>true</c> only if <c>.hdr</c>, <c>.dat</c>, <c>.idx</c>, <c>.search</c>, and
    /// <c>.tips</c> no longer exist. If any primary file remains (e.g. a foreign process or
    /// non-sharing handle blocked retirement), any moved files are rolled back and <c>false</c>
    /// is returned so <see cref="AppendSpillBatch"/> can abort rather than appending after stale
    /// bytes, leaving the old store intact.
    /// </para>
    /// <para>
    /// Note: on Linux and macOS, renaming an open file always succeeds and <see cref="FileShare.Delete"/>
    /// is a no-op, so the same code path holds across all platforms.
    /// </para>
    /// </summary>
    public bool Reset()
    {
        try
        {
            var cacheDir = AppPaths.CacheDirectory;
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
                return true;
            }

            var stem = GraphCachePaths.Stem(repoPath);

            // Sweep leftover retired files for this stem from prior incomplete runs.
            SweepRetiredFiles(cacheDir, stem);

            // Retire primary files. If any primary file fails to move (e.g. foreign handle),
            // roll back any files moved so far so the old store remains intact.
            var primaryFiles = new[] { _dat, _idx, _search, _tips, _hdr };
            var moved = new List<(string Original, string Retired)>();
            var allMoved = true;

            foreach (var p in primaryFiles)
            {
                if (!File.Exists(p))
                    continue;

                var retiredPath = $"{p}.{Guid.NewGuid():N}.retired";
                try
                {
                    File.Move(p, retiredPath);
                    moved.Add((p, retiredPath));
                }
                catch
                {
                    allMoved = false;
                    break;
                }
            }

            if (!allMoved)
            {
                foreach (var (orig, ret) in moved)
                {
                    try { File.Move(ret, orig); } catch { }
                }
                return false;
            }

            foreach (var (_, ret) in moved)
            {
                try { File.Delete(ret); } catch { /* OS removes on handle close or sweep cleans up */ }
            }

            RetireOrderFiles(cacheDir, stem);

            return !File.Exists(_hdr)
                && !File.Exists(_dat)
                && !File.Exists(_idx)
                && !File.Exists(_search)
                && !File.Exists(_tips);
        }
        catch
        {
            return false;
        }
    }

    private static void SweepRetiredFiles(string cacheDir, string stem)
    {
        try
        {
            var dir = new DirectoryInfo(cacheDir);
            if (!dir.Exists)
                return;
            foreach (var f in dir.GetFiles($"{stem}.meta.*.retired"))
            {
                try { f.Delete(); }
                catch { /* best effort sweep; skip locked */ }
            }
        }
        catch { }
    }

    private static void RetireFile(string path)
    {
        if (!File.Exists(path))
            return;

        var retiredPath = $"{path}.{Guid.NewGuid():N}.retired";
        try
        {
            File.Move(path, retiredPath);
            try { File.Delete(retiredPath); } catch { /* OS removes on handle close or sweep cleans up */ }
        }
        catch
        {
            // Move failed (e.g. non-sharing lock or foreign handle): fall back to direct delete.
            try { File.Delete(path); } catch { }
        }
    }

    private static void RetireOrderFiles(string cacheDir, string stem)
    {
        try
        {
            var dir = new DirectoryInfo(cacheDir);
            if (!dir.Exists)
                return;
            foreach (var f in dir.GetFiles($"{stem}.meta.order.*"))
            {
                if (f.Name.EndsWith(".retired", StringComparison.OrdinalIgnoreCase))
                    continue;
                RetireFile(f.FullName);
            }
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
    /// (no projection copy) — this is what the spill uses, and what makes the store usable
    /// as an optimistic-snapshot source (v10.1.2).</summary>
    public void Append(IReadOnlyList<RawCommit> rows, int start, int count)
        => Append(i => rows[i].Metadata, i => rows[i].ParentIds, start, count);

    private void Append(Func<int, CommitMetadata> get, Func<int, IReadOnlyList<GitObjectId>?> getParents, int start, int count)
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
                        bw.Write(parents[p].AsSpan());
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
    /// <see cref="Append"/>(s).
    /// <paramref name="fullWalkElapsed"/>: pass the wall time of a walk that
    /// started at row 0 and just reached <paramref name="exhausted"/> — recorded as the header's
    /// <c>LastFullWalkMs</c>. <c>null</c> (every other call — a mid-walk batch, or a resumed
    /// partial walk) leaves whatever value is already on disk untouched.</summary>
    public void Commit(int rowCount, bool exhausted, TimeSpan? fullWalkElapsed = null) =>
        WriteHeader(rowCount, recordCount: rowCount, orderVersion: 0, exhausted, fullWalkElapsed);

    /// <summary>Rewrites the header to point at a new order version, after
    /// <see cref="WriteOrder"/> has written it and any genuinely-new commits have already been
    /// appended via <see cref="Append(IReadOnlyList{RawCommit},int,int)"/>. <paramref
    /// name="recordCount"/> is the TOTAL distinct records now in <c>.meta.dat</c> (can exceed
    /// <paramref name="rowCount"/> once history has been rewritten). Call after
    /// <see cref="WriteOrder"/> and <see cref="WriteTips"/> — this is the atomic switch-over: a
    /// reader that opened the store under the old header keeps reading the old order until it
    /// reopens.</summary>
    /// <summary>Never re-times a reorder's own re-walk here — carries whatever
    /// <c>LastFullWalkMs</c> the OLD header already had forward unchanged (via
    /// <see cref="WriteHeader"/>'s <c>fullWalkElapsed: null</c>), so a store's guard-4 baseline
    /// survives every reorder until a fresh from-row-0 spool times a real one.</summary>
    public void CommitReorder(int rowCount, int recordCount, int orderVersion, bool exhausted) =>
        WriteHeader(rowCount, recordCount, orderVersion, exhausted, fullWalkElapsed: null);

    private void WriteHeader(int rowCount, int recordCount, int orderVersion, bool exhausted, TimeSpan? fullWalkElapsed)
    {
        try
        {
            var lastFullWalkMs = fullWalkElapsed is { } elapsed && elapsed > TimeSpan.Zero
                ? (long)elapsed.TotalMilliseconds
                : ReadCurrentLastFullWalkMs();
            var hdr = new GraphMetaStore.Header
            {
                Version = GraphMetaStore.Version,
                Fingerprint = fingerprint,
                IncludeRemoteBranches = includeRemoteBranches,
                RowCount = rowCount,
                RecordCount = recordCount,
                OrderVersion = orderVersion,
                Exhausted = exhausted,
                WalkEngine = GraphWalkCache.CliWalkMarker,
                LastFullWalkMs = lastFullWalkMs,
            };
            var tmp = $"{_hdr}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(hdr, GraphMetaStore.Json));
            File.Move(tmp, _hdr, overwrite: true);
        }
        catch { }
    }

    /// <summary>Reads <c>LastFullWalkMs</c> off whatever header is currently on disk for THIS
    /// writer's own fingerprint — 0 if absent, unreadable, or written under a different walk key
    /// (a fresh walk key has no baseline of its own yet). Called under
    /// <see cref="GraphCacheLocks"/> by every <see cref="WriteHeader"/> call that isn't itself
    /// recording a fresh timing, so the field survives every ordinary header rewrite (a mid-walk
    /// spill batch, a reorder) instead of silently resetting to 0.</summary>
    private long ReadCurrentLastFullWalkMs()
    {
        try
        {
            if (!File.Exists(_hdr))
                return 0;
            var existing = JsonSerializer.Deserialize<GraphMetaStore.Header>(GraphMetaStore.ReadAllBytesShared(_hdr), GraphMetaStore.Json);
            return existing is not null && existing.Fingerprint == fingerprint ? existing.LastFullWalkMs : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Writes a new, versioned row -&gt; record order file. Doesn't touch the
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

    /// <summary>Writes the sorted tip SHAs the CURRENT order was built from — read back
    /// by a future reorder/splice to decide whether it can trust the existing order as a
    /// starting point. Not versioned; always describes whatever order the header currently
    /// points at.</summary>
    public void WriteTips(IReadOnlyList<GitObjectId> sortedTips)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDirectory);
            var bytes = new byte[sortedTips.Count * 20];
            for (var i = 0; i < sortedTips.Count; i++)
                sortedTips[i].AsSpan().CopyTo(bytes.AsSpan(i * 20, 20));
            var tmp = $"{_tips}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, _tips, overwrite: true);
        }
        catch { }
    }

    private static void WriteSha(BinaryWriter bw, GitObjectId id) => bw.Write(id.AsSpan());

    private static void WriteWhen(BinaryWriter bw, DateTimeOffset when)
    {
        bw.Write(when.Ticks);                             // local-clock ticks
        bw.Write((short)when.Offset.TotalMinutes);        // zone offset
    }

    private static string Clean(string s)
        => s.Replace('\n', ' ').Replace('\r', ' ').Replace(GraphMetaStore.Sep, ' ');

    /// <summary>Appends a spill batch under this repo's <see cref="GraphCacheLocks"/>
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
    public (SpillOutcome Outcome, int RowCount) AppendSpillBatch(
        IReadOnlyList<RawCommit> rows, int from, int to, bool exhausted,
        TimeSpan? fullWalkElapsed = null, IReadOnlyList<GitObjectId>? sortedTips = null)
    {
        lock (GraphCacheLocks.For(repoPath))
        {
            var existing = GraphMetaStore.ProbeExisting(repoPath, includeRemoteBranches);
            var matchesKey = existing.Ok && existing.WalkKey == fingerprint;

            if (!matchesKey)
            {
                if (from != 0)
                    return (SpillOutcome.Conflict, existing.RowCount);
                if (!Reset())
                {
                    Diagnostics.PerfLog.Mark($"graph cache reset blocked ({repoPath})");
                    return (SpillOutcome.Conflict, existing.RowCount);
                }
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
            Commit(to, exhausted, fullWalkElapsed);
            // Without this, a store's tips are only ever written by
            // a REORDER (TryCommitReorder) — a never-reordered store (the common case for a repo
            // opened once and never re-walked since) would have no `.meta.tips` at all, so guard 1
            // could never pass for it. Writing tips here too, the first time a plain spill reaches
            // exhaustion, gives every exhausted store a valid tip set to splice from later.
            if (exhausted && sortedTips is { Count: > 0 })
                WriteTips(sortedTips);
            return (SpillOutcome.Appended, to);
        }
    }

    /// <summary>Streaming reorder: appends one batch of brand-new records a reorder
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
            var existing = GraphMetaStore.ProbeExisting(repoPath, includeRemoteBranches);
            if (!existing.Ok || existing.WalkKey != expectedWalkKey)
                return false;
            Append(newRecords, 0, newRecords.Count);
            return true;
        }
    }

    /// <summary>The reorder's atomic switch-over, under this repo's
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
        int newRecordCount, int[] order, IReadOnlyList<GitObjectId> sortedTips)
    {
        lock (GraphCacheLocks.For(repoPath))
        {
            var probe = GraphMetaStore.ProbeExisting(repoPath, includeRemoteBranches);
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

/// <summary>Outcome of <see cref="GraphMetaWriter.AppendSpillBatch"/>.</summary>
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

    /// <summary>[row] -&gt; record index, or null for the identity order (row IS
    /// record — every store before its first reorder, and the overwhelming majority always).
    /// Loaded once at <see cref="Open"/> and never mutated in place — a reorder always produces a
    /// freshly-opened store (see <c>GraphLayoutService</c>'s reorder job), so a reader never needs
    /// to see an order file change under it.</summary>
    private readonly int[]? _order;

    private readonly Dictionary<int, CommitMetadata> _decoded = new(DecodeCacheCap);
    private readonly Queue<int> _decodedOrder = new(DecodeCacheCap);
    private readonly object _sync = new();

    // Lease count: the RepoCache that opened the store holds the first lease; every
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
        Id = GitObjectId.Zero,
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
    /// caller, and the only shape <see cref="SetCount"/>
    /// supports growing in place.</summary>
    public static DiskCommitStore Open(string repoPath, int rowCount) => Open(repoPath, rowCount, rowCount, orderVersion: 0);

    /// <summary>Opens a store whose current header names an explicit order version.
    /// <paramref name="recordCount"/> sizes the <c>.idx</c>/offset table (records, not rows);
    /// <paramref name="orderVersion"/> 0 means identity (no order file to load).</summary>
    public static DiskCommitStore Open(string repoPath, int rowCount, int recordCount, int orderVersion)
    {
        var datPath = GraphCachePaths.File(repoPath, GraphMetaStore.DatExt);
        var idxPath = GraphCachePaths.File(repoPath, GraphMetaStore.IdxExt);
        var offsets = ReadOffsets(idxPath, recordCount);
        var dat = File.OpenHandle(datPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
        var bytes = GraphMetaStore.ReadAllBytesShared(orderPath);
        var n = Math.Min(rowCount, bytes.Length / 4);
        var order = new int[n];
        Buffer.BlockCopy(bytes, 0, order, 0, n * 4);
        return order;
    }

    /// <summary>Adopt rows appended since <see cref="Open"/> (called after an Extend spill).
    /// Reads only the <c>.idx</c> bytes appended since the last call instead of
    /// re-reading the whole file — the existing offsets are kept and only the new tail is appended.
    /// Only valid for an identity-order store (<see cref="_order"/> null) — a store
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

    /// <summary>Total distinct commit records in <c>.meta.dat</c> right now (can exceed
    /// <see cref="Count"/> once history has been reordered — old records superseded by a reorder
    /// stay in the file, unreferenced, until compaction).</summary>
    public int RecordCount => _offsets.Length;

    /// <summary>Row <paramref name="row"/>'s record index — identity unless an order file is
    /// loaded.</summary>
    private int RecordOf(int row) => _order is null ? row : _order[row];

    /// <summary>The full row -&gt; record order this store was opened with, as a
    /// fresh array (safe for a caller to hand to <see cref="GraphMetaSplice.Build"/>, which never
    /// mutates <see cref="_order"/> in place — see that field's own doc comment on why a reorder
    /// always opens a freshly-loaded store instead). Identity (<c>row == record</c>) when this
    /// store has no order file (<c>OrderVersion</c> 0).</summary>
    public int[] ToOrderArray() => _order is not null ? [.. _order] : [.. Enumerable.Range(0, _count)];

    /// <summary>Builds a SHA -&gt; record-index table by reading just the leading
    /// 20-byte SHA of every existing record (skips message/author/parent bytes entirely) — used
    /// by the reorder job to find which commits in a freshly walked history already have a
    /// durable record, so it only appends genuinely new ones. One small read per record; this is
    /// a background, gated, lowest-priority job (never on the render thread), so it trades read
    /// count for simplicity rather than batching.
    /// <para>
    /// Keyed by <see cref="CompactShaIndex"/> (an 8-byte prefix per entry, verified
    /// against the full id on any hit) rather than the 40-char hex string — at
    /// 1.5M records on linux a hex-string table holds ~150-180 MB; this holds meaningfully
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
                index.Add(new GitObjectId(shaBuf), rec);
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

    /// <summary>Reads rows [<paramref name="first"/>, <paramref name="first"/> +
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
                // An explicit order means consecutive ROWS need not be consecutive
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

    /// <summary>Decodes row <paramref name="index"/> WITH its parent SHAs (persisted
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
                // Same "identity order only" restriction as ReadRawRange — a reordered
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
    /// Ctrl+F backing scan: streams <c>.meta.search</c>, one line per RECORD (
    /// the file is record-indexed, like <c>.dat</c>/<c>.idx</c>, so it is never rewritten by a
    /// reorder). Matches the in-memory find exactly — substring on message or author, prefix on
    /// the SHA. For an identity-order store a record index IS the row; otherwise the hits are
    /// mapped back to rows via the inverse of the current order, built once per call (no
    /// persisted reverse map), and a record no longer referenced
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
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);
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
    /// more than once, and never while a lease is still outstanding. Readers keep reading through
    /// their open handles even if <see cref="GraphMetaWriter.Reset"/> has retired and deleted the
    /// file on disk; the OS frees the disk allocation once the last handle closes.</summary>
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
        var bytes = GraphMetaStore.ReadAllBytesShared(idxPath);
        var n = Math.Min(rowCount, bytes.Length / 8);
        var offsets = new long[n];
        for (var i = 0; i < n; i++)
            offsets[i] = BitConverter.ToInt64(bytes, i * 8);
        return offsets;
    }

    /// <summary>Reads only the offsets for rows [<paramref name="fromRow"/>,
    /// <paramref name="rowCount"/>) — the tail appended since the caller last read this file —
    /// instead of <see cref="ReadOffsets"/>'s whole-file read.</summary>
    private static long[] ReadOffsetsFrom(string idxPath, int fromRow, int rowCount)
    {
        using var fs = new FileStream(idxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
    private (CommitMetadata Meta, GitObjectId[] Parents) Decode(int row)
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
    private static (CommitMetadata Meta, GitObjectId[] Parents) DecodeFrom(ReadOnlySpan<byte> record)
    {
        using var ms = new MemoryStream(record.ToArray(), writable: false);
        using var br = new BinaryReader(ms, new UTF8Encoding(false));
        var sha = br.ReadBytes(20);
        var id = new GitObjectId(sha);
        var msg = br.ReadString();
        var author = br.ReadString();
        var email = br.ReadString();
        var authorWhen = ReadWhen(br);
        var commitWhen = ReadWhen(br);
        var parentCount = br.ReadInt32();
        var parents = new GitObjectId[parentCount];
        for (var i = 0; i < parentCount; i++)
            parents[i] = new GitObjectId(br.ReadBytes(20));
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
/// The pure, storage-agnostic half of "reorder
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
    /// directly instead, so it never has to materialize the whole new history at
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
/// A SHA -&gt; record-index table keyed on the first 8 bytes of the commit id (a
/// <c>long</c>) rather than its 40-char hex string — at scale the hex-string table was the actual
/// dominant memory cost (~150-180 MB at 1.5M records on linux), not the record indices
/// themselves. Every lookup still verifies the full id, so a prefix collision (two different
/// commits sharing the same first 8 bytes — astronomically unlikely for real SHA-1s, but not
/// provably impossible) is always resolved correctly via a small side list rather than silently
/// returning the wrong record.
/// </summary>
internal sealed class CompactShaIndex(int capacity = 0)
{
    private readonly Dictionary<long, (GitObjectId Id, int Record)> _byPrefix = new(capacity);
    private List<(GitObjectId Id, int Record)>? _collisions;

    public int Count { get; private set; }

    private static long Prefix(GitObjectId id) => BitConverter.ToInt64(id.AsSpan());

    public void Add(GitObjectId id, int record)
    {
        Count++;
        var prefix = Prefix(id);
        if (_byPrefix.TryAdd(prefix, (id, record)))
            return;
        (_collisions ??= []).Add((id, record));
    }

    public bool TryGetValue(GitObjectId id, out int record)
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
/// The streaming half of "reorder instead of re-spill" — maps successive BATCHES of a
/// freshly walked history to records, so a caller never has to hold the whole new walk resident
/// (the old, non-streaming shape held ~490 MB of <see cref="RawCommit"/>s on linux plus the SHA
/// table). Deliberately has no file or git access, like the one-shot
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
