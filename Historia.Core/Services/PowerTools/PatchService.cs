namespace Historia.Services.PowerTools;

/// <inheritdoc cref="IPatchService"/>
public sealed partial class PatchService(IGitCliService cli) : IPatchService
{
    private static readonly string[] ApplyFlags = ["--unidiff-zero", "--whitespace=nowarn", "--recount"];

    public string GeneratePatch(FileDiff source, PatchScope scope, IReadOnlySet<DiffLineRef>? selectedLines = null, bool reverse = false)
    {
        if (source.IsBinary)
            throw new HistoriaGitException(GitErrorKind.Unknown, "Binary files cannot be partially staged.");
        if (string.IsNullOrEmpty(source.RawPatch))
            return string.Empty;

        var (_, chunks) = Split(source.RawPatch);

        if (scope == PatchScope.WholeFile)
            return source.RawPatch.EndsWith('\n') ? source.RawPatch : source.RawPatch + "\n";

        var wanted = scope == PatchScope.SingleHunk
            ? (selectedLines ?? new HashSet<DiffLineRef>()).Select(r => r.HunkIndex).ToHashSet()
            : Enumerable.Range(0, chunks.Count).ToHashSet();

        // Synthesise a clean file header from the FileDiff to keep index and mode lines
        // out of a patch whose partial content no longer matches them.
        var sb = new StringBuilder(BuildHeader(source));
        var emitted = 0;
        var runningDelta = 0;

        for (var h = 0; h < chunks.Count; h++)
        {
            if (!wanted.Contains(h))
                continue;

            var hunk = scope == PatchScope.SelectedLines
                ? RebuildChunk(chunks[h], h, selectedLines ?? new HashSet<DiffLineRef>(), reverse)
                : ParseSingleHunk(chunks[h]);

            if (hunk is null)
                continue;

            if (hunk.RawFallback is not null)
            {
                sb.Append(hunk.RawFallback);
                emitted++;
                continue;
            }

            sb.Append(FormatHeader(hunk, reverse, runningDelta));
            sb.Append(hunk.Body);
            runningDelta += hunk.NewCount - hunk.OldCount;
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
        var text = raw.EndsWith('\n') ? raw[..^1] : raw;
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

    private enum LineSide { Old, New, Both }

    private readonly record struct KeptLine(string Content, LineSide Side, bool HasNoNewline, string? MarkerText);

    private sealed record RebuiltHunk(int OldStart, int NewStart, int OldCount, int NewCount, string Tail, string Body, string? RawFallback = null);

    private static (int OldCount, int NewCount) RecountBody(string body)
    {
        var oldCount = 0;
        var newCount = 0;
        var lines = body.Split('\n');
        foreach (var l in lines)
        {
            if (l.Length == 0 || l[0] == '\\')
                continue;

            if (l[0] == ' ')
            {
                oldCount++;
                newCount++;
            }
            else if (l[0] == '-')
            {
                oldCount++;
            }
            else if (l[0] == '+')
            {
                newCount++;
            }
        }
        return (oldCount, newCount);
    }

    private static RebuiltHunk ParseSingleHunk(string chunk)
    {
        var firstNewline = chunk.IndexOf('\n');
        var header = firstNewline >= 0 ? chunk[..firstNewline] : chunk;
        var m = HunkHeader().Match(header);
        if (!m.Success)
            return new RebuiltHunk(0, 0, 0, 0, string.Empty, string.Empty, RawFallback: chunk);

        var oldStart = int.Parse(m.Groups["os"].Value, CultureInfo.InvariantCulture);
        var newStart = int.Parse(m.Groups["ns"].Value, CultureInfo.InvariantCulture);
        var tail = m.Groups["tail"].Value;
        var body = firstNewline >= 0 ? chunk[(firstNewline + 1)..] : string.Empty;
        var (oldCount, newCount) = RecountBody(body);
        return new RebuiltHunk(oldStart, newStart, oldCount, newCount, tail, body);
    }

    private static string FormatHeader(RebuiltHunk hunk, bool reverse, int runningDelta)
    {
        var os = hunk.OldStart;
        var ns = hunk.NewStart;
        var oc = hunk.OldCount;
        var nc = hunk.NewCount;

        var t = !reverse
            ? (oc > 0 ? os : os + 1) + runningDelta
            : (nc > 0 ? ns : ns + 1) - runningDelta;

        var newOs = oc > 0 ? t : t - 1;
        var newNs = nc > 0 ? t : t - 1;

        return $"@@ -{newOs},{oc} +{newNs},{nc} @@{hunk.Tail}\n";
    }

    /// <summary>Rebuilds one hunk chunk keeping only the selected +/- lines (requirement 4).</summary>
    private static RebuiltHunk? RebuildChunk(string chunk, int hunkIndex, IReadOnlySet<DiffLineRef> selected, bool reverse)
    {
        var lines = chunk.Split('\n');
        var header = lines[0];
        var m = HunkHeader().Match(header);
        if (!m.Success)
            return new RebuiltHunk(0, 0, 0, 0, string.Empty, string.Empty, RawFallback: chunk);

        var oldStart = int.Parse(m.Groups["os"].Value, CultureInfo.InvariantCulture);
        var newStart = int.Parse(m.Groups["ns"].Value, CultureInfo.InvariantCulture);
        var tail = m.Groups["tail"].Value;

        var keptLines = new List<KeptLine>();
        int added = 0, removed = 0, bodyIndex = 0;
        bool lastLineWasKept = false;

        for (var i = 1; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Length == 0)
                continue; // trailing split artifact — real body lines always have a prefix char

            if (l.StartsWith("\\", StringComparison.Ordinal))
            {
                // "\ No newline at end of file" — keep it if the line it qualifies was kept.
                if (lastLineWasKept && keptLines.Count > 0)
                {
                    var idx = keptLines.Count - 1;
                    keptLines[idx] = keptLines[idx] with { HasNoNewline = true, MarkerText = l };
                }
                lastLineWasKept = false;
                continue;
            }

            var prefix = l[0];
            var isSel = selected.Contains(new DiffLineRef(hunkIndex, bodyIndex));
            bodyIndex++;

            switch (prefix)
            {
                case '+':
                    if (isSel)
                    {
                        keptLines.Add(new KeptLine(l, LineSide.New, false, null));
                        added++;
                        lastLineWasKept = true;
                    }
                    else if (reverse)
                    {
                        // In reverse, an unselected addition is part of the current index state (context).
                        keptLines.Add(new KeptLine(" " + l[1..], LineSide.Both, false, null));
                        lastLineWasKept = true;
                    }
                    else
                    {
                        // In forward, an unselected addition is dropped.
                        lastLineWasKept = false;
                    }
                    break;

                case '-':
                    if (isSel)
                    {
                        keptLines.Add(new KeptLine(l, LineSide.Old, false, null));
                        removed++;
                        lastLineWasKept = true;
                    }
                    else if (!reverse)
                    {
                        // In forward, an unselected deletion is kept as context.
                        keptLines.Add(new KeptLine(" " + l[1..], LineSide.Both, false, null));
                        lastLineWasKept = true;
                    }
                    else
                    {
                        // In reverse, an unselected deletion is dropped (already not in index).
                        lastLineWasKept = false;
                    }
                    break;

                default:
                    keptLines.Add(new KeptLine(l, LineSide.Both, false, null));
                    lastLineWasKept = true;
                    break;
            }
        }

        if (added == 0 && removed == 0)
            return null;

        // Refuse a line-joining selection (B16): a line carrying '\ No newline' must be the last line of its side.
        for (var k = 0; k < keptLines.Count; k++)
        {
            var item = keptLines[k];
            if (!item.HasNoNewline)
                continue;

            var appliesToOld = item.Side is LineSide.Old or LineSide.Both;
            var appliesToNew = item.Side is LineSide.New or LineSide.Both;

            for (var j = k + 1; j < keptLines.Count; j++)
            {
                var later = keptLines[j];
                if ((appliesToOld && later.Side is LineSide.Old or LineSide.Both) ||
                    (appliesToNew && later.Side is LineSide.New or LineSide.Both))
                {
                    throw new HistoriaGitException(GitErrorKind.Unknown,
                        "This selection splits an end-of-file newline change. Include the file's last line, or stage the whole hunk.");
                }
            }
        }

        var body = new StringBuilder();
        foreach (var item in keptLines)
        {
            body.Append(item.Content).Append('\n');
            if (item.HasNoNewline && item.MarkerText is not null)
            {
                body.Append(item.MarkerText).Append('\n');
            }
        }

        var bodyText = body.ToString();
        var (oldCount, newCount) = RecountBody(bodyText);
        return new RebuiltHunk(oldStart, newStart, oldCount, newCount, tail, bodyText);
    }

    [GeneratedRegex(@"^@@ -(?<os>\d+)(?:,\d+)? \+(?<ns>\d+)(?:,\d+)? @@(?<tail>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex HunkHeader();
}
