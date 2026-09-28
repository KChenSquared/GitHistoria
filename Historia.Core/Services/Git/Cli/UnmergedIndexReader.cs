namespace Historia.Services.Git;

public sealed record UnmergedEntry(string Mode, GitObjectId Id, int Stage);

public sealed record UnmergedPathConflict(
    string Path,
    UnmergedEntry? Ancestor,
    UnmergedEntry? Ours,
    UnmergedEntry? Theirs);

/// <summary>
/// Reads unmerged index stages and determines binary status using Git CLI and blob inspection.
/// </summary>
public sealed class UnmergedIndexReader(IGitCliService cli)
{
    private const long MaxBlobBytes = 5 * 1024 * 1024;
    private const int MaxHeuristicBytes = 8000;
    private const long MaxBatchBytes = 16 * 1024 * 1024;

    public async Task<IReadOnlyList<UnmergedPathConflict>> ReadAsync(
        string repositoryPath,
        string? path = null,
        CancellationToken ct = default)
    {
        var args = new List<string> { "ls-files", "-u", "-z" };
        if (!string.IsNullOrEmpty(path))
        {
            args.Add("--");
            args.Add(path);
        }

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = repositoryPath,
            RawStandardOutput = true,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Read unmerged index");

        return Parse(result.StandardOutput);
    }

    public async Task<bool> HasUnmergedPathsAsync(string repositoryPath, CancellationToken ct = default)
    {
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["ls-files", "-u"],
            WorkingDirectory = repositoryPath,
            MaxStandardOutputBytes = 512,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess && !result.OutputTruncated)
            throw HistoriaGitException.FromCli(result, "Check unmerged paths");

        return !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    public async Task<bool> IsBinaryAsync(string repositoryPath, UnmergedEntry? entry, CancellationToken ct = default)
    {
        if (entry is null)
            return false;
        var binary = await FindBinaryBlobsAsync(repositoryPath, [entry], ct).ConfigureAwait(false);
        return binary.Contains(entry.Id);
    }

    /// <summary>
    /// Returns the ids of the <paramref name="entries"/> whose blobs libgit2's
    /// <c>Blob.IsBinary</c> would call binary. One <c>cat-file --batch-check</c> for every size,
    /// then <c>cat-file --batch</c> in chunks of at most <see cref="MaxBatchBytes"/>; a blob over
    /// the 5 MB budget reads only its first 8 000 bytes, which is all the heuristic looks at.
    /// </summary>
    public async Task<IReadOnlySet<GitObjectId>> FindBinaryBlobsAsync(
        string repositoryPath,
        IEnumerable<UnmergedEntry> entries,
        CancellationToken ct = default)
    {
        var ids = entries
            .Where(e => e.Mode != "160000")
            .Select(e => e.Id)
            .Distinct()
            .ToList();
        var binary = new HashSet<GitObjectId>();
        if (ids.Count == 0)
            return binary;

        var sizes = await ReadBlobSizesAsync(repositoryPath, ids, ct).ConfigureAwait(false);

        var chunk = new List<GitObjectId>();
        long chunkBytes = 0;
        foreach (var id in ids)
        {
            if (!sizes.TryGetValue(id, out var size) || size == 0)
                continue;

            if (size > MaxBlobBytes)
            {
                if (await IsLargeBlobBinaryAsync(repositoryPath, id, ct).ConfigureAwait(false))
                    binary.Add(id);
                continue;
            }

            if (chunk.Count > 0 && chunkBytes + size > MaxBatchBytes)
            {
                await ClassifyChunkAsync(repositoryPath, chunk, binary, ct).ConfigureAwait(false);
                chunk.Clear();
                chunkBytes = 0;
            }
            chunk.Add(id);
            chunkBytes += size;
        }

        if (chunk.Count > 0)
            await ClassifyChunkAsync(repositoryPath, chunk, binary, ct).ConfigureAwait(false);

        return binary;
    }

    private async Task<Dictionary<GitObjectId, long>> ReadBlobSizesAsync(
        string repositoryPath,
        IReadOnlyList<GitObjectId> ids,
        CancellationToken ct)
    {
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["cat-file", "--batch-check=%(objectname) %(objecttype) %(objectsize)"],
            WorkingDirectory = repositoryPath,
            StandardInput = string.Join('\n', ids) + "\n",
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Read blob sizes");

        // "<id> blob <size>" per object; a missing object prints "<id> missing" and is skipped.
        var sizes = new Dictionary<GitObjectId, long>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split(' ');
            if (parts.Length == 3 && parts[1] == "blob"
                && GitObjectId.TryParse(parts[0], out var id)
                && long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                sizes[id] = size;
            }
        }

        return sizes;
    }

    private async Task ClassifyChunkAsync(
        string repositoryPath,
        IReadOnlyList<GitObjectId> ids,
        HashSet<GitObjectId> binary,
        CancellationToken ct)
    {
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["cat-file", "--batch"],
            WorkingDirectory = repositoryPath,
            StandardInput = string.Join('\n', ids) + "\n",
            RawStandardOutput = true,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Read blob content");

        // Each object is "<id> <type> <size>\n<size bytes>\n".
        var bytes = result.StandardOutputBytes ?? [];
        var pos = 0;
        while (pos < bytes.Length)
        {
            var headerEnd = Array.IndexOf(bytes, (byte)'\n', pos);
            if (headerEnd < 0)
                break;

            var header = Encoding.ASCII.GetString(bytes, pos, headerEnd - pos).Split(' ');
            pos = headerEnd + 1;
            if (header.Length != 3
                || !GitObjectId.TryParse(header[0], out var id)
                || !int.TryParse(header[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                continue;
            }

            if (pos + size > bytes.Length)
                break;

            if (IsBinary(bytes.AsSpan(pos, size)))
                binary.Add(id);
            pos += size + 1;
        }
    }

    private async Task<bool> IsLargeBlobBinaryAsync(string repositoryPath, GitObjectId id, CancellationToken ct)
    {
        var blobResult = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["cat-file", "blob", id.ToString()],
            WorkingDirectory = repositoryPath,
            RawStandardOutput = true,
            MaxStandardOutputBytes = MaxHeuristicBytes,
        }, ct).ConfigureAwait(false);

        if (!blobResult.IsSuccess && !blobResult.OutputTruncated)
            throw HistoriaGitException.FromCli(blobResult, "Read blob content");

        var bytes = blobResult.StandardOutputBytes;
        return bytes is { Length: > 0 } && IsBinary(bytes);
    }

    internal static IReadOnlyList<UnmergedPathConflict> Parse(string stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return [];

        // -z output is NUL-terminated entries: "<mode> <sha> <stage>\t<path>\0"
        var entries = stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var map = new Dictionary<string, (UnmergedEntry? Ancestor, UnmergedEntry? Ours, UnmergedEntry? Theirs)>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var tabIndex = entry.IndexOf('\t');
            if (tabIndex < 0)
                continue;

            var metadata = entry[..tabIndex];
            var filePath = entry[(tabIndex + 1)..];

            var spaceParts = metadata.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (spaceParts.Length < 3)
                continue;

            var mode = spaceParts[0];
            if (!GitObjectId.TryParse(spaceParts[1], out var id))
                continue;

            if (!int.TryParse(spaceParts[2], out var stage))
                continue;

            var item = new UnmergedEntry(mode, id, stage);

            if (!map.TryGetValue(filePath, out var stages))
                stages = (null, null, null);

            switch (stage)
            {
                case 1:
                    stages = stages with { Ancestor = item };
                    break;
                case 2:
                    stages = stages with { Ours = item };
                    break;
                case 3:
                    stages = stages with { Theirs = item };
                    break;
            }

            map[filePath] = stages;
        }

        var result = new List<UnmergedPathConflict>(map.Count);
        foreach (var (p, stages) in map)
        {
            result.Add(new UnmergedPathConflict(p, stages.Ancestor, stages.Ours, stages.Theirs));
        }

        return result;
    }

    internal static bool IsBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxHeuristicBytes)
            bytes = bytes[..MaxHeuristicBytes];

        var offset = DetectBom(bytes, out var bom);
        if (bom > BomType.Utf8)
            return true;

        var printable = 0;
        var nonprintable = 0;

        for (var i = offset; i < bytes.Length; i++)
        {
            var b = bytes[i];
            if ((b > 0x1F && b != 127) || b == (byte)'\b' || b == 0x1B || b == (byte)'\f')
            {
                printable++;
            }
            else if (b == 0)
            {
                return true;
            }
            else if (!IsSpace(b))
            {
                nonprintable++;
            }
        }

        return (printable >> 7) < nonprintable;
    }

    private enum BomType { None = 0, Utf8 = 1, Utf16Le = 2, Utf16Be = 3, Utf32Le = 4, Utf32Be = 5 }

    private static int DetectBom(ReadOnlySpan<byte> bytes, out BomType bom)
    {
        bom = BomType.None;
        if (bytes.Length < 2)
            return 0;

        switch (bytes[0])
        {
            case 0:
                if (bytes.Length >= 4 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                {
                    bom = BomType.Utf32Be;
                    return 4;
                }
                break;
            case 0xEF:
                if (bytes.Length >= 3 && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    bom = BomType.Utf8;
                    return 3;
                }
                break;
            case 0xFE:
                if (bytes[1] == 0xFF)
                {
                    bom = BomType.Utf16Be;
                    return 2;
                }
                break;
            case 0xFF:
                if (bytes[1] == 0xFE)
                {
                    if (bytes.Length >= 4 && bytes[2] == 0 && bytes[3] == 0)
                    {
                        bom = BomType.Utf32Le;
                        return 4;
                    }
                    bom = BomType.Utf16Le;
                    return 2;
                }
                break;
        }

        return 0;
    }

    private static bool IsSpace(byte b)
        => b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\v' || b == (byte)'\f' || b == (byte)'\r';
}
