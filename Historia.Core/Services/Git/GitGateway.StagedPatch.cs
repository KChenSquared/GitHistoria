namespace Historia.Services.Git;

public sealed partial class GitGateway
{
    private const long MaxStagedPatchBytes = 64L * 1024 * 1024;

    // Trap: the path of a chunk can't come from its ---/+++ lines — a binary file, a mode-only
    // change and a 100% rename have none. The paths come from a --name-status -z listing with the
    // same flags, which git emits in the same order as the patch, and pair with the chunks by
    // position. When the counts disagree, the chunk's own diff --git header is parsed instead.
    private static readonly string[] StagedPatchFlags =
    [
        "--cached",
        "--no-ext-diff",
        "--no-textconv",
        "--no-color",
        "--src-prefix=a/",
        "--dst-prefix=b/",
        "-M",
        "--diff-algorithm=minimal",
    ];

    public async Task<IReadOnlyList<(string Path, string? Patch)>> GetStagedPatchesAsync(
        bool againstHeadParent, long? maxPatchBytes = null, CancellationToken ct = default)
    {
        var baseArgs = new List<string>(StagedPatchFlags);
        if (againstHeadParent)
            baseArgs.Add(await HeadParentOrEmptyTreeAsync(ct).ConfigureAwait(false));

        var patch = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["diff", .. baseArgs],
            WorkingDirectory = context.RepositoryPath,
            RawStandardOutput = true,
            MaxStandardOutputBytes = maxPatchBytes is { } budget ? Math.Min(budget, MaxStagedPatchBytes) : MaxStagedPatchBytes,
            Timeout = TimeSpan.FromSeconds(60),
            ExtraEnvironment = LiteralPathspecEnv,
        }, ct).ConfigureAwait(false);

        var truncated = patch.OutputTruncated;
        if (truncated && maxPatchBytes is null)
            throw new HistoriaGitException(GitErrorKind.Unknown, "The staged changes are too large to read.");
        if (!truncated && !patch.IsSuccess)
            throw HistoriaGitException.FromCli(patch, "GetStagedPatches");

        var chunks = SplitPatch(patch.StandardOutput);
        // The read stopped mid-file, so the last chunk is incomplete.
        if (truncated && chunks.Count > 0)
            chunks.RemoveAt(chunks.Count - 1);
        if (!truncated && chunks.Count == 0)
            return [];

        var owners = await ReadChunkOwnersAsync(baseArgs, ct).ConfigureAwait(false);
        var result = new List<(string, string?)>(Math.Max(chunks.Count, owners.Count));
        if (!truncated || chunks.Count > owners.Count)
        {
            var paired = owners.Count == chunks.Count;
            for (var i = 0; i < chunks.Count; i++)
                result.Add((paired ? owners[i] : PathFromHeader(chunks[i]), chunks[i]));
            return result;
        }

        // The chunks read are a prefix of git's output, so they pair with a prefix of the owners;
        // every file past the read comes back without a patch.
        for (var i = 0; i < owners.Count; i++)
            result.Add((owners[i], i < chunks.Count ? chunks[i] : null));
        return result;
    }

    private async Task<string> HeadParentOrEmptyTreeAsync(CancellationToken ct)
    {
        var parent = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["rev-parse", "--verify", "--quiet", "HEAD^{commit}^"],
            WorkingDirectory = context.RepositoryPath,
            Timeout = TimeSpan.FromSeconds(10),
        }, ct).ConfigureAwait(false);

        return parent.IsSuccess && !string.IsNullOrWhiteSpace(parent.StandardOutput)
            ? "HEAD^"
            : await GetEmptyTreeIdAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The path each patch chunk belongs to, in git's order. A type change prints as a
    /// deletion and an addition, so its path owns two chunks; an unmerged path prints none.</summary>
    private async Task<List<string>> ReadChunkOwnersAsync(IReadOnlyList<string> baseArgs, CancellationToken ct)
    {
        var listing = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["diff", "--name-status", "-z", .. baseArgs],
            WorkingDirectory = context.RepositoryPath,
            RawStandardOutput = true,
            MaxStandardOutputBytes = MaxStagedPatchBytes,
            Timeout = TimeSpan.FromSeconds(30),
            ExtraEnvironment = LiteralPathspecEnv,
        }, ct).ConfigureAwait(false);

        if (!listing.IsSuccess)
            throw HistoriaGitException.FromCli(listing, "GetStagedPatches");

        var fields = listing.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var owners = new List<string>();
        // Each entry is STATUS NUL path NUL; a rename or copy carries the old path first.
        for (var i = 0; i < fields.Length;)
        {
            var status = fields[i];
            var takes = status.StartsWith('R') || status.StartsWith('C') ? 2 : 1;
            if (i + takes >= fields.Length)
                break;
            var path = fields[i + takes];
            var chunkCount = status.StartsWith('T') ? 2 : status.StartsWith('U') ? 0 : 1;
            for (var c = 0; c < chunkCount; c++)
                owners.Add(path);
            i += takes + 1;
        }
        return owners;
    }

    private static List<string> SplitPatch(string text)
    {
        var chunks = new List<string>();
        var start = -1;
        var pos = 0;
        while (pos < text.Length)
        {
            if (string.CompareOrdinal(text, pos, "diff --git ", 0, 11) == 0)
            {
                if (start >= 0)
                    chunks.Add(text[start..pos]);
                start = pos;
            }
            var eol = text.IndexOf('\n', pos);
            if (eol < 0)
                break;
            pos = eol + 1;
        }
        if (start >= 0)
            chunks.Add(text[start..]);
        return chunks;
    }

    private static string PathFromHeader(string chunk)
    {
        var eol = chunk.IndexOf('\n');
        var header = eol < 0 ? chunk : chunk[..eol];
        var idx = header.LastIndexOf(" b/", StringComparison.Ordinal);
        return idx >= 0 ? header[(idx + 3)..].Trim('"') : header["diff --git ".Length..];
    }
}
