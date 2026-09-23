namespace Historia.Services.PowerTools;

/// <inheritdoc cref="IConflictService"/>
public sealed class ConflictService(IGitCliService cli, ISettingsService settings) : IConflictService
{
    // 5 MB / 50 000 lines: budget twins of LibGit2Service's diff budgets.
    private const long MaxWorkingCopyBytes = 5 * 1024 * 1024;
    private const int MaxWorkingCopyLines = 50_000;

    public Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(RepositoryContext context, CancellationToken ct = default)
        => context.WithRepositoryAsync(ListConflicts, ct);

    public async Task<ConflictWorkingCopy> LoadWorkingCopyAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
    {
        var full = Path.Combine(context.RepositoryPath, filePath);

        // Submodules (directories) are never read as text files.
        if (Directory.Exists(full))
        {
            return new ConflictWorkingCopy
            {
                Path = filePath,
                DiskHash = string.Empty,
                IsBinary = true,
            };
        }

        if (!File.Exists(full))
        {
            return new ConflictWorkingCopy
            {
                Path = filePath,
                DiskHash = string.Empty,
            };
        }

        var info = new FileInfo(full);
        if (info.Length > MaxWorkingCopyBytes)
        {
            var largeBytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
            var largeHash = Convert.ToHexStringLower(SHA256.HashData(largeBytes));
            return new ConflictWorkingCopy
            {
                Path = filePath,
                DiskHash = largeHash,
                ExceedsBudget = true,
            };
        }

        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        var diskHash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        if (!TextFileFormat.TryDetect(bytes, out var format, out var text))
        {
            var isBinary = bytes.Contains((byte)0);
            return new ConflictWorkingCopy
            {
                Path = filePath,
                DiskHash = diskHash,
                IsBinary = isBinary,
                UnsupportedEncoding = !isBinary,
            };
        }

        if (CountLines(text) > MaxWorkingCopyLines)
        {
            return new ConflictWorkingCopy
            {
                Path = filePath,
                DiskHash = diskHash,
                Format = format,
                ExceedsBudget = true,
            };
        }

        var attrResult = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["check-attr", "conflict-marker-size", "--", filePath],
            WorkingDirectory = context.RepositoryPath,
        }, ct).ConfigureAwait(false);
        var markerSize = ParseMarkerSize(attrResult.StandardOutput);

        return new ConflictWorkingCopy
        {
            Path = filePath,
            Text = text,
            Format = format,
            DiskHash = diskHash,
            MarkerSize = markerSize,
        };
    }

    public async Task<ResolutionSaveResult> SaveResolutionAsync(
        RepositoryContext context,
        string filePath,
        string text,
        TextFileFormat format,
        string expectedDiskHash,
        bool stage,
        CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        var full = Path.Combine(context.RepositoryPath, filePath);

        byte[] currentBytes = File.Exists(full)
            ? await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false)
            : [];
        var currentHash = Convert.ToHexStringLower(SHA256.HashData(currentBytes));

        if (!string.Equals(currentHash, expectedDiskHash, StringComparison.OrdinalIgnoreCase))
        {
            return new ResolutionSaveResult { Saved = false, DiskHash = currentHash };
        }

        var encoded = format.Encode(text);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, encoded, ct).ConfigureAwait(false);
        var newHash = Convert.ToHexStringLower(SHA256.HashData(encoded));

        if (stage)
        {
            var add = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["add", "--", filePath],
                WorkingDirectory = context.RepositoryPath,
            }, ct).ConfigureAwait(false);
            if (!add.IsSuccess)
                throw HistoriaGitException.FromCli(add, "Save resolution (stage)");
        }

        return new ResolutionSaveResult { Saved = true, DiskHash = newHash };
    }

    public async Task ResolveWithSideAsync(RepositoryContext context, string filePath, ConflictSide side, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        var (isSubmodule, chosenStageSha, chosenStagePresent) = await context.WithRepositoryAsync(repo =>
        {
            var conflict = repo.Index.Conflicts[filePath]
                ?? throw new HistoriaGitException(GitErrorKind.RefNotFound, $"'{filePath}' is not a conflicted path.");

            var isSub = (conflict.Ours?.Mode == Mode.GitLink)
                     || (conflict.Theirs?.Mode == Mode.GitLink)
                     || (conflict.Ancestor?.Mode == Mode.GitLink);

            var chosenEntry = side == ConflictSide.Ours ? conflict.Ours : conflict.Theirs;
            return (isSub, chosenEntry?.Id.Sha, chosenEntry is not null);
        }, ct).ConfigureAwait(false);

        if (isSubmodule)
        {
            if (chosenStagePresent && !string.IsNullOrEmpty(chosenStageSha))
            {
                var update = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["update-index", "--cacheinfo", $"160000,{chosenStageSha},{filePath}"],
                    WorkingDirectory = context.RepositoryPath,
                }, ct).ConfigureAwait(false);
                if (!update.IsSuccess)
                    throw HistoriaGitException.FromCli(update, "Resolve submodule side");
            }
            else
            {
                var rm = await cli.RunAsync(new GitCliRequest
                {
                    Arguments = ["rm", "--cached", "--", filePath],
                    WorkingDirectory = context.RepositoryPath,
                }, ct).ConfigureAwait(false);
                if (!rm.IsSuccess)
                    throw HistoriaGitException.FromCli(rm, "Resolve submodule side (rm)");
            }
            return;
        }

        if (chosenStagePresent)
        {
            var sideArg = side == ConflictSide.Ours ? "--ours" : "--theirs";
            var checkout = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["checkout", sideArg, "--", filePath],
                WorkingDirectory = context.RepositoryPath,
            }, ct).ConfigureAwait(false);
            if (!checkout.IsSuccess)
                throw HistoriaGitException.FromCli(checkout, $"Resolve ({sideArg})");

            var add = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["add", "--", filePath],
                WorkingDirectory = context.RepositoryPath,
            }, ct).ConfigureAwait(false);
            if (!add.IsSuccess)
                throw HistoriaGitException.FromCli(add, "Resolve (stage)");
        }
        else
        {
            var rm = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["rm", "-f", "--", filePath],
                WorkingDirectory = context.RepositoryPath,
            }, ct).ConfigureAwait(false);
            if (!rm.IsSuccess)
                throw HistoriaGitException.FromCli(rm, "Resolve (delete)");
        }
    }

    public async Task RecreateConflictAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        var res = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["checkout", "--conflict=zdiff3", "--", filePath],
            WorkingDirectory = context.RepositoryPath,
        }, ct).ConfigureAwait(false);

        if (!res.IsSuccess)
            throw HistoriaGitException.FromCli(res, "Recreate conflict");
    }

    public async Task<ConflictSideLabels> GetSideLabelsAsync(RepositoryContext context, CancellationToken ct = default)
    {
        var op = RepoOperation(context);
        var gitDir = context.GitDirectory;

        return await context.WithRepositoryAsync(repo =>
        {
            var headSha = repo.Head.Tip?.Sha ?? string.Empty;
            var headBranch = repo.Head.FriendlyName;
            var headShort = headSha.Length >= 7 ? headSha[..7] : headSha;
            var headSubject = repo.Head.Tip?.MessageShort ?? string.Empty;
            var oursTitle = headBranch;
            var oursDetail = $"{headShort} {headSubject}".Trim();

            string theirsTitle;
            string theirsDetail;
            string opHeadSha = string.Empty;
            string msgNum = string.Empty;

            switch (op)
            {
                case OperationState.Merging:
                {
                    var mergeHeadFile = Path.Combine(gitDir, "MERGE_HEAD");
                    if (File.Exists(mergeHeadFile))
                        opHeadSha = File.ReadAllText(mergeHeadFile).Trim();

                    var mergeMsgFile = Path.Combine(gitDir, "MERGE_MSG");
                    var firstMsgLine = File.Exists(mergeMsgFile)
                        ? (File.ReadLines(mergeMsgFile).FirstOrDefault() ?? string.Empty).Trim()
                        : string.Empty;

                    var commit = !string.IsNullOrEmpty(opHeadSha) ? repo.Lookup<Commit>(opHeadSha) : null;
                    var shortSha = opHeadSha.Length >= 7 ? opHeadSha[..7] : opHeadSha;
                    theirsTitle = !string.IsNullOrEmpty(firstMsgLine) ? firstMsgLine : (shortSha.Length > 0 ? shortSha : "Incoming merge");
                    theirsDetail = commit is not null ? $"{shortSha} {commit.MessageShort}".Trim() : shortSha;
                    break;
                }

                case OperationState.Rebasing:
                {
                    var rebaseMergeDir = Path.Combine(gitDir, "rebase-merge");
                    var rebaseApplyDir = Path.Combine(gitDir, "rebase-apply");
                    var rebaseDir = Directory.Exists(rebaseMergeDir) ? rebaseMergeDir : rebaseApplyDir;

                    var ontoPath = Path.Combine(rebaseDir, "onto");
                    var ontoSha = File.Exists(ontoPath) ? File.ReadAllText(ontoPath).Trim() : string.Empty;
                    var ontoCommit = !string.IsNullOrEmpty(ontoSha) ? repo.Lookup<Commit>(ontoSha) : null;
                    var ontoShort = ontoSha.Length >= 7 ? ontoSha[..7] : ontoSha;

                    oursTitle = "the branch you are rebasing onto";
                    oursDetail = ontoCommit is not null ? $"{ontoShort} {ontoCommit.MessageShort}".Trim() : ontoShort;

                    var headNamePath = Path.Combine(rebaseDir, "head-name");
                    var headName = File.Exists(headNamePath) ? File.ReadAllText(headNamePath).Trim() : string.Empty;
                    if (headName.StartsWith("refs/heads/", StringComparison.Ordinal))
                        headName = headName[11..];

                    var stoppedShaPath = Path.Combine(rebaseDir, "stopped-sha");
                    var stoppedSha = File.Exists(stoppedShaPath) ? File.ReadAllText(stoppedShaPath).Trim() : string.Empty;
                    if (string.IsNullOrEmpty(stoppedSha))
                    {
                        var rebaseHeadFile = Path.Combine(gitDir, "REBASE_HEAD");
                        if (File.Exists(rebaseHeadFile))
                            stoppedSha = File.ReadAllText(rebaseHeadFile).Trim();
                    }
                    opHeadSha = stoppedSha;

                    var stoppedCommit = !string.IsNullOrEmpty(stoppedSha) ? repo.Lookup<Commit>(stoppedSha) : null;
                    var stoppedShort = stoppedSha.Length >= 7 ? stoppedSha[..7] : stoppedSha;

                    theirsTitle = !string.IsNullOrEmpty(headName) ? $"{headName} (replaying)" : "your commit being replayed";
                    theirsDetail = stoppedCommit is not null ? $"{stoppedShort} {stoppedCommit.MessageShort}".Trim() : stoppedShort;

                    var msgNumFile = Path.Combine(rebaseMergeDir, "msgnum");
                    if (File.Exists(msgNumFile))
                        msgNum = File.ReadAllText(msgNumFile).Trim();
                    else
                    {
                        var nextFile = Path.Combine(rebaseApplyDir, "next");
                        if (File.Exists(nextFile))
                            msgNum = File.ReadAllText(nextFile).Trim();
                    }
                    break;
                }

                case OperationState.CherryPicking:
                {
                    var cherryHeadFile = Path.Combine(gitDir, "CHERRY_PICK_HEAD");
                    if (File.Exists(cherryHeadFile))
                        opHeadSha = File.ReadAllText(cherryHeadFile).Trim();

                    var commit = !string.IsNullOrEmpty(opHeadSha) ? repo.Lookup<Commit>(opHeadSha) : null;
                    var shortSha = opHeadSha.Length >= 7 ? opHeadSha[..7] : opHeadSha;
                    theirsTitle = $"Cherry-pick {shortSha}".Trim();
                    theirsDetail = commit is not null ? $"{shortSha} {commit.MessageShort}".Trim() : shortSha;
                    break;
                }

                case OperationState.Reverting:
                {
                    var revertHeadFile = Path.Combine(gitDir, "REVERT_HEAD");
                    if (File.Exists(revertHeadFile))
                        opHeadSha = File.ReadAllText(revertHeadFile).Trim();

                    var commit = !string.IsNullOrEmpty(opHeadSha) ? repo.Lookup<Commit>(opHeadSha) : null;
                    var shortSha = opHeadSha.Length >= 7 ? opHeadSha[..7] : opHeadSha;
                    theirsTitle = $"Revert {shortSha}".Trim();
                    theirsDetail = commit is not null ? $"{shortSha} {commit.MessageShort}".Trim() : shortSha;
                    break;
                }

                default:
                {
                    theirsTitle = "Stashed / applied changes";
                    theirsDetail = "Working tree changes";
                    break;
                }
            }

            var stopId = $"{headSha}:{opHeadSha}:{msgNum}";

            return new ConflictSideLabels
            {
                OursTitle = oursTitle,
                OursDetail = oursDetail,
                TheirsTitle = theirsTitle,
                TheirsDetail = theirsDetail,
                Operation = op,
                StopId = stopId,
            };
        }, ct).ConfigureAwait(false);
    }

    public async Task<bool> LaunchExternalMergeToolAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
    {
        var tool = settings.Current.ExternalMergeTool;
        if (string.IsNullOrWhiteSpace(tool))
        {
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["mergetool", "--no-prompt", "--", filePath],
                WorkingDirectory = context.RepositoryPath,
                Timeout = TimeSpan.FromMinutes(30),
            }, ct).ConfigureAwait(false);
            return result.ExitCode == 0;
        }

        var temp = Directory.CreateTempSubdirectory("historia-merge-").FullName;
        try
        {
            var ext = Path.GetExtension(filePath);
            var (hasStage1, hasStage2, hasStage3) = await context.WithRepositoryAsync(repo =>
            {
                var conflict = repo.Index.Conflicts[filePath];
                return (conflict?.Ancestor is not null, conflict?.Ours is not null, conflict?.Theirs is not null);
            }, ct).ConfigureAwait(false);

            var basePath = await CheckoutStageTempAsync(context, filePath, 1, hasStage1, temp, "BASE", ext, ct).ConfigureAwait(false);
            var localPath = await CheckoutStageTempAsync(context, filePath, 2, hasStage2, temp, "LOCAL", ext, ct).ConfigureAwait(false);
            var remotePath = await CheckoutStageTempAsync(context, filePath, 3, hasStage3, temp, "REMOTE", ext, ct).ConfigureAwait(false);
            var mergedPath = Path.Combine(context.RepositoryPath, filePath);

            var tokens = Tokenize(tool);
            if (tokens.Count == 0)
                return false;

            string Expand(string token) => token
                .Replace("$BASE", basePath)
                .Replace("$LOCAL", localPath)
                .Replace("$REMOTE", remotePath)
                .Replace("$MERGED", mergedPath);

            var psi = new ProcessStartInfo(Expand(tokens[0])) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var token in tokens.Skip(1))
                psi.ArgumentList.Add(Expand(token));

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
            if (process is null)
                return false;

            using (process)
                await process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (!File.Exists(mergedPath))
                return true;

            var merged = await File.ReadAllTextAsync(mergedPath, ct).ConfigureAwait(false);
            return !merged.Contains("<<<<<<<", StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    private async Task<string> CheckoutStageTempAsync(
        RepositoryContext context,
        string filePath,
        int stage,
        bool stagePresent,
        string tempDir,
        string label,
        string ext,
        CancellationToken ct)
    {
        var targetFile = Path.Combine(tempDir, $"{label}{ext}");
        if (!stagePresent)
        {
            await File.WriteAllBytesAsync(targetFile, [], ct).ConfigureAwait(false);
            return targetFile;
        }

        var stagePrefix = Path.Combine(tempDir, $"stage{stage}") + Path.DirectorySeparatorChar;
        var subDir = Path.GetDirectoryName(Path.Combine(stagePrefix, filePath));
        if (!string.IsNullOrEmpty(subDir))
            Directory.CreateDirectory(subDir);

        var res = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["checkout-index", $"--prefix={stagePrefix}", $"--stage={stage}", "--", filePath],
            WorkingDirectory = context.RepositoryPath,
        }, ct).ConfigureAwait(false);

        var extracted = Path.Combine(stagePrefix, filePath);
        if (File.Exists(extracted))
        {
            File.Move(extracted, targetFile, overwrite: true);
        }
        else
        {
            await File.WriteAllBytesAsync(targetFile, [], ct).ConfigureAwait(false);
        }

        return targetFile;
    }

    public async Task ContinueOperationAsync(RepositoryContext context, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        var stillUnmerged = await context.WithRepositoryAsync(HasUnmergedPaths, ct).ConfigureAwait(false);
        if (stillUnmerged)
            throw new HistoriaGitException(GitErrorKind.MergeConflict, "Some paths are still unmerged. Resolve every conflicted file first.");

        await RunSequencerAsync(context, "--continue", ct).ConfigureAwait(false);
    }

    public Task SkipCurrentAsync(RepositoryContext context, CancellationToken ct = default)
    {
        var op = RepoOperation(context);
        if (op == OperationState.Merging)
            throw new HistoriaGitException(GitErrorKind.Unknown, "A merge cannot skip a commit — resolve or abort it.");
        return RunSequencerAsync(context, "--skip", ct);
    }

    public Task AbortOperationAsync(RepositoryContext context, CancellationToken ct = default)
        => RunSequencerAsync(context, "--abort", ct);

    // ── internals ──

    private async Task RunSequencerAsync(RepositoryContext context, string action, CancellationToken ct)
    {
        using var _ = context.BeginSelfOperation();

        var verb = RepoOperation(context) switch
        {
            OperationState.Merging => "merge",
            OperationState.Rebasing => "rebase",
            OperationState.CherryPicking => "cherry-pick",
            OperationState.Reverting => "revert",
            _ => throw new HistoriaGitException(GitErrorKind.Unknown, "No operation is in progress."),
        };

        var env = new Dictionary<string, string> { ["GIT_EDITOR"] = "true" }; // never open an editor on --continue
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = [verb, action],
            WorkingDirectory = context.RepositoryPath,
            ExtraEnvironment = env,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess && result.ErrorKind != GitErrorKind.MergeConflict)
            throw HistoriaGitException.FromCli(result, $"{verb} {action}");

        await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);
    }

    private static OperationState RepoOperation(RepositoryContext context)
        => Historia.Services.State.RepositoryStateService.DetectOperationState(context.GitDirectory);

    private static IReadOnlyList<ConflictFile> ListConflicts(Repository repo)
    {
        repo.RetrieveStatus();
        var result = new List<ConflictFile>();
        foreach (var conflict in repo.Index.Conflicts)
        {
            var path = conflict.Ancestor?.Path ?? conflict.Ours?.Path ?? conflict.Theirs?.Path ?? "";
            var status = DeriveStatus(conflict);
            var isBinary = IsBinary(repo, conflict.Ours) || IsBinary(repo, conflict.Theirs) || IsBinary(repo, conflict.Ancestor);
            var isSubmodule = (conflict.Ancestor?.Mode == Mode.GitLink)
                           || (conflict.Ours?.Mode == Mode.GitLink)
                           || (conflict.Theirs?.Mode == Mode.GitLink);

            result.Add(new ConflictFile
            {
                FilePath = path,
                Status = status,
                IsBinary = isBinary,
                IsSubmodule = isSubmodule,
            });
        }

        return result;
    }

    private static bool HasUnmergedPaths(Repository repo)
    {
        repo.RetrieveStatus();
        return repo.Index.Conflicts.Any();
    }

    private static ConflictStatus DeriveStatus(Conflict c)
    {
        var haveBase = c.Ancestor is not null;
        var haveOurs = c.Ours is not null;
        var haveTheirs = c.Theirs is not null;

        return (haveBase, haveOurs, haveTheirs) switch
        {
            (true, true, true) => ConflictStatus.BothModified,
            (false, true, true) => ConflictStatus.BothAdded,
            (true, false, true) => ConflictStatus.DeletedByUs,
            (true, true, false) => ConflictStatus.DeletedByThem,
            (false, true, false) => ConflictStatus.AddedByUs,
            (false, false, true) => ConflictStatus.AddedByThem,
            _ => ConflictStatus.BothDeletedRenamed,
        };
    }

    private static bool IsBinary(Repository repo, IndexEntry? entry)
        => entry is not null && (repo.Lookup<Blob>(entry.Id)?.IsBinary ?? false);

    private static int CountLines(string text)
    {
        var count = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                count++;
                if (i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
            }
            else if (text[i] == '\n')
            {
                count++;
            }
        }
        return count;
    }

    private static int ParseMarkerSize(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return 7;
        var parts = output.Trim().Split(':');
        if (parts.Length >= 3 && int.TryParse(parts[^1].Trim(), out var size) && size > 0)
            return size;
        return 7;
    }

    /// <summary>
    /// Splits a user-configured external-tool command line into its executable and argument tokens,
    /// honouring double-quoted segments.
    /// </summary>
    internal static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }

            current.Append(c);
            hasToken = true;
        }

        if (hasToken)
            tokens.Add(current.ToString());

        return tokens;
    }
}
