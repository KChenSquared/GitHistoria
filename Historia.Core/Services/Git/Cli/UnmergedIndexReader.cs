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
        if (entry is null || entry.Mode == "160000")
            return false;

        var sizeResult = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["cat-file", "-s", entry.Id.ToString()],
            WorkingDirectory = repositoryPath,
        }, ct).ConfigureAwait(false);

        if (!sizeResult.IsSuccess)
            throw HistoriaGitException.FromCli(sizeResult, "Read blob size");

        if (!long.TryParse(sizeResult.StandardOutput.Trim(), out var size))
            return false;

        if (size > MaxBlobBytes)
            return true;

        if (size == 0)
            return false;

        var blobResult = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["cat-file", "blob", entry.Id.ToString()],
            WorkingDirectory = repositoryPath,
            RawStandardOutput = true,
            MaxStandardOutputBytes = MaxHeuristicBytes,
        }, ct).ConfigureAwait(false);

        if (!blobResult.IsSuccess && !blobResult.OutputTruncated)
            throw HistoriaGitException.FromCli(blobResult, "Read blob content");

        var bytes = blobResult.StandardOutputBytes;
        if (bytes is null || bytes.Length == 0)
            return false;

        return IsBinary(bytes);
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
