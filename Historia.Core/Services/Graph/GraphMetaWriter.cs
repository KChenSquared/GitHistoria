namespace Historia.Services.Graph;

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
    private int _hashSize;

    // The incarnation this writer last wrote or saw on disk; written into every header. Guarded by
    // this repo's GraphCacheLocks lock.
    private string _generation = "";

    public int HashSize => _hashSize > 0 ? _hashSize : EnsureHashSize();

    /// <summary>The store incarnation this writer last wrote or saw under its walk key — what a
    /// reader opened on the same files reports as <see cref="DiskCommitStore.Generation"/>. A
    /// reader whose generation differs holds files that have since been replaced.</summary>
    public string Generation
    {
        get
        {
            lock (GraphCacheLocks.For(repoPath))
                return _generation;
        }
    }

    /// <summary>Adopts the incarnation of the header on disk when it belongs to
    /// <paramref name="walkKey"/>: another writer may have reset the store and come back to this
    /// key, and a header written with a stale incarnation would make readers of the new files
    /// think they were replaced. Caller holds this repo's <see cref="GraphCacheLocks"/> lock.</summary>
    private void AdoptHeaderGeneration(long walkKey)
    {
        var probe = GraphMetaStore.ProbeGeneration(repoPath, includeRemoteBranches);
        if (probe.Ok && probe.WalkKey == walkKey)
            _generation = probe.Generation;
    }

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
        _hashSize = 0;
        try
        {
            var cacheDir = AppPaths.CacheDirectory;
            if (!Directory.Exists(cacheDir))
            {
                Directory.CreateDirectory(cacheDir);
                _generation = Guid.NewGuid().ToString("N");
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

            var cleared = !File.Exists(_hdr)
                && !File.Exists(_dat)
                && !File.Exists(_idx)
                && !File.Exists(_search)
                && !File.Exists(_tips);
            if (cleared)
                _generation = Guid.NewGuid().ToString("N");
            return cleared;
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
    /// <see cref="CommitMetadata"/> list — each parent slot is written as a zero SHA
    /// (<see cref="ReadRaw"/> only ever reads real spill data via the <see cref="RawCommit"/>
    /// overload below, so this is harmless; for callers and tests that hold only metadata).</summary>
    public void Append(IReadOnlyList<CommitMetadata> rows, int start, int count)
        => Append(i => rows[i], _ => null, start, count);

    /// <summary>Overload that reads metadata + real parent SHAs straight off the walk records
    /// (no projection copy) — this is what the spill uses, and what makes the store usable
    /// as an optimistic-snapshot source.</summary>
    public void Append(IReadOnlyList<RawCommit> rows, int start, int count)
        => Append(i => rows[i].Metadata, i => rows[i].ParentIds, start, count);

    private int EnsureHashSize()
    {
        if (_hashSize != 0)
            return _hashSize;
        if (File.Exists(_hdr))
        {
            try
            {
                var hdr = JsonSerializer.Deserialize<GraphMetaStore.Header>(GraphMetaStore.ReadAllBytesShared(_hdr), GraphMetaStore.Json);
                if (hdr is not null)
                    _hashSize = hdr.HashSize == 0 ? 20 : hdr.HashSize;
            }
            catch { }
        }
        return _hashSize;
    }

    private void Append(Func<int, CommitMetadata> get, Func<int, IReadOnlyList<GitObjectId>?> getParents, int start, int count)
    {
        if (count <= 0)
            return;

        EnsureHashSize();

        for (var i = 0; i < count; i++)
        {
            var m = get(start + i);
            var idSize = m.Id.HashSize;
            if (idSize != 20 && idSize != 32)
                throw new StoreInvalidException($"Invalid commit id hash size: {idSize}");

            if (_hashSize == 0)
            {
                _hashSize = idSize;
            }
            else if (_hashSize != idSize)
            {
                throw new StoreInvalidException($"Commit id hash size mismatch: expected {_hashSize}, got {idSize}");
            }

            var parents = getParents(start + i);
            if (parents is not null)
            {
                for (var p = 0; p < parents.Count; p++)
                {
                    var parent = parents[p];
                    if (!parent.IsEmpty && !parent.IsZero && parent.HashSize != _hashSize)
                        throw new StoreInvalidException($"Parent commit id hash size mismatch: expected {_hashSize}, got {parent.HashSize}");
                }
            }
        }

        try
        {
            Directory.CreateDirectory(AppPaths.CacheDirectory);
            using var dat = new FileStream(_dat, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var idx = new FileStream(_idx, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var search = new StreamWriter(new FileStream(_search, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            var bw = new BinaryWriter(dat, new UTF8Encoding(false), leaveOpen: true);
            var idxBuf = new byte[8];
            Span<byte> zeroSha = stackalloc byte[_hashSize];
            zeroSha.Clear();

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
                    if (parents is not null && p < parents.Count && !parents[p].IsEmpty && !parents[p].IsZero)
                        bw.Write(parents[p].AsSpan());
                    else
                        bw.Write(zeroSha); // no real parent SHA available for this record
                }

                // Full SHA so a Ctrl+F prefix of any length matches exactly as the
                // in-memory store does.
                search.Write(m.Id.ToString());
                search.Write(GraphMetaStore.Sep);
                search.Write(Clean(m.MessageShort).ToLowerInvariant());
                search.Write(GraphMetaStore.Sep);
                search.Write(Clean(m.AuthorName).ToLowerInvariant());
                search.Write('\n');
            }
            bw.Flush();
        }
        catch (StoreInvalidException)
        {
            throw;
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
            var hashSize = _hashSize > 0 ? _hashSize : ReadCurrentHashSize();
            if (hashSize <= 0)
                hashSize = 20;
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
                HashSize = hashSize,
                Generation = _generation,
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

    private int ReadCurrentHashSize()
    {
        try
        {
            if (!File.Exists(_hdr))
                return 0;
            var existing = JsonSerializer.Deserialize<GraphMetaStore.Header>(GraphMetaStore.ReadAllBytesShared(_hdr), GraphMetaStore.Json);
            return existing is not null ? existing.HashSize : 0;
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
            var hashSize = _hashSize > 0 ? _hashSize : (sortedTips.Count > 0 ? sortedTips[0].HashSize : 20);
            if (hashSize <= 0)
                hashSize = 20;

            for (var i = 0; i < sortedTips.Count; i++)
            {
                if (sortedTips[i].HashSize != hashSize)
                    throw new StoreInvalidException($"Tip id hash size mismatch: expected {hashSize}, got {sortedTips[i].HashSize}");
            }

            if (_hashSize == 0 && sortedTips.Count > 0)
                _hashSize = hashSize;

            var bytes = new byte[sortedTips.Count * hashSize];
            for (var i = 0; i < sortedTips.Count; i++)
                sortedTips[i].AsSpan().CopyTo(bytes.AsSpan(i * hashSize, hashSize));
            var tmp = $"{_tips}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, _tips, overwrite: true);
        }
        catch (StoreInvalidException)
        {
            throw;
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
            var rowHashSize = rows.Count > 0 ? rows[0].Id.HashSize : 20;
            var existingHashSize = EnsureHashSize();
            if (matchesKey && existingHashSize > 0 && rowHashSize > 0 && existingHashSize != rowHashSize)
                matchesKey = false;
            if (matchesKey)
                AdoptHeaderGeneration(fingerprint);

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

            try
            {
                Append(rows, 0, to - from);
            }
            catch (StoreInvalidException)
            {
                return (SpillOutcome.Conflict, existing.RowCount);
            }
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
            try
            {
                Append(newRecords, 0, newRecords.Count);
                return true;
            }
            catch (StoreInvalidException)
            {
                return false;
            }
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

            // The reorder rewrites the header under a new walk key but keeps the store's files, so
            // readers of them keep the incarnation the old header named.
            AdoptHeaderGeneration(expectedOldWalkKey);
            var newOrderVersion = expectedOldOrderVersion + 1;
            if (!WriteOrder(newOrderVersion, order))
                return false;
            try
            {
                WriteTips(sortedTips);
            }
            catch (StoreInvalidException)
            {
                return false;
            }
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
