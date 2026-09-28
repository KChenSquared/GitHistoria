namespace Historia.Services.Git;

public sealed partial class GitGateway
{
    private string? _cachedEmptyTreeId;

    // Trap: User configuration (such as diff.external, diff.noprefix, diff.algorithm, color.ui,
    // or diff.*.textconv) alters git diff output formats and semantics. We pin:
    // - --no-ext-diff: bypass external diff tools (diff.external) that break parsing or pop up GUIs.
    // - --no-textconv: bypass textconv filters to ensure raw diff rather than transformed representations.
    // - --no-color: suppress ANSI color codes (color.diff) that corrupt diff line parsing.
    // - --src-prefix=a/ and --dst-prefix=b/: guarantee deterministic path prefixes regardless of diff.noprefix.
    // - -M: ensure rename and copy detection matching libgit2 SimilarityOptions.Renames.
    // - --diff-algorithm=myers: pin the Myers algorithm to match libgit2 and avoid user diff.algorithm overrides.
    // - --unified=<n>: control context line budget deterministically overriding diff.context.
    private static List<string> BuildPinnedFlags(int contextLines) =>
    [
        "--no-ext-diff",
        "--no-textconv",
        "--no-color",
        "--src-prefix=a/",
        "--dst-prefix=b/",
        "-M",
        "--diff-algorithm=myers",
        $"--unified={contextLines}",
    ];

    private static List<string> BuildNumstatFlags() =>
    [
        "--no-ext-diff",
        "--no-textconv",
        "--no-color",
        "--src-prefix=a/",
        "--dst-prefix=b/",
        "-M",
        "--diff-algorithm=myers",
        "--numstat",
    ];

    public async Task<FileDiff> GetFileDiffAsync(DiffRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Path))
            throw new HistoriaGitException(GitErrorKind.Unknown, "A file path is required for a diff.");

        var path = request.Path;
        const long maxBytes = 5 * 1024 * 1024; // 5 MB
        const int maxLines = 50_000;

        var pinnedFlags = BuildPinnedFlags(request.ContextLines);
        var numstatFlags = BuildNumstatFlags();

        List<string> args;
        List<string> numstatArgs;
        var isNoIndex = false;

        switch (request.Source)
        {
            case DiffSource.UnstagedChanges:
            {
                args = ["diff", ..pinnedFlags, "--", path];
                numstatArgs = ["diff", ..numstatFlags, "--", path];

                var unstagedResult = await RunDiffCliAsync(args, maxBytes, ct).ConfigureAwait(false);
                if (!unstagedResult.OutputTruncated && string.IsNullOrEmpty(unstagedResult.StandardOutput))
                {
                    var fullPath = Path.Combine(context.RepositoryPath, path);
                    if (File.Exists(fullPath))
                    {
                        var lsResult = await cli.RunAsync(new GitCliRequest
                        {
                            Arguments = ["ls-files", "-z", "--", path],
                            WorkingDirectory = context.RepositoryPath,
                            Timeout = TimeSpan.FromSeconds(10),
                        }, ct).ConfigureAwait(false);

                        if (lsResult.IsSuccess && string.IsNullOrEmpty(lsResult.StandardOutput))
                        {
                            isNoIndex = true;
                            args = ["diff", ..pinnedFlags, "--no-index", "--", "/dev/null", path];
                            numstatArgs = ["diff", ..numstatFlags, "--no-index", "--", "/dev/null", path];
                            var untrackedResult = await RunDiffCliAsync(args, maxBytes, ct).ConfigureAwait(false);
                            return await MapDiffResultAsync(untrackedResult, path, isNoIndex, maxBytes, maxLines, numstatArgs, ct).ConfigureAwait(false);
                        }
                    }
                }

                return await MapDiffResultAsync(unstagedResult, path, isNoIndex, maxBytes, maxLines, numstatArgs, ct).ConfigureAwait(false);
            }

            case DiffSource.StagedChanges:
            {
                args = ["diff", "--cached", ..pinnedFlags, "--", path];
                numstatArgs = ["diff", "--cached", ..numstatFlags, "--", path];
                var result = await RunDiffCliAsync(args, maxBytes, ct).ConfigureAwait(false);
                return await MapDiffResultAsync(result, path, isNoIndex, maxBytes, maxLines, numstatArgs, ct).ConfigureAwait(false);
            }

            case DiffSource.Commit:
            {
                var commitId = request.CommitId
                    ?? throw new HistoriaGitException(GitErrorKind.RefNotFound, "No commit id supplied.");

                var sha = commitId.ToString();
                var logResult = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["log", "-1", "--format=%P", sha],
                    WorkingDirectory = context.RepositoryPath,
                    Timeout = TimeSpan.FromSeconds(10),
                }, ct).ConfigureAwait(false);

                if (!logResult.IsSuccess)
                    throw HistoriaGitException.FromCli(logResult, "CommitDiff");

                var parents = logResult.StandardOutput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parents.Length == 0)
                {
                    // Root commit: diff-tree -p --root -M.
                    args = ["diff-tree", "--no-commit-id", "-p", "--root", ..pinnedFlags, sha, "--", path];
                    numstatArgs = ["diff-tree", "--no-commit-id", "--root", ..numstatFlags, sha, "--", path];
                }
                else
                {
                    var parentIdx = Math.Max(0, request.ParentIndex - 1);
                    if (parentIdx < parents.Length)
                    {
                        var parentSha = parents[parentIdx];
                        args = ["diff", ..pinnedFlags, parentSha, sha, "--", path];
                        numstatArgs = ["diff", ..numstatFlags, parentSha, sha, "--", path];
                    }
                    else
                    {
                        // Parent index out of range: diff against empty tree (matches libgit2 null parent).
                        var emptyTree = await GetEmptyTreeIdAsync(ct).ConfigureAwait(false);
                        args = ["diff", ..pinnedFlags, emptyTree, sha, "--", path];
                        numstatArgs = ["diff", ..numstatFlags, emptyTree, sha, "--", path];
                    }
                }

                var result = await RunDiffCliAsync(args, maxBytes, ct).ConfigureAwait(false);
                return await MapDiffResultAsync(result, path, isNoIndex, maxBytes, maxLines, numstatArgs, ct).ConfigureAwait(false);
            }

            case DiffSource.Comparison:
            {
                var left = request.CommitId?.ToString() ?? await GetEmptyTreeIdAsync(ct).ConfigureAwait(false);
                var right = request.OtherCommitId?.ToString() ?? await GetEmptyTreeIdAsync(ct).ConfigureAwait(false);

                args = ["diff", ..pinnedFlags, left, right, "--", path];
                numstatArgs = ["diff", ..numstatFlags, left, right, "--", path];
                var result = await RunDiffCliAsync(args, maxBytes, ct).ConfigureAwait(false);
                return await MapDiffResultAsync(result, path, isNoIndex, maxBytes, maxLines, numstatArgs, ct).ConfigureAwait(false);
            }

            default:
                throw new HistoriaGitException(GitErrorKind.Unknown, $"Unsupported diff source '{request.Source}'.");
        }
    }

    private async Task<GitCliResult> RunDiffCliAsync(IReadOnlyList<string> args, long maxBytes, CancellationToken ct)
    {
        return await cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = context.RepositoryPath,
            // Trap: line-mode capture re-joins lines with Environment.NewLine, turning an LF
            // patch into CRLF on Windows (and dropping real CRs) — hunk staging then fails.
            RawStandardOutput = true,
            MaxStandardOutputBytes = maxBytes,
            Timeout = TimeSpan.FromSeconds(30),
        }, ct).ConfigureAwait(false);
    }

    private async Task<FileDiff> MapDiffResultAsync(
        GitCliResult result,
        string path,
        bool isNoIndex,
        long maxBytes,
        int maxLines,
        IReadOnlyList<string> numstatArgs,
        CancellationToken ct)
    {
        if (!result.OutputTruncated)
        {
            if (isNoIndex)
            {
                if (result.ExitCode != 0 && result.ExitCode != 1)
                    throw HistoriaGitException.FromCli(result, "DiffUntracked");
            }
            else if (!result.IsSuccess)
            {
                throw HistoriaGitException.FromCli(result, "Diff");
            }
        }

        var patchText = result.StandardOutput ?? string.Empty;
        if (string.IsNullOrEmpty(patchText) && !result.OutputTruncated)
            return FileDiff.EmptyFor(path, DiffKind.Modified);

        var (kind, oldPath, isBinary) = ParseDiffHeader(patchText, path);

        if (isBinary)
        {
            return FileDiff.EmptyFor(path, kind) with
            {
                OldPath = oldPath,
                IsBinary = true,
            };
        }

        var lineCount = CountLines(patchText);
        var isTruncated = result.OutputTruncated || patchText.Length > maxBytes || lineCount > maxLines;

        if (isTruncated)
        {
            var (numAdded, numDeleted) = await QueryNumstatCountsAsync(numstatArgs, ct).ConfigureAwait(false);
            return FileDiff.EmptyFor(path, kind) with
            {
                OldPath = oldPath,
                IsTruncated = true,
                AddedLines = numAdded,
                DeletedLines = numDeleted,
            };
        }

        var hunks = UnifiedDiffParser.Parse(patchText);
        var added = hunks.Sum(h => h.Lines.Count(l => l.Type == DiffLineType.Addition));
        var deleted = hunks.Sum(h => h.Lines.Count(l => l.Type == DiffLineType.Deletion));

        return new FileDiff
        {
            Path = path,
            OldPath = oldPath,
            Kind = kind,
            IsBinary = false,
            IsTruncated = false,
            Hunks = hunks,
            AddedLines = added,
            DeletedLines = deleted,
            RawPatch = patchText,
        };
    }

    private static (DiffKind Kind, string? OldPath, bool IsBinary) ParseDiffHeader(string patchText, string currentPath)
    {
        var kind = DiffKind.Modified;
        string? oldPath = null;
        var isBinary = false;
        var hasDeletedMode = false;
        var hasNewMode = false;
        string? oldMode = null;
        string? newMode = null;

        using var reader = new StringReader(patchText);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
                break;

            if (line.StartsWith("Binary files ", StringComparison.Ordinal) && line.EndsWith("differ", StringComparison.Ordinal))
            {
                isBinary = true;
            }
            else if (line.StartsWith("rename from ", StringComparison.Ordinal))
            {
                kind = DiffKind.Renamed;
                oldPath = line["rename from ".Length..].Trim();
            }
            else if (line.StartsWith("copy from ", StringComparison.Ordinal))
            {
                kind = DiffKind.Copied;
                oldPath = line["copy from ".Length..].Trim();
            }
            else if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
            {
                hasDeletedMode = true;
            }
            else if (line.StartsWith("new file mode ", StringComparison.Ordinal))
            {
                hasNewMode = true;
            }
            else if (line.StartsWith("old mode ", StringComparison.Ordinal))
            {
                oldMode = line["old mode ".Length..].Trim();
            }
            else if (line.StartsWith("new mode ", StringComparison.Ordinal))
            {
                newMode = line["new mode ".Length..].Trim();
            }
            else if (line.StartsWith("--- /dev/null", StringComparison.Ordinal))
            {
                hasNewMode = true;
            }
            else if (line.StartsWith("+++ /dev/null", StringComparison.Ordinal))
            {
                hasDeletedMode = true;
            }
        }

        if (kind != DiffKind.Renamed && kind != DiffKind.Copied)
        {
            if (hasDeletedMode && hasNewMode)
                kind = DiffKind.TypeChanged;
            else if (hasNewMode)
                kind = DiffKind.Added;
            else if (hasDeletedMode)
                kind = DiffKind.Deleted;
            else if (oldMode is not null && newMode is not null)
            {
                var oldType = oldMode.Length >= 3 ? oldMode[..3] : oldMode;
                var newType = newMode.Length >= 3 ? newMode[..3] : newMode;
                if (!string.Equals(oldType, newType, StringComparison.Ordinal))
                    kind = DiffKind.TypeChanged;
                else
                    kind = DiffKind.Modified;
            }
            else
                kind = DiffKind.Modified;
        }

        if (string.Equals(oldPath, currentPath, StringComparison.Ordinal))
            oldPath = null;

        return (kind, oldPath, isBinary);
    }

    private async Task<(int Added, int Deleted)> QueryNumstatCountsAsync(IReadOnlyList<string> numstatArgs, CancellationToken ct)
    {
        var numstatResult = await cli.RunAsync(new GitCliRequest
        {
            Arguments = numstatArgs,
            WorkingDirectory = context.RepositoryPath,
            Timeout = TimeSpan.FromSeconds(30),
        }, ct).ConfigureAwait(false);

        if (numstatResult.ExitCode != 0 && numstatResult.ExitCode != 1)
            return (0, 0);

        var output = numstatResult.StandardOutput.Trim();
        if (string.IsNullOrEmpty(output))
            return (0, 0);

        var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].TrimEnd('\r');
        var parts = firstLine.Split('\t');
        if (parts.Length >= 2)
        {
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a);
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d);
            return (a, d);
        }

        return (0, 0);
    }

    private async Task<string> GetEmptyTreeIdAsync(CancellationToken ct)
    {
        if (_cachedEmptyTreeId is not null)
            return _cachedEmptyTreeId;

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["hash-object", "-t", "tree", "--stdin"],
            WorkingDirectory = context.RepositoryPath,
            StandardInput = string.Empty,
            Timeout = TimeSpan.FromSeconds(10),
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "EmptyTreeHash");

        var id = result.StandardOutput.Trim();
        _cachedEmptyTreeId = id;
        return id;
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var count = 1;
        foreach (var ch in text)
            if (ch == '\n')
                count++;
        return count;
    }
}
