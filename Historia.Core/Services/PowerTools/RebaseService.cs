namespace Historia.Services.PowerTools;

/// <inheritdoc cref="IRebaseService"/>
public sealed class RebaseService(IGitCliService cli) : IRebaseService
{
    /// <summary>
    /// Test hook: overrides how the <c>GIT_SEQUENCE_EDITOR</c> / <c>GIT_EDITOR</c> command is
    /// built. Signature is (mode, tempDir) → shell command. Defaults to re-invoking the Historia
    /// executable with the given switch.
    /// </summary>
    public static Func<string, string, string>? EditorCommandOverride;

    private static string BuildEditorCommand(string mode, string dir)
    {
        if (EditorCommandOverride is { } f)
            return f(mode, Fwd(dir));
        // Git runs the editor command through its bundled sh; Windows backslash paths get
        // mangled there, so hand it forward slashes.
        return $"\"{Fwd(Environment.ProcessPath ?? "Historia")}\" {mode} \"{Fwd(dir)}\"";
    }

    private static string Fwd(string p) => p.Replace('\\', '/');

    public Task<RebaseSession> BuildSessionAsync(RepositoryContext context, string upstreamRef, CancellationToken ct = default)
        => context.WithRepositoryAsync(repo => Build(repo, upstreamRef), ct);

    public IReadOnlyList<RebaseValidationIssue> Validate(RebaseSession session)
    {
        var issues = new List<RebaseValidationIssue>();
        var entries = session.Entries;

        if (entries.Count > 0 && entries[0].Action is RebaseAction.Squash or RebaseAction.Fixup)
            issues.Add(new("The first commit cannot be squashed or fixed up — there is no preceding commit.", false));

        if (entries.All(e => e.Action == RebaseAction.Drop))
            issues.Add(new("Every commit is set to Drop. This is allowed but rarely intended.", true));

        foreach (var e in entries.Where(e => e.Action == RebaseAction.Reword))
            if (string.IsNullOrWhiteSpace(e.RewordMessage))
                issues.Add(new($"Reword of {e.CommitId.Sha[..7]} has an empty message — this would abort the rebase.", false));

        return issues;
    }

    public async Task StartAsync(RebaseSession session, RepositoryContext context, bool autostash, CancellationToken ct = default)
    {
        if (Validate(session).Any(i => !i.RequiresConfirmation))
            throw new HistoriaGitException(GitErrorKind.Unknown, "The rebase plan has unresolved validation errors.");

        using var _ = context.BeginSelfOperation();

        var unchanged = session.Entries.Count > 0
                        && session.Entries.All(e => e.Action == RebaseAction.Pick);

        var env = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
        };
        var args = new List<string> { "rebase" };
        if (autostash) args.Add("--autostash");

        string? tempDir = null;
        if (unchanged)
        {
            // Non-interactive fast path.
            args.Add("--onto");
            args.Add(session.OntoRefName);
            args.Add(session.UpstreamRefName);
            args.Add(session.SourceBranchName);
        }
        else
        {
            tempDir = Path.Combine(Path.GetTempPath(), "Historia-rebase-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            WritePlan(session, tempDir);

            // The re-invoked-self helper overwrites a file Git hands it. Gate it with a
            // per-session secret: the token is both written into tempDir and passed on the
            // child env, so an unrelated process can't drive the helper with crafted args.
            var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(tempDir, "token"), token);

            env["GIT_SEQUENCE_EDITOR"] = BuildEditorCommand("--rebase-seq", tempDir);
            env["GIT_EDITOR"] = BuildEditorCommand("--rebase-msg", tempDir);
            env["HISTORIA_REBASE_DIR"] = tempDir;
            env["HISTORIA_REBASE_TOKEN"] = token;

            args.Add("-i");
            args.Add("--onto");
            args.Add(session.OntoRefName);
            args.Add(session.UpstreamRefName);
            args.Add(session.SourceBranchName);
        }

        try
        {
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = args,
                WorkingDirectory = context.RepositoryPath,
                ExtraEnvironment = env,
                Timeout = TimeSpan.FromMinutes(10),
            }, ct).ConfigureAwait(false);

            await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);

            if (!result.IsSuccess && result.ErrorKind != GitErrorKind.MergeConflict)
                throw HistoriaGitException.FromCli(result, "Rebase");
        }
        finally
        {
            if (tempDir is not null)
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
            }
        }
    }

    // ── plan file ──

    private static void WritePlan(RebaseSession session, string dir)
    {
        var todo = new StringBuilder();
        var messages = new List<string>();

        foreach (var e in session.Entries)
        {
            var verb = e.Action switch
            {
                RebaseAction.Reword => "reword",
                RebaseAction.Edit => "edit",
                RebaseAction.Squash => "squash",
                RebaseAction.Fixup => "fixup",
                RebaseAction.Drop => "drop",
                _ => "pick",
            };

            if (e.Action == RebaseAction.Drop)
            {
                todo.Append("# drop ").Append(e.CommitId.Sha).Append(' ').Append(e.MessageShort).Append('\n');
                continue;
            }

            todo.Append(verb).Append(' ').Append(e.CommitId.Sha).Append(' ').Append(e.MessageShort).Append('\n');

            if (e.Action is RebaseAction.Reword or RebaseAction.Squash)
                messages.Add(e.RewordMessage ?? e.MessageShort);
        }

        File.WriteAllText(Path.Combine(dir, "plan"), todo.ToString());
        // One message per line-group, separated by a NUL so multi-line messages survive.
        File.WriteAllText(Path.Combine(dir, "messages"), string.Join("\0", messages));
        File.WriteAllText(Path.Combine(dir, "msg-cursor"), "0");
    }

    private static RebaseSession Build(Repository repo, string upstreamRef)
    {
        var upstream = repo.Lookup<Commit>(upstreamRef) ?? (repo.Branches[upstreamRef]?.Tip)
            ?? throw new HistoriaGitException(GitErrorKind.RefNotFound, $"'{upstreamRef}' not found.");
        var head = repo.Head;
        if (head.Tip is null)
            throw new HistoriaGitException(GitErrorKind.Unknown, "HEAD has no commits to rebase.");

        var filter = new CommitFilter
        {
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
            IncludeReachableFrom = head.Tip,
            ExcludeReachableFrom = upstream,
        };

        var entries = repo.Commits.QueryBy(filter)
            .Reverse() // oldest first
            .Select(c => new RebaseEntry
            {
                CommitId = c.Id.ToGitObjectId(),
                MessageShort = c.MessageShort,
                AuthorName = c.Author.Name,
                AuthorEmail = c.Author.Email,
            })
            .ToList();

        return new RebaseSession
        {
            OntoCommitId = upstream.Id.ToGitObjectId(),
            OntoRefName = upstreamRef,
            UpstreamRefName = upstreamRef,
            SourceBranchName = head.FriendlyName,
            Entries = entries,
        };
    }
}
