namespace Historia.Services.Git;

public sealed partial class GitGateway
{
    private static readonly IReadOnlyDictionary<string, string> LiteralPathspecEnv =
        new Dictionary<string, string> { ["GIT_LITERAL_PATHSPECS"] = "1" };

    public async Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0)
            return;

        using var _ = context.BeginSelfOperation();

        await context.WithWriteLockAsync(async innerCt =>
        {
            var validPaths = await FilterValidStagePathsAsync(paths, innerCt).ConfigureAwait(false);
            if (validPaths.Count == 0)
                return;

            var normalizedPaths = validPaths.Select(p => p.Replace('\\', '/')).ToList();
            var stdin = string.Join('\0', normalizedPaths) + '\0';

            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["add", "-A", "--pathspec-from-file=-", "--pathspec-file-nul"],
                WorkingDirectory = context.RepositoryPath,
                StandardInput = stdin,
                ExtraEnvironment = LiteralPathspecEnv,
            }, innerCt).ConfigureAwait(false);

            if (!result.IsSuccess && !(result.ExitCode == 1 && IsIgnoredAdviceOnly(result.StandardError)))
                throw HistoriaGitException.FromCli(result, "Stage");
        }, ct).ConfigureAwait(false);
    }

    public async Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0)
            return;

        using var _ = context.BeginSelfOperation();

        await context.WithWriteLockAsync(async innerCt =>
        {
            var headCheck = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["rev-parse", "--verify", "-q", "HEAD"],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            var normalizedPaths = paths.Select(p => p.Replace('\\', '/')).ToList();
            var stdin = string.Join('\0', normalizedPaths) + '\0';

            if (!headCheck.IsSuccess)
            {
                var result = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["rm", "--cached", "-q", "--pathspec-from-file=-", "--pathspec-file-nul"],
                    WorkingDirectory = context.RepositoryPath,
                    StandardInput = stdin,
                    ExtraEnvironment = LiteralPathspecEnv,
                }, innerCt).ConfigureAwait(false);

                if (!result.IsSuccess)
                    throw HistoriaGitException.FromCli(result, "Unstage");
                return;
            }

            var restoreResult = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["restore", "--staged", "--pathspec-from-file=-", "--pathspec-file-nul"],
                WorkingDirectory = context.RepositoryPath,
                StandardInput = stdin,
                ExtraEnvironment = LiteralPathspecEnv,
            }, innerCt).ConfigureAwait(false);

            if (!restoreResult.IsSuccess)
                throw HistoriaGitException.FromCli(restoreResult, "Unstage");
        }, ct).ConfigureAwait(false);
    }

    public async Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0)
            return;

        using var _ = context.BeginSelfOperation();

        await context.WithWriteLockAsync(async innerCt =>
        {
            var headCheck = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["rev-parse", "--verify", "-q", "HEAD"],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            if (!headCheck.IsSuccess)
            {
                await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["rm", "--cached", "-q", "--ignore-unmatch", "--pathspec-from-file=-", "--pathspec-file-nul"],
                    WorkingDirectory = context.RepositoryPath,
                    StandardInput = string.Join('\0', paths.Select(p => p.Replace('\\', '/'))) + '\0',
                    ExtraEnvironment = LiteralPathspecEnv,
                }, innerCt).ConfigureAwait(false);

                foreach (var path in paths)
                {
                    RecycleBin.Delete(Path.Combine(context.RepositoryPath, path));
                }
                return;
            }

            var inHeadSet = new HashSet<string>(StringComparer.Ordinal);
            var chunks = ChunkPathsByCommandLineLength(paths);
            foreach (var chunk in chunks)
            {
                var args = new List<string>(chunk.Count + 5) { "ls-tree", "-z", "--name-only", "--full-tree", "HEAD", "--" };
                args.AddRange(chunk.Select(p => p.Replace('\\', '/')));

                var treeResult = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = args,
                    WorkingDirectory = context.RepositoryPath,
                }, innerCt).ConfigureAwait(false);

                if (treeResult.IsSuccess && !string.IsNullOrEmpty(treeResult.StandardOutput))
                {
                    var names = treeResult.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var name in names)
                        inHeadSet.Add(name);
                }
            }

            var inHead = new List<string>();
            var notInHead = new List<string>();
            foreach (var path in paths)
            {
                var normalized = path.Replace('\\', '/');
                if (inHeadSet.Contains(normalized) || inHeadSet.Contains(path))
                    inHead.Add(normalized);
                else
                    notInHead.Add(normalized);
            }

            if (inHead.Count > 0)
            {
                var restoreResult = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["restore", "--source=HEAD", "--staged", "--worktree", "--pathspec-from-file=-", "--pathspec-file-nul"],
                    WorkingDirectory = context.RepositoryPath,
                    StandardInput = string.Join('\0', inHead) + '\0',
                    ExtraEnvironment = LiteralPathspecEnv,
                }, innerCt).ConfigureAwait(false);

                if (!restoreResult.IsSuccess)
                    throw HistoriaGitException.FromCli(restoreResult, "Discard");
            }

            if (notInHead.Count > 0)
            {
                await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["rm", "--cached", "-q", "--ignore-unmatch", "--pathspec-from-file=-", "--pathspec-file-nul"],
                    WorkingDirectory = context.RepositoryPath,
                    StandardInput = string.Join('\0', notInHead) + '\0',
                    ExtraEnvironment = LiteralPathspecEnv,
                }, innerCt).ConfigureAwait(false);

                foreach (var path in notInHead)
                {
                    RecycleBin.Delete(Path.Combine(context.RepositoryPath, path));
                }
            }
        }, ct).ConfigureAwait(false);
    }

    public async Task<GitObjectId> CommitAsync(CommitRequest request, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        return await context.WithWriteLockAsync(async innerCt =>
        {
            var args = new List<string> { "commit", "-F", "-" };
            if (request.Sign) args.Add("-S");
            if (request.AmendPreviousCommit) args.Add("--amend");
            if (request.AllowEmptyCommit) args.Add("--allow-empty");
            args.Add("--author");
            args.Add($"{request.AuthorName} <{request.AuthorEmail}>");

            var env = new Dictionary<string, string>
            {
                ["GIT_COMMITTER_NAME"] = request.CommitterName ?? request.AuthorName,
                ["GIT_COMMITTER_EMAIL"] = request.CommitterEmail ?? request.AuthorEmail,
            };

            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = args,
                WorkingDirectory = context.RepositoryPath,
                StandardInput = request.Message ?? "",
                ExtraEnvironment = env,
            }, innerCt).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                var kind = result.ErrorKind;
                if (kind != GitErrorKind.HookFailed)
                {
                    var combinedKind = GitErrorClassifier.Classify(result.ExitCode, $"{result.StandardOutput}\n{result.StandardError}");
                    if (combinedKind == GitErrorKind.HookFailed)
                        kind = GitErrorKind.HookFailed;
                }
                if (kind == GitErrorKind.HookFailed)
                    throw new HistoriaGitException(GitErrorKind.HookFailed, "Commit hook failed.", $"{result.StandardOutput}\n{result.StandardError}".Trim());

                throw HistoriaGitException.FromCli(result, "Commit");
            }

            var revParse = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["rev-parse", "HEAD"],
                WorkingDirectory = context.RepositoryPath,
            }, innerCt).ConfigureAwait(false);

            if (!revParse.IsSuccess)
                throw HistoriaGitException.FromCli(revParse, "Commit rev-parse");

            return GitObjectId.Parse(revParse.StandardOutput.Trim());
        }, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> FilterValidStagePathsAsync(
        IReadOnlyList<string> paths, CancellationToken ct)
    {
        var existingOnDisk = new HashSet<string>(StringComparer.Ordinal);
        var missingFromDisk = new List<string>();

        foreach (var path in paths)
        {
            var fullPath = Path.Combine(context.RepositoryPath, path);
            if (File.Exists(fullPath) || Directory.Exists(fullPath))
                existingOnDisk.Add(path);
            else
                missingFromDisk.Add(path);
        }

        if (missingFromDisk.Count == 0)
            return paths;

        var trackedInIndex = new HashSet<string>(StringComparer.Ordinal);
        var chunks = ChunkPathsByCommandLineLength(missingFromDisk);
        foreach (var chunk in chunks)
        {
            var args = new List<string>(chunk.Count + 3) { "ls-files", "-z", "--" };
            args.AddRange(chunk);

            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = args,
                WorkingDirectory = context.RepositoryPath,
            }, ct).ConfigureAwait(false);

            if (result.IsSuccess && !string.IsNullOrEmpty(result.StandardOutput))
            {
                var tracked = result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                foreach (var t in tracked)
                    trackedInIndex.Add(t);
            }
        }

        var resultList = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            if (existingOnDisk.Contains(path) || trackedInIndex.Contains(path))
                resultList.Add(path);
        }

        return resultList;
    }

    private static IEnumerable<List<string>> ChunkPathsByCommandLineLength(
        IReadOnlyList<string> paths, int maxChars = 25000)
    {
        var currentChunk = new List<string>();
        var currentLength = 0;

        foreach (var path in paths)
        {
            var itemLength = path.Length + 1;
            if (currentChunk.Count > 0 && currentLength + itemLength > maxChars)
            {
                yield return currentChunk;
                currentChunk = new List<string>();
                currentLength = 0;
            }

            currentChunk.Add(path);
            currentLength += itemLength;
        }

        if (currentChunk.Count > 0)
            yield return currentChunk;
    }

    private static bool IsIgnoredAdviceOnly(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return false;

        var lines = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var hasIgnoredNotice = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("The following paths are ignored", StringComparison.OrdinalIgnoreCase))
            {
                hasIgnoredNotice = true;
                continue;
            }
            if (line.StartsWith("hint:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith("fatal: no files added to index", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return hasIgnoredNotice;
    }
}
