using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Historia.Services.Git;
using Historia.Services.Workspace;

namespace Historia.Services.PowerTools;

/// <inheritdoc cref="IPatchService"/>
public sealed partial class PatchService(IGitCliService cli) : IPatchService
{
    private static readonly string[] ApplyFlags = ["--unidiff-zero", "--whitespace=nowarn", "--recount"];

    public string GeneratePatch(FileDiff source, PatchScope scope, IReadOnlySet<DiffLineRef>? selectedLines = null)
    {
        if (source.IsBinary)
            throw new HistoriaGitException(GitErrorKind.Unknown, "Binary files cannot be partially staged (§8.2.3).");
        if (string.IsNullOrEmpty(source.RawPatch))
            return string.Empty;

        var (_, chunks) = Split(source.RawPatch);

        if (scope == PatchScope.WholeFile)
            return source.RawPatch.EndsWith('\n') ? source.RawPatch : source.RawPatch + "\n";

        var wanted = scope == PatchScope.SingleHunk
            ? (selectedLines ?? new HashSet<DiffLineRef>()).Select(r => r.HunkIndex).ToHashSet()
            : Enumerable.Range(0, chunks.Count).ToHashSet();

        // Always synthesise a clean file header from the FileDiff — LibGit2Sharp's per-file
        // patch text does not reliably include the `diff --git` / `---` / `+++` lines that
        // `git apply` needs.
        var sb = new StringBuilder(BuildHeader(source));
        var emitted = 0;
        for (var h = 0; h < chunks.Count; h++)
        {
            if (!wanted.Contains(h))
                continue;

            var chunkText = scope == PatchScope.SelectedLines
                ? RebuildChunk(chunks[h], h, selectedLines ?? new HashSet<DiffLineRef>())
                : chunks[h];

            if (chunkText is null)
                continue;
            sb.Append(chunkText);
            emitted++;
        }

        return emitted == 0 ? string.Empty : sb.ToString();
    }

    public async Task<PatchCheckResult> CheckAsync(RepositoryContext context, string patch, PatchTarget target, bool reverse, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(patch))
            return new PatchCheckResult(false, "The patch is empty (nothing selected).");

        var result = await RunApplyAsync(context, patch, target, reverse, check: true, ct).ConfigureAwait(false);
        return result.IsSuccess
            ? new PatchCheckResult(true, null)
            : new PatchCheckResult(false, string.IsNullOrWhiteSpace(result.StandardError) ? "git apply --check failed" : result.StandardError.Trim());
    }

    public async Task ApplyPatchAsync(RepositoryContext context, string patch, PatchTarget target, bool reverse, CancellationToken ct = default)
    {
        var check = await CheckAsync(context, patch, target, reverse, ct).ConfigureAwait(false);
        if (!check.CanApply)
            throw new HistoriaGitException(GitErrorKind.Unknown, $"Patch cannot be applied: {check.FailureReason}");

        using var _ = context.BeginSelfOperation();
        var result = await RunApplyAsync(context, patch, target, reverse, check: false, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Apply patch");
    }

    private Task<GitCliResult> RunApplyAsync(RepositoryContext context, string patch, PatchTarget target, bool reverse, bool check, CancellationToken ct)
    {
        var args = new List<string> { "apply" };
        if (check) args.Add("--check");
        if (target == PatchTarget.StagingArea) args.Add("--cached");
        if (reverse) args.Add("--reverse");
        args.AddRange(ApplyFlags);
        args.Add("-");

        return cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = context.RepositoryPath,
            StandardInput = patch.EndsWith('\n') ? patch : patch + "\n",
        }, ct);
    }

    private static string BuildHeader(FileDiff d)
    {
        var oldPath = d.OldPath ?? d.Path;
        var sb = new StringBuilder();
        sb.Append("diff --git a/").Append(oldPath).Append(" b/").Append(d.Path).Append('\n');

        if (oldPath != d.Path)
        {
            sb.Append("rename from ").Append(oldPath).Append('\n');
            sb.Append("rename to ").Append(d.Path).Append('\n');
        }

        sb.Append(d.Kind == DiffKind.Added ? "--- /dev/null\n" : $"--- a/{oldPath}\n");
        sb.Append(d.Kind == DiffKind.Deleted ? "+++ /dev/null\n" : $"+++ b/{d.Path}\n");
        return sb.ToString();
    }

    // ── Raw-patch surgery ──

    private static (string Preamble, List<string> Chunks) Split(string raw)
    {
        var text = raw.Replace("\r\n", "\n").TrimEnd('\n');
        var lines = text.Split('\n');
        var preamble = new StringBuilder();
        var chunks = new List<string>();
        StringBuilder? current = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (current is not null)
                    chunks.Add(current.ToString());
                current = new StringBuilder();
                current.Append(line).Append('\n');
            }
            else if (current is null)
            {
                if (line.Length > 0 || preamble.Length > 0)
                    preamble.Append(line).Append('\n');
            }
            else if (line.Length > 0)
            {
                current.Append(line).Append('\n');
            }
        }

        if (current is not null)
            chunks.Add(current.ToString());

        return (preamble.ToString(), chunks);
    }

    /// <summary>Rebuilds one hunk chunk keeping only the selected +/- lines (§8.2.3, requirement 4).</summary>
    private static string? RebuildChunk(string chunk, int hunkIndex, IReadOnlySet<DiffLineRef> selected)
    {
        var lines = chunk.Split('\n');
        var header = lines[0];
        var m = HunkHeader().Match(header);
        if (!m.Success)
            return chunk;

        var oldStart = int.Parse(m.Groups["os"].Value, CultureInfo.InvariantCulture);
        var newStart = int.Parse(m.Groups["ns"].Value, CultureInfo.InvariantCulture);
        var tail = m.Groups["tail"].Value;

        var body = new StringBuilder();
        int oldCount = 0, newCount = 0, added = 0, removed = 0, bodyIndex = 0;

        for (var i = 1; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Length == 0)
                continue; // trailing split artifact — real body lines always have a prefix char
            if (l.StartsWith("\\", StringComparison.Ordinal))
            {
                // "\ No newline at end of file" — keep it if the line it qualifies was kept.
                var kept = body.Length > 0;
                if (kept) body.Append(l).Append('\n');
                continue;
            }

            var prefix = l.Length > 0 ? l[0] : ' ';
            var isSel = selected.Contains(new DiffLineRef(hunkIndex, bodyIndex));
            bodyIndex++;

            switch (prefix)
            {
                case '+':
                    if (isSel) { body.Append(l).Append('\n'); newCount++; added++; }
                    break;
                case '-':
                    if (isSel) { body.Append(l).Append('\n'); oldCount++; removed++; }
                    else { body.Append(' ').Append(l.AsSpan(1)).Append('\n'); oldCount++; newCount++; }
                    break;
                default:
                    body.Append(l).Append('\n');
                    oldCount++;
                    newCount++;
                    break;
            }
        }

        if (added == 0 && removed == 0)
            return null;

        var newHeader = $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@{tail}";
        return newHeader + "\n" + body;
    }

    [GeneratedRegex(@"^@@ -(?<os>\d+)(?:,\d+)? \+(?<ns>\d+)(?:,\d+)? @@(?<tail>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();
}
