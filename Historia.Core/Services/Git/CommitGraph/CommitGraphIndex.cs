namespace Historia.Services.Git;

/// <summary>
/// Reads git's commit-graph files directly, read-only: which of a set of ids are commits in the
/// graph, and each one's corrected commit date. git's CLI stays the source of truth; this only
/// answers questions git has already written down, faster than a process could.
/// <para>Located the way git locates them: <c>objects/info/commit-graph</c>, else the split chain
/// <c>objects/info/commit-graphs/commit-graph-chain</c> (base layer first) of
/// <c>graph-&lt;hash&gt;.graph</c> layers. Only the repository's own objects directory is read,
/// never an alternate's.</para>
/// <para>The corrected commit date of a commit is its commit time plus its generation-data
/// offset; it is greater than every parent's and at least the commit's own time, so a tip's value
/// bounds the commit time of everything it reaches. A graph with a layer that has no generation
/// data (a generation-1 graph) gives no such bound, and is reported as no usable graph.</para>
/// <para>Every file is opened shared for read, write and delete and closed before
/// <see cref="Lookup"/> returns: git rewrites and renames these files (<c>gc</c>, split
/// writes), and a handle held open on Windows would make that fail. Nothing is memory-mapped
/// and nothing is cached between calls.</para>
/// </summary>
public sealed class CommitGraphIndex(string objectsDirectory)
{
    private const int HeaderSize = 8;
    private const int TocEntrySize = 12;
    private const int FanoutSize = 256 * 4;

    private const uint ChunkOidFanout = 0x4F494446;  // OIDF
    private const uint ChunkOidLookup = 0x4F49444C;  // OIDL
    private const uint ChunkCommitData = 0x43444154; // CDAT
    private const uint ChunkGenData = 0x47444132;    // GDA2
    private const uint ChunkGenOverflow = 0x47444F32; // GDO2
    private const uint ChunkBase = 0x42415345;       // BASE

    private const uint OffsetOverflow = 0x80000000;

    // Reads of commit data for found commits are merged into one span while the gap between them
    // is at most this many bytes, and a span never grows past MaxSpan.
    private const int MergeGap = 16 * 1024;
    private const int MaxSpan = 1024 * 1024;

    public string ObjectsDirectory { get; } = objectsDirectory;

    /// <summary>Looks up <paramref name="ids"/> in the commit-graph. Null when there is no
    /// usable graph — none at all, a layer without generation data, a hash that doesn't match the
    /// ids, a file that fails validation, or an I/O error (each logged once with the reason).</summary>
    public CommitGraphLookup? Lookup(IReadOnlyCollection<GitObjectId> ids)
    {
        try
        {
            return LookupCore(ids);
        }
        catch (InvalidDataException ex)
        {
            Unusable(ex.Message);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Unusable($"I/O error: {ex.Message}");
            return null;
        }
    }

    private void Unusable(string reason) =>
        AppLog.Write(AppLogLevel.Info, "git", $"Commit-graph not used for {ObjectsDirectory}: {reason}");

    private CommitGraphLookup? LookupCore(IReadOnlyCollection<GitObjectId> ids)
    {
        var hashLength = 0;
        foreach (var id in ids)
        {
            if (hashLength == 0)
                hashLength = id.HashSize;
            else if (id.HashSize != hashLength)
                throw new InvalidDataException("ids of mixed hash lengths");
        }

        var layers = LocateLayers();
        if (layers is null)
        {
            Unusable("no commit-graph file");
            return null;
        }

        var sorted = ids.Distinct().ToArray();
        Array.Sort(sorted);
        var found = new Dictionary<GitObjectId, long>(Math.Min(sorted.Length, 1 << 16));

        var baseHashes = new List<byte[]>();
        for (var i = 0; i < layers.Count; i++)
        {
            var (path, expectedHash) = layers[i];
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var layer = ReadLayer(handle, path, hashLength);
            hashLength = layer.HashLength;

            // A chain layer names its base layers, in order, in its BASE chunk and its header, and
            // its file name is its own trailing checksum — as git checks before trusting a chain.
            if (expectedHash is not null)
            {
                if (!layer.Checksum.AsSpan().SequenceEqual(expectedHash))
                    throw new InvalidDataException($"{Path.GetFileName(path)}: checksum doesn't match its name");
                if (layer.BaseCount != i || layer.BaseHashes.Count != i)
                    throw new InvalidDataException($"{Path.GetFileName(path)}: names {layer.BaseCount} base graphs, expected {i}");
                for (var b = 0; b < i; b++)
                    if (!layer.BaseHashes[b].AsSpan().SequenceEqual(baseHashes[b]))
                        throw new InvalidDataException($"{Path.GetFileName(path)}: base graph {b} doesn't match the chain");
                baseHashes.Add(expectedHash);
            }
            else if (layer.BaseCount != 0)
                throw new InvalidDataException("the single commit-graph file names base graphs");

            if (layer.GenData is null)
            {
                Unusable($"{Path.GetFileName(path)} has no corrected commit dates (generation data)");
                return null;
            }

            if (sorted.Length > 0)
                ReadLayerEntries(handle, layer, sorted, found);
        }

        return new CommitGraphLookup(found);
    }

    /// <summary>The graph files in git's order, each with the checksum its chain line names (null
    /// for the single file). Null when there is none.</summary>
    private List<(string Path, byte[]? ExpectedHash)>? LocateLayers()
    {
        var info = Path.Combine(ObjectsDirectory, "info");
        var single = Path.Combine(info, "commit-graph");
        if (File.Exists(single))
            return [(single, null)];

        var chainFile = Path.Combine(info, "commit-graphs", "commit-graph-chain");
        if (!File.Exists(chainFile))
            return null;

        byte[] bytes;
        using (var handle = File.OpenHandle(chainFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var length = RandomAccess.GetLength(handle);
            if (length > 1 << 20)
                throw new InvalidDataException("commit-graph-chain is implausibly large");
            bytes = new byte[length];
            ReadExactly(handle, bytes, 0);
        }

        var layers = new List<(string, byte[]?)>();
        foreach (var raw in Encoding.ASCII.GetString(bytes).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
                continue;
            if (!GitObjectId.TryParse(line, out var hash))
                throw new InvalidDataException($"commit-graph-chain line '{line}' is not a hash");
            layers.Add((Path.Combine(info, "commit-graphs", $"graph-{line}.graph"), hash.AsSpan().ToArray()));
        }
        return layers.Count == 0 ? null : layers;
    }

    private sealed record Layer(
        int HashLength, uint Count, byte[] Fanout, long OidLookup, long CommitData,
        long? GenData, long? GenOverflow, long GenOverflowCount, int BaseCount, List<byte[]> BaseHashes, byte[] Checksum);

    /// <summary>Header, chunk table and fanout, validated before anything in the file is
    /// trusted.</summary>
    private static Layer ReadLayer(SafeFileHandle handle, string path, int expectedHashLength)
    {
        var name = Path.GetFileName(path);
        var fileLength = RandomAccess.GetLength(handle);

        var header = new byte[HeaderSize];
        if (fileLength < HeaderSize)
            throw new InvalidDataException($"{name}: truncated header");
        ReadExactly(handle, header, 0);
        if (header[0] != 'C' || header[1] != 'G' || header[2] != 'P' || header[3] != 'H')
            throw new InvalidDataException($"{name}: bad signature");
        if (header[4] != 1)
            throw new InvalidDataException($"{name}: unsupported version {header[4]}");
        var hashLength = header[5] switch
        {
            1 => 20,
            2 => 32,
            _ => throw new InvalidDataException($"{name}: unknown hash version {header[5]}"),
        };
        if (expectedHashLength != 0 && hashLength != expectedHashLength)
            throw new InvalidDataException($"{name}: hash length {hashLength} doesn't match the repository's ({expectedHashLength})");
        int chunkCount = header[6];
        int baseCount = header[7];

        var tocLength = (chunkCount + 1) * TocEntrySize;
        if (fileLength < HeaderSize + tocLength + FanoutSize + hashLength)
            throw new InvalidDataException($"{name}: too small to hold {chunkCount} chunks");
        var toc = new byte[tocLength];
        ReadExactly(handle, toc, HeaderSize);

        var dataEnd = fileLength - hashLength;
        var chunks = new Dictionary<uint, (long Offset, long Size)>();
        for (var i = 0; i < chunkCount; i++)
        {
            var id = BinaryPrimitives.ReadUInt32BigEndian(toc.AsSpan(i * TocEntrySize));
            var offset = BinaryPrimitives.ReadInt64BigEndian(toc.AsSpan(i * TocEntrySize + 4));
            var next = BinaryPrimitives.ReadInt64BigEndian(toc.AsSpan((i + 1) * TocEntrySize + 4));
            if (id == 0)
                throw new InvalidDataException($"{name}: chunk table ends early");
            if (offset < HeaderSize + tocLength || next < offset || next > dataEnd)
                throw new InvalidDataException($"{name}: chunk offsets out of order or outside the file");
            if (!chunks.TryAdd(id, (offset, next - offset)))
                throw new InvalidDataException($"{name}: duplicate chunk {id:X8}");
        }
        if (BinaryPrimitives.ReadUInt32BigEndian(toc.AsSpan(chunkCount * TocEntrySize)) != 0)
            throw new InvalidDataException($"{name}: chunk table has no terminator");

        (long Offset, long Size) Required(uint id, string label) =>
            chunks.TryGetValue(id, out var c) ? c : throw new InvalidDataException($"{name}: no {label} chunk");

        var fanoutChunk = Required(ChunkOidFanout, "OID fanout");
        if (fanoutChunk.Size != FanoutSize)
            throw new InvalidDataException($"{name}: OID fanout chunk is the wrong size");
        var fanout = new byte[FanoutSize];
        ReadExactly(handle, fanout, fanoutChunk.Offset);
        uint last = 0;
        for (var i = 0; i < 256; i++)
        {
            var f = BinaryPrimitives.ReadUInt32BigEndian(fanout.AsSpan(i * 4));
            if (f < last)
                throw new InvalidDataException($"{name}: fanout values out of order");
            last = f;
        }
        var count = last;

        // git reads a chunk's record count as size / record size; so does this.
        var lookup = Required(ChunkOidLookup, "OID lookup");
        if (lookup.Size / hashLength != count)
            throw new InvalidDataException($"{name}: OID lookup chunk is the wrong size");
        var data = Required(ChunkCommitData, "commit data");
        if (data.Size / (hashLength + 16) != count)
            throw new InvalidDataException($"{name}: commit data chunk is the wrong size");

        long? gen = null, genOverflow = null;
        long genOverflowCount = 0;
        if (chunks.TryGetValue(ChunkGenData, out var g))
        {
            if (g.Size / 4 != count)
                throw new InvalidDataException($"{name}: generation data chunk is the wrong size");
            gen = g.Offset;
            if (chunks.TryGetValue(ChunkGenOverflow, out var o))
            {
                genOverflow = o.Offset;
                genOverflowCount = o.Size / 8;
            }
        }

        var baseHashes = new List<byte[]>();
        if (baseCount > 0)
        {
            var b = Required(ChunkBase, "base graphs");
            if (b.Size / hashLength < baseCount)
                throw new InvalidDataException($"{name}: base graphs chunk is too small");
            var bytes = new byte[baseCount * hashLength];
            ReadExactly(handle, bytes, b.Offset);
            for (var i = 0; i < baseCount; i++)
                baseHashes.Add(bytes.AsSpan(i * hashLength, hashLength).ToArray());
        }

        var checksum = new byte[hashLength];
        ReadExactly(handle, checksum, dataEnd);

        return new Layer(hashLength, count, fanout, lookup.Offset, data.Offset, gen, genOverflow, genOverflowCount,
            baseCount, baseHashes, checksum);
    }

    /// <summary>Finds the wanted ids in one layer and reads their corrected dates. The OID lookup
    /// is streamed one fanout bucket at a time — only buckets holding a wanted id — and merged
    /// against the sorted ids, rather than one binary search (and its reads) per id. Commit data
    /// and generation data are then read in position order, nearby entries in one read.</summary>
    private static void ReadLayerEntries(SafeFileHandle handle, Layer layer, GitObjectId[] sorted, Dictionary<GitObjectId, long> found)
    {
        var h = layer.HashLength;
        var positions = new List<(uint Pos, GitObjectId Id)>();
        byte[] block = [];

        var k = 0;
        while (k < sorted.Length)
        {
            int first = sorted[k].AsSpan()[0];
            var lo = first == 0 ? 0u : BinaryPrimitives.ReadUInt32BigEndian(layer.Fanout.AsSpan((first - 1) * 4));
            var hi = BinaryPrimitives.ReadUInt32BigEndian(layer.Fanout.AsSpan(first * 4));
            var end = k;
            while (end < sorted.Length && sorted[end].AsSpan()[0] == first)
                end++;

            var n = (int)(hi - lo);
            if (n > 0)
            {
                var bytes = n * h;
                if (block.Length < bytes)
                    block = new byte[Math.Max(bytes, block.Length * 2)];
                ReadExactly(handle, block.AsSpan(0, bytes), layer.OidLookup + (long)lo * h);

                var r = 0;
                for (var i = k; i < end && r < n; i++)
                {
                    var want = sorted[i].AsSpan();
                    while (r < n && block.AsSpan(r * h, h).SequenceCompareTo(want) < 0)
                        r++;
                    if (r < n && block.AsSpan(r * h, h).SequenceEqual(want))
                        positions.Add((lo + (uint)r, sorted[i]));
                }
            }
            k = end;
        }

        if (positions.Count == 0)
            return;

        // Positions come out in id order, which is also position order within the lookup.
        var times = ReadSpans(handle, positions, layer.CommitData, h + 16, h + 8, 8);
        var offsets = ReadSpans(handle, positions, layer.GenData!.Value, 4, 0, 4);

        List<int>? overflowAt = null;
        for (var i = 0; i < positions.Count; i++)
        {
            var t = times[i];
            var commitTime = ((long)(BinaryPrimitives.ReadUInt32BigEndian(t) & 0x3) << 32) | BinaryPrimitives.ReadUInt32BigEndian(t.AsSpan(4));
            var offset = BinaryPrimitives.ReadUInt32BigEndian(offsets[i]);
            if ((offset & OffsetOverflow) != 0)
            {
                var index = offset ^ OffsetOverflow;
                if (layer.GenOverflow is null || index >= layer.GenOverflowCount)
                    throw new InvalidDataException("generation data needs overflow entries the file doesn't have");
                (overflowAt ??= []).Add(i);
                found[positions[i].Id] = commitTime; // offset added below
            }
            else
                found[positions[i].Id] = commitTime + offset;
        }

        if (overflowAt is not null)
        {
            var buffer = new byte[8];
            foreach (var i in overflowAt)
            {
                var index = BinaryPrimitives.ReadUInt32BigEndian(offsets[i]) ^ OffsetOverflow;
                ReadExactly(handle, buffer, layer.GenOverflow!.Value + index * 8L);
                found[positions[i].Id] += (long)BinaryPrimitives.ReadUInt64BigEndian(buffer);
            }
        }
    }

    /// <summary>For each position (ascending), the <paramref name="fieldLength"/> bytes at
    /// <paramref name="fieldOffset"/> within its <paramref name="recordSize"/>-byte record of the
    /// chunk at <paramref name="chunkOffset"/>. Records close together are read in one span.</summary>
    private static byte[][] ReadSpans(
        SafeFileHandle handle, List<(uint Pos, GitObjectId Id)> positions, long chunkOffset, int recordSize, int fieldOffset, int fieldLength)
    {
        var result = new byte[positions.Count][];
        var buffer = new byte[MaxSpan + recordSize];
        var i = 0;
        while (i < positions.Count)
        {
            var spanStart = (long)positions[i].Pos * recordSize;
            var j = i + 1;
            while (j < positions.Count)
            {
                var nextStart = (long)positions[j].Pos * recordSize;
                var prevEnd = (long)positions[j - 1].Pos * recordSize + recordSize;
                if (nextStart - prevEnd > MergeGap || nextStart + recordSize - spanStart > MaxSpan)
                    break;
                j++;
            }
            var spanLength = (int)((long)positions[j - 1].Pos * recordSize + recordSize - spanStart);
            ReadExactly(handle, buffer.AsSpan(0, spanLength), chunkOffset + spanStart);
            for (var x = i; x < j; x++)
            {
                var at = (int)((long)positions[x].Pos * recordSize - spanStart) + fieldOffset;
                result[x] = buffer.AsSpan(at, fieldLength).ToArray();
            }
            i = j;
        }
        return result;
    }

    private static void ReadExactly(SafeFileHandle handle, Span<byte> into, long offset)
    {
        var done = 0;
        while (done < into.Length)
        {
            var n = RandomAccess.Read(handle, into[done..], offset + done);
            if (n <= 0)
                throw new InvalidDataException("file is shorter than its chunk table says");
            done += n;
        }
    }
}

/// <summary>What one <see cref="CommitGraphIndex.Lookup"/> found: the looked-up ids that are
/// commits in the commit-graph, with their corrected commit dates.</summary>
public sealed class CommitGraphLookup
{
    private readonly Dictionary<GitObjectId, long> _dates;

    internal CommitGraphLookup(Dictionary<GitObjectId, long> dates) => _dates = dates;

    /// <summary>Looked-up ids found in the graph.</summary>
    public int Count => _dates.Count;

    /// <summary>True when <paramref name="id"/> was looked up and is a commit in the graph.</summary>
    public bool Contains(GitObjectId id) => _dates.ContainsKey(id);

    /// <summary>The corrected commit date (seconds since the epoch) of a looked-up commit in the
    /// graph; null otherwise.</summary>
    public long? CorrectedDate(GitObjectId id) => _dates.TryGetValue(id, out var d) ? d : null;
}
