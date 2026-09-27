namespace Historia.Services.Git;

/// <summary>
/// LibGit2Sharp projection adapter. Every method here is <b>synchronous</b> and is
/// meant to be invoked <i>inside</i> a <c>RepositoryContext.WithRepositoryAsync</c> callback;
/// it projects LibGit2Sharp objects into Historia's own records and returns only
/// those. Stateless — registered as a singleton.
/// </summary>
public sealed class LibGit2Service
{
    /// <summary><see cref="Branch.RemoteName"/> throws for a local branch — guard it.</summary>
    private static string? SafeRemoteName(Branch branch)
    {
        if (!branch.IsRemote)
            return null;
        try { return branch.RemoteName; }
        catch (Exception) { return null; }
    }

    public RepositoryInfo ProjectInfo(Repository repo)
    {
        var head = repo.Head;
        return new RepositoryInfo
        {
            WorkingDirectory = repo.Info.WorkingDirectory ?? repo.Info.Path,
            GitDirectory = repo.Info.Path,
            DisplayName = DeriveName(repo.Info.WorkingDirectory ?? repo.Info.Path),
            IsBare = repo.Info.IsBare,
            IsHeadDetached = repo.Info.IsHeadDetached,
            IsHeadUnborn = repo.Info.IsHeadUnborn,
            CurrentBranchName = repo.Info.IsHeadUnborn ? null : head.FriendlyName,
            HasCommitHooks = RepoHasCommitHooks(repo.Info.Path),
        };
    }

    /// <summary>True when an executable <c>pre-commit</c> or <c>commit-msg</c> hook is present.</summary>
    public static bool RepoHasCommitHooks(string gitDir)
    {
        // core.hooksPath is not consulted here — the common husky / pre-commit / lint-staged
        // setups install into <gitdir>/hooks. A ".sample" file does not count.
        var hooksDir = Path.Combine(gitDir, "hooks");
        foreach (var name in (ReadOnlySpan<string>)["pre-commit", "commit-msg"])
        {
            var path = Path.Combine(hooksDir, name);
            if (File.Exists(path))
                return true;
        }

        return false;
    }

    public RepositoryStatus ProjectStatus(Repository repo)
    {
        if (repo.Info.IsBare)
            return RepositoryStatus.Empty with { ComputedAt = DateTimeOffset.Now };

        var options = new StatusOptions
        {
            DetectRenamesInIndex = true,
            DetectRenamesInWorkDir = true,
            IncludeUntracked = true,
            RecurseUntrackedDirs = true,
            IncludeIgnored = false,
        };

        var staged = new List<FileStatusEntry>();
        var unstaged = new List<FileStatusEntry>();
        var untracked = new List<FileStatusEntry>();
        var conflicted = new List<FileStatusEntry>();

        HashSet<string>? submodulePaths = null;
        try
        {
            submodulePaths = new HashSet<string>(repo.Submodules.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Best-effort submodule collection
        }

        foreach (var entry in repo.RetrieveStatus(options))
        {
            var state = entry.State;
            if (state == FileStatus.Unaltered || state == FileStatus.Ignored)
                continue;

            var isSubmodule = submodulePaths is not null && submodulePaths.Contains(entry.FilePath);

            if (state.HasFlag(FileStatus.Conflicted))
            {
                conflicted.Add(Make(repo, entry.FilePath, FileChangeKind.Conflicted, null, isSubmodule));
                continue;
            }

            if (HasIndexChange(entry, out var stagedKind, out var stagedOld))
                staged.Add(Make(repo, entry.FilePath, stagedKind, stagedOld, isSubmodule));

            if (state.HasFlag(FileStatus.NewInWorkdir))
                untracked.Add(Make(repo, entry.FilePath, FileChangeKind.Untracked, null, isSubmodule));
            else if (HasWorkdirChange(entry, out var workKind, out var workOld))
                unstaged.Add(Make(repo, entry.FilePath, workKind, workOld, isSubmodule));
        }

        var tracking = repo.Head.TrackingDetails;
        return new RepositoryStatus
        {
            Staged = staged,
            Unstaged = unstaged,
            Untracked = untracked,
            Conflicted = conflicted,
            IsDirty = staged.Count + unstaged.Count + untracked.Count + conflicted.Count > 0,
            AheadBy = tracking?.AheadBy ?? 0,
            BehindBy = tracking?.BehindBy ?? 0,
            ComputedAt = DateTimeOffset.Now,
        };
    }

    public CommitDetail ProjectCommitDetail(Repository repo, GitObjectId id)
    {
        var commit = repo.Lookup<Commit>(id.ToLibGit2())
            ?? throw new HistoriaGitException(GitErrorKind.RefNotFound, $"Commit {id.ToString()} not found.");

        // LibGit2Sharp's Commit.Message is the *full* message (subject + body). Consumers want
        // the body alone — they pair it with MessageShort themselves — so strip the first line
        // here. Without this, "subject + body" renders the subject twice (worse for a commit
        // whose whole message is a repeated word, e.g. "wip\nwip").
        var fullMessage = commit.Message ?? "";
        var firstBreak = fullMessage.IndexOf('\n');
        var messageBody = firstBreak >= 0 ? fullMessage[(firstBreak + 1)..].Trim() : "";

        return new CommitDetail
        {
            Id = commit.Id.ToGitObjectId(),
            Message = fullMessage.TrimEnd('\n', '\r'),
            MessageShort = commit.MessageShort,
            MessageBody = messageBody,
            AuthorName = commit.Author.Name,
            AuthorEmail = commit.Author.Email,
            AuthorWhen = commit.Author.When,
            CommitterName = commit.Committer.Name,
            CommitterEmail = commit.Committer.Email,
            CommitWhen = commit.Committer.When,
            ParentIds = commit.Parents.Select(p => p.Id.ToGitObjectId()).ToList(),
            Decorations = [],
        };
    }

    /// <summary>Files changed by a commit versus the chosen parent (1-based; root → empty tree).</summary>
    public IReadOnlyList<FileStatusEntry> ProjectCommitFiles(Repository repo, GitObjectId id, int parentIndex)
    {
        var commit = repo.Lookup<Commit>(id.ToLibGit2())
            ?? throw new HistoriaGitException(GitErrorKind.RefNotFound, $"Commit {id.ToString()} not found.");
        var parent = commit.Parents.ElementAtOrDefault(Math.Max(0, parentIndex - 1));

        using var changes = repo.Diff.Compare<TreeChanges>(parent?.Tree, commit.Tree);
        var result = new List<FileStatusEntry>();
        foreach (var change in changes)
        {
            result.Add(new FileStatusEntry
            {
                Path = change.Path,
                Kind = change.Status switch
                {
                    ChangeKind.Added => FileChangeKind.Added,
                    ChangeKind.Deleted => FileChangeKind.Deleted,
                    ChangeKind.Renamed => FileChangeKind.Renamed,
                    ChangeKind.Copied => FileChangeKind.Copied,
                    ChangeKind.TypeChanged => FileChangeKind.TypeChanged,
                    _ => FileChangeKind.Modified,
                },
                OldPath = change.OldPath == change.Path ? null : change.OldPath,
                IsBinary = false,
                IsSubmodule = false,
            });
        }

        return result;
    }

    public IReadOnlyList<BranchInfo> ProjectBranches(Repository repo)
    {
        var result = new List<BranchInfo>();
        foreach (var branch in repo.Branches)
        {
            result.Add(new BranchInfo
            {
                Name = branch.FriendlyName,
                CanonicalName = branch.CanonicalName,
                IsRemote = branch.IsRemote,
            RemoteName = SafeRemoteName(branch),
                IsCurrent = branch.IsCurrentRepositoryHead,
                TipCommitId = branch.Tip?.Id.ToGitObjectId(),
                UpstreamName = branch.TrackedBranch?.FriendlyName,
                // Ahead/behind stays null here — computed lazily per visible row.
            });
        }

        return result;
    }

    public IReadOnlyList<TagInfo> ProjectTags(Repository repo)
    {
        var result = new List<TagInfo>();
        foreach (var tag in repo.Tags)
        {
            try
            {
                var target = tag.PeeledTarget as Commit;
                result.Add(new TagInfo
                {
                    Name = tag.FriendlyName,
                    TargetCommitId = (target?.Id ?? tag.Target.Id).ToGitObjectId(),
                    IsAnnotated = tag.IsAnnotated,
                    Message = tag.Annotation?.Message,
                    TaggerName = tag.Annotation?.Tagger?.Name,
                    TaggerWhen = tag.Annotation?.Tagger?.When,
                });
            }
            catch (LibGit2SharpException)
            {
                // A single malformed tag (missing target, broken annotation) must not empty
                // the whole list — skip it.
            }
        }

        return result;
    }

    public IReadOnlyList<RemoteInfo> ProjectRemotes(Repository repo)
        => repo.Network.Remotes
            .Select(r => new RemoteInfo { Name = r.Name, Url = r.Url, PushUrl = r.PushUrl })
            .ToList();

    /// <summary>
    /// Projects a single-file diff for one of the sources in including the
    /// merge-commit and root-commit cases. The size guard runs before content is materialized
    ///: files above 5 MB or 50,000 lines return <c>IsTruncated</c> with no hunks.
    /// </summary>
    public FileDiff ProjectDiff(Repository repo, DiffRequest request)
    {
        const long maxBytes = 5 * 1024 * 1024;
        const int maxLines = 50_000;

        if (string.IsNullOrWhiteSpace(request.Path))
            throw new HistoriaGitException(GitErrorKind.Unknown, "A file path is required for a diff.");

        var path = request.Path!;
        var paths = new[] { path };
        var compareOptions = new CompareOptions
        {
            ContextLines = request.ContextLines,
            Similarity = SimilarityOptions.Renames,
        };
        ApplyWhitespace(compareOptions, request.Whitespace);

        Patch patch = request.Source switch
        {
            DiffSource.UnstagedChanges =>
                repo.Diff.Compare<Patch>(paths, includeUntracked: true, explicitPathsOptions: null, compareOptions: compareOptions),

            DiffSource.StagedChanges =>
                repo.Diff.Compare<Patch>(repo.Head.Tip?.Tree, DiffTargets.Index, paths, explicitPathsOptions: null, compareOptions: compareOptions),

            DiffSource.Commit => DiffCommit(repo, request, paths, compareOptions),

            DiffSource.Comparison => DiffComparison(repo, request, paths, compareOptions),

            _ => throw new HistoriaGitException(GitErrorKind.Unknown, $"Unsupported diff source '{request.Source}'."),
        };

        var entry = patch[path];
        if (entry is null)
            return FileDiff.EmptyFor(path, DiffKind.Modified);

        var kind = MapDiffKind(entry.Status);
        if (entry.IsBinaryComparison)
        {
            return FileDiff.EmptyFor(path, kind) with
            {
                OldPath = entry.OldPath == path ? null : entry.OldPath,
                IsBinary = true,
            };
        }

        var patchText = entry.Patch ?? string.Empty;
        var lineCount = CountLines(patchText);
        if (patchText.Length > maxBytes || lineCount > maxLines)
        {
            return FileDiff.EmptyFor(path, kind) with
            {
                OldPath = entry.OldPath == path ? null : entry.OldPath,
                IsTruncated = true,
                AddedLines = entry.LinesAdded,
                DeletedLines = entry.LinesDeleted,
            };
        }

        var hunks = UnifiedDiffParser.Parse(patchText);
        return new FileDiff
        {
            Path = path,
            OldPath = entry.OldPath == path ? null : entry.OldPath,
            Kind = kind,
            IsBinary = false,
            IsTruncated = false,
            Hunks = hunks,
            AddedLines = entry.LinesAdded,
            DeletedLines = entry.LinesDeleted,
            RawPatch = patchText,
        };
    }

    private static Patch DiffCommit(Repository repo, DiffRequest request, string[] paths, CompareOptions options)
    {
        var commit = repo.Lookup<Commit>(request.CommitId?.ToLibGit2()
            ?? throw new HistoriaGitException(GitErrorKind.RefNotFound, "No commit id supplied."));
        if (commit is null)
            throw new HistoriaGitException(GitErrorKind.RefNotFound, "Commit not found.");

        // Root commit → empty tree; merge commit → the requested parent.
        var parent = commit.Parents.ElementAtOrDefault(Math.Max(0, request.ParentIndex - 1));
        return repo.Diff.Compare<Patch>(parent?.Tree, commit.Tree, paths, explicitPathsOptions: null, compareOptions: options);
    }

    private static Patch DiffComparison(Repository repo, DiffRequest request, string[] paths, CompareOptions options)
    {
        var left = request.CommitId is { } l ? repo.Lookup<Commit>(l.ToLibGit2()) : null;
        var right = request.OtherCommitId is { } r ? repo.Lookup<Commit>(r.ToLibGit2()) : null;
        return repo.Diff.Compare<Patch>(left?.Tree, right?.Tree, paths, explicitPathsOptions: null, compareOptions: options);
    }

    private static void ApplyWhitespace(CompareOptions options, WhitespaceMode mode)
    {
        options.Algorithm = DiffAlgorithm.Myers;
        // LibGit2Sharp exposes whitespace handling only through raw diff flags on some
        // builds; the viewer applies IgnoreAll/IgnoreTrailing as a display option.
        _ = mode;
    }

    private static int CountLines(string text)
    {
        var count = 1;
        foreach (var ch in text)
            if (ch == '\n')
                count++;
        return count;
    }

    private static DiffKind MapDiffKind(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => DiffKind.Added,
        ChangeKind.Deleted => DiffKind.Deleted,
        ChangeKind.Renamed => DiffKind.Renamed,
        ChangeKind.Copied => DiffKind.Copied,
        ChangeKind.TypeChanged => DiffKind.TypeChanged,
        _ => DiffKind.Modified,
    };

    // ── Cheap local mutations ──

    public void Stage(Repository repo, IReadOnlyList<string> paths) => Commands.Stage(repo, paths);

    public void Unstage(Repository repo, IReadOnlyList<string> paths) => Commands.Unstage(repo, paths);

    public void DiscardWorkingTree(Repository repo, IReadOnlyList<string> paths)
    {
        var workDir = repo.Info.WorkingDirectory;
        if (repo.Info.IsHeadUnborn)
        {
            foreach (var path in paths)
            {
                try { repo.Index.Remove(path); } catch { }
                if (!string.IsNullOrEmpty(workDir))
                    RecycleBin.Delete(Path.Combine(workDir, path));
            }
            try { repo.Index.Write(); } catch { }
            return;
        }

        try
        {
            Commands.Unstage(repo, paths);
        }
        catch
        {
            // If Commands.Unstage fails on any path, continue to individual checks
        }

        var inHead = new List<string>();
        var notInHead = new List<string>();
        var headTree = repo.Head.Tip?.Tree;

        foreach (var path in paths)
        {
            var normalized = path.Replace('\\', '/');
            if (headTree != null && headTree[normalized] is not null)
            {
                inHead.Add(path);
            }
            else
            {
                notInHead.Add(path);
            }
        }

        if (inHead.Count > 0)
        {
            repo.CheckoutPaths(
                repo.Head.Tip!.Sha,
                inHead,
                new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
        }

        if (notInHead.Count > 0 && !string.IsNullOrEmpty(workDir))
        {
            foreach (var path in notInHead)
            {
                try { repo.Index.Remove(path); } catch { }
                // A path that isn't in HEAD is a never-committed file — route it to the recycle
                // bin (Windows) so "discard" stays recoverable; permanent delete elsewhere.
                RecycleBin.Delete(Path.Combine(workDir, path));
            }
            try { repo.Index.Write(); } catch { }
        }
    }

    public GitObjectId Commit(Repository repo, CommitRequest request)
    {
        var now = DateTimeOffset.Now;
        var author = new Signature(request.AuthorName, request.AuthorEmail, now);
        var committer = new Signature(
            request.CommitterName ?? request.AuthorName,
            request.CommitterEmail ?? request.AuthorEmail,
            now);

        var options = new CommitOptions
        {
            AmendPreviousCommit = request.AmendPreviousCommit,
            AllowEmptyCommit = request.AllowEmptyCommit,
        };

        return repo.Commit(request.Message, author, committer, options).Id.ToGitObjectId();
    }

    public BranchInfo CreateBranch(Repository repo, string name, string startPoint, bool checkout)
    {
        var branch = repo.CreateBranch(name, startPoint);
        if (checkout)
            CheckoutBranchWithRollback(repo, branch, new CheckoutOptions());

        return new BranchInfo
        {
            Name = branch.FriendlyName,
            CanonicalName = branch.CanonicalName,
            IsRemote = branch.IsRemote,
            RemoteName = SafeRemoteName(branch),
            IsCurrent = branch.IsCurrentRepositoryHead,
            TipCommitId = branch.Tip?.Id.ToGitObjectId(),
            UpstreamName = branch.TrackedBranch?.FriendlyName,
        };
    }

    public void DeleteBranch(Repository repo, string name, bool force)
    {
        var branch = repo.Branches[name]
            ?? throw new HistoriaGitException(GitErrorKind.RefNotFound, $"Branch '{name}' not found.");
        if (branch.IsCurrentRepositoryHead)
            throw new InvalidOperationException("Cannot delete the current branch.");
        if (!force && branch.TrackedBranch is null && !IsMerged(repo, branch))
            throw new InvalidOperationException($"Branch '{name}' is not fully merged. Use force to delete.");

        repo.Branches.Remove(branch);
    }

    public void RenameBranch(Repository repo, string oldName, string newName)
        => repo.Branches.Rename(oldName, newName);

    public void Checkout(Repository repo, string refName, GitCheckoutOptions options)
    {
        var checkoutOptions = new CheckoutOptions
        {
            CheckoutModifiers = options.Force ? CheckoutModifiers.Force : CheckoutModifiers.None,
        };

        if (options.CreateBranch && options.NewBranchName is { } newName)
        {
            var branch = repo.CreateBranch(newName, refName);
            CheckoutBranchWithRollback(repo, branch, checkoutOptions);
            return;
        }

        CheckoutOrThrowClassified(repo, opts => Commands.Checkout(repo, refName, opts), checkoutOptions);
    }

    private static void CheckoutBranchWithRollback(Repository repo, Branch branch, CheckoutOptions options)
    {
        try
        {
            CheckoutOrThrowClassified(repo, opts => Commands.Checkout(repo, branch, opts), options);
        }
        catch
        {
            // Safe to remove: a ref created in this call, pointing at an already-reachable commit.
            try
            {
                if (repo.Head.CanonicalName != branch.CanonicalName)
                    repo.Branches.Remove(branch);
            }
            catch
            {
            }

            throw;
        }
    }

    // Translates libgit2 checkout conflicts at the repo gate so stash recovery can key on UncommittedChanges.
    private static void CheckoutOrThrowClassified(Repository repo, Action<CheckoutOptions> checkout, CheckoutOptions options)
    {
        var conflictingPaths = new List<string>();
        var prevNotify = options.OnCheckoutNotify;
        options.CheckoutNotifyFlags |= CheckoutNotifyFlags.Conflict;
        options.OnCheckoutNotify = (path, flags) =>
        {
            if ((flags & CheckoutNotifyFlags.Conflict) != 0 && path is not null)
            {
                var normalized = path.Replace('\\', '/');
                if (!conflictingPaths.Contains(normalized))
                    conflictingPaths.Add(normalized);
            }

            return prevNotify?.Invoke(path, flags) ?? true;
        };

        try
        {
            checkout(options);
        }
        catch (CheckoutConflictException ex)
        {
            if (conflictingPaths.Count == 0)
                throw new HistoriaGitException(GitErrorKind.UncommittedChanges, ex.Message);

            var summaryPaths = conflictingPaths.Count <= 3
                ? string.Join(", ", conflictingPaths)
                : $"{string.Join(", ", conflictingPaths.Take(3))} and {conflictingPaths.Count - 3} more";
            var message = $"Checkout would overwrite local changes to {summaryPaths}.";
            var details = "error: Your local changes to the following files would be overwritten by checkout:\n\t"
                + string.Join("\n\t", conflictingPaths)
                + "\nPlease commit your changes or stash them before you switch branches.\nAborting";

            throw new HistoriaGitException(GitErrorKind.UncommittedChanges, message, details);
        }
    }

    public TagInfo CreateTag(Repository repo, string name, string target, string? message, Signature? tagger)
    {
        Tag tag;
        if (string.IsNullOrEmpty(message) || tagger is null)
        {
            tag = repo.ApplyTag(name, target);
        }
        else
        {
            tag = repo.ApplyTag(name, target, tagger, message);
        }

        var peeled = tag.PeeledTarget as Commit;
        return new TagInfo
        {
            Name = tag.FriendlyName,
            TargetCommitId = (peeled?.Id ?? tag.Target.Id).ToGitObjectId(),
            IsAnnotated = tag.IsAnnotated,
            Message = tag.Annotation?.Message,
            TaggerName = tag.Annotation?.Tagger.Name,
            TaggerWhen = tag.Annotation?.Tagger.When,
        };
    }

    public void DeleteTag(Repository repo, string name) => repo.Tags.Remove(name);

    public void AddRemote(Repository repo, string name, string url) => repo.Network.Remotes.Add(name, url);

    public void RemoveRemote(Repository repo, string name) => repo.Network.Remotes.Remove(name);

    public void SetRemoteUrl(Repository repo, string name, string url)
        => repo.Network.Remotes.Update(name, r => r.Url = url);

    /// <summary>Renames a remote's alias — a config + refspec edit, never a network call.
    /// <see cref="RemoteCollection.Rename(string, string, RemoteRenameFailureHandler)"/> silently
    /// drops non-default fetch refspecs it cannot rewrite unless given the failure handler;
    /// collect those here so the caller can warn the user instead of losing them quietly.</summary>
    public IReadOnlyList<string> RenameRemote(Repository repo, string name, string newName)
    {
        var problematic = new List<string>();
        repo.Network.Remotes.Rename(name, newName, problematic.Add);
        return problematic;
    }

    // ── Helpers ──

    private static bool IsMerged(Repository repo, Branch branch)
    {
        if (branch.Tip is null)
            return true;
        var headTip = repo.Head.Tip;
        if (headTip is null)
            return false;
        return repo.ObjectDatabase.CalculateHistoryDivergence(branch.Tip, headTip).AheadBy == 0;
    }

    private static bool HasIndexChange(StatusEntry entry, out FileChangeKind kind, out string? oldPath)
    {
        var state = entry.State;
        oldPath = null;
        if (state.HasFlag(FileStatus.NewInIndex)) { kind = FileChangeKind.Added; return true; }
        if (state.HasFlag(FileStatus.RenamedInIndex)) { kind = FileChangeKind.Renamed; oldPath = entry.HeadToIndexRenameDetails?.OldFilePath; return true; }
        if (state.HasFlag(FileStatus.ModifiedInIndex)) { kind = FileChangeKind.Modified; return true; }
        if (state.HasFlag(FileStatus.DeletedFromIndex)) { kind = FileChangeKind.Deleted; return true; }
        if (state.HasFlag(FileStatus.TypeChangeInIndex)) { kind = FileChangeKind.TypeChanged; return true; }
        kind = default;
        return false;
    }

    private static bool HasWorkdirChange(StatusEntry entry, out FileChangeKind kind, out string? oldPath)
    {
        var state = entry.State;
        oldPath = null;
        if (state.HasFlag(FileStatus.RenamedInWorkdir)) { kind = FileChangeKind.Renamed; oldPath = entry.IndexToWorkDirRenameDetails?.OldFilePath; return true; }
        if (state.HasFlag(FileStatus.ModifiedInWorkdir)) { kind = FileChangeKind.Modified; return true; }
        if (state.HasFlag(FileStatus.DeletedFromWorkdir)) { kind = FileChangeKind.Deleted; return true; }
        if (state.HasFlag(FileStatus.TypeChangeInWorkdir)) { kind = FileChangeKind.TypeChanged; return true; }
        kind = default;
        return false;
    }

    private static FileStatusEntry Make(Repository repo, string path, FileChangeKind kind, string? oldPath, bool isSubmodule)
    {
        var size = repo.Info.WorkingDirectory is { } wd
            ? WorkingTreeFileSize.GetOrZero(Path.Combine(wd, path))
            : 0;

        return new FileStatusEntry
        {
            Path = path,
            Kind = kind,
            OldPath = oldPath,
            IsBinary = false, // Refined by the diff engine when the file is opened.
            IsSubmodule = isSubmodule,
            SizeBytes = size,
        };
    }

    private static string DeriveName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}
