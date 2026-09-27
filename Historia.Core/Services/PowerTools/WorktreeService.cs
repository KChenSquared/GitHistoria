namespace Historia.Services.PowerTools;

/// <inheritdoc cref="IWorktreeService"/>
public sealed class WorktreeService(IGitCliService cli) : IWorktreeService
{
    public async Task<IReadOnlyList<WorktreeInfo>> ListAsync(RepositoryContext context, CancellationToken ct = default)
    {
        var result = await Run(context, ["worktree", "list", "--porcelain"], ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            return [];

        var list = new List<WorktreeInfo>();
        string? path = null, branch = null, reason = null;
        GitObjectId? headId = null;
        bool bare = false, detached = false, locked = false, prunable = false, first = true;

        void Flush()
        {
            if (path is null)
                return;
            list.Add(new WorktreeInfo
            {
                Path = path,
                Branch = detached ? "(detached)" : (branch is not null ? ShortBranch(branch) : bare ? "(bare)" : "(unknown)"),
                HeadCommitId = headId,
                IsMainWorktree = first,
                IsLocked = locked,
                LockReason = reason,
                IsPrunable = prunable,
            });
            first = false;
            path = branch = reason = null;
            headId = null;
            bare = detached = locked = prunable = false;
        }

        foreach (var raw in result.StandardOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) { Flush(); continue; }

            var sp = line.IndexOf(' ');
            var key = sp < 0 ? line : line[..sp];
            var val = sp < 0 ? "" : line[(sp + 1)..];

            switch (key)
            {
                case "worktree": path = val; break;
                case "HEAD": headId = GitObjectId.TryParse(val, out var id) ? id : null; break;
                case "branch": branch = val; break;
                case "bare": bare = true; break;
                case "detached": detached = true; break;
                case "locked": locked = true; reason = val.Length > 0 ? val : null; break;
                case "prunable": prunable = true; break;
            }
        }

        Flush();
        return list;
    }

    public async Task AddAsync(RepositoryContext context, WorktreeAddRequest request, CancellationToken ct = default)
    {
        var args = new List<string> { "worktree", "add" };
        if (request.NewBranch is { } nb)
        {
            args.Add("-b");
            args.Add(nb);
            args.Add("--");
            args.Add(request.Path);
            if (request.Branch is { } start) args.Add(start);
        }
        else if (request.Detach)
        {
            args.Add("--detach");
            args.Add("--");
            args.Add(request.Path);
            if (request.Commit is { } c) args.Add(c);
        }
        else
        {
            args.Add("--");
            args.Add(request.Path);
            if (request.Branch is { } b) args.Add(b);
        }

        await Mutate(context, args, "Add worktree", ct).ConfigureAwait(false);
    }

    public Task RemoveAsync(RepositoryContext context, string path, bool force, CancellationToken ct = default)
    {
        var args = new List<string> { "worktree", "remove" };
        if (force) args.Add("--force");
        args.Add("--");
        args.Add(path);
        return Mutate(context, args, "Remove worktree", ct);
    }

    public Task LockAsync(RepositoryContext context, string path, string reason, CancellationToken ct = default)
        => Mutate(context, ["worktree", "lock", "--reason", reason, "--", path], "Lock worktree", ct);

    public Task UnlockAsync(RepositoryContext context, string path, CancellationToken ct = default)
        => Mutate(context, ["worktree", "unlock", "--", path], "Unlock worktree", ct);

    public Task MoveAsync(RepositoryContext context, string path, string newPath, CancellationToken ct = default)
        => Mutate(context, ["worktree", "move", "--", path, newPath], "Move worktree", ct);

    public Task PruneAsync(RepositoryContext context, CancellationToken ct = default)
        => Mutate(context, ["worktree", "prune"], "Prune worktrees", ct);

    public Task RepairAsync(RepositoryContext context, CancellationToken ct = default)
        => Mutate(context, ["worktree", "repair"], "Repair worktrees", ct);

    private static string ShortBranch(string refName)
        => refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName["refs/heads/".Length..] : refName;

    private Task<GitCliResult> Run(RepositoryContext context, IReadOnlyList<string> args, CancellationToken ct)
        => cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = context.RepositoryPath,
            Timeout = TimeSpan.FromMinutes(5),
        }, ct);

    private async Task Mutate(RepositoryContext context, IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        using var _ = context.BeginSelfOperation();
        var result = await Run(context, args, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, what);
        await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);
    }
}
