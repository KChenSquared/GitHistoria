namespace Historia.Services.ExternalControl;

/// <summary>Timing knobs of <see cref="ExternalControlHost"/>; tests shorten them.</summary>
public sealed record ExternalControlHostOptions
{
    /// <summary>Overall time one request may take, including waiting for an earlier request.</summary>
    public TimeSpan RequestBudget { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>How long to wait for a freshly opened repository's graph to paint before navigating
    /// anyway; the graph can only reveal a commit once it is listening.</summary>
    public TimeSpan GraphPaintWait { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait, after proposing a commit message, for the commit box to take it
    /// before reporting that it is only being offered.</summary>
    public TimeSpan DraftSettleTime { get; init; } = TimeSpan.FromMilliseconds(400);
}

/// <summary>
/// Executes control requests from <c>historia-cmdr</c>. Everything here navigates: it opens or
/// focuses a repository tab, selects a commit, opens a diff, and — for a commit proposal — offers
/// text to the commit box. It never writes to a repository, its index or refs, or to settings, and
/// it never commits. Requests run one at a time.
/// </summary>
public sealed class ExternalControlHost
{
    private readonly IWorkspaceService _workspace;
    private readonly ISettingsService _settings;
    private readonly IGitCliService _cli;
    private readonly IUiDispatcher _ui;
    private readonly IAppLog _log;
    private readonly Action _activateWindow;
    private readonly ExternalControlHostOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ExternalControlHost(
        IWorkspaceService workspace,
        ISettingsService settings,
        IGitCliService cli,
        IUiDispatcher ui,
        IAppLog log,
        Action activateWindow,
        ExternalControlHostOptions? options = null)
    {
        _workspace = workspace;
        _settings = settings;
        _cli = cli;
        _ui = ui;
        _log = log;
        _activateWindow = activateWindow;
        _options = options ?? new ExternalControlHostOptions();
    }

    public async Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken ct)
    {
        var verb = ControlVerbs.ToWire(request.Verb);
        ControlResponse response;
        string? repoName = null;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(_options.RequestBudget);

            await _gate.WaitAsync(budget.Token).ConfigureAwait(false);
            try
            {
                (response, repoName) = await ExecuteAsync(request, budget.Token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            response = ControlResponse.Fail(ControlError.Timeout, "Historia did not finish in time.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error("control", $"{verb} failed", ex);
            response = ControlResponse.Fail(ControlError.Internal, "Historia could not handle the request.");
        }

        var outcome = response.Ok
            ? "ok"
            : JsonNamingPolicy.KebabCaseLower.ConvertName((response.Error ?? ControlError.Internal).ToString());
        _log.Info("control", repoName is null ? $"{verb} -> {outcome}" : $"{verb} {repoName} -> {outcome}");
        return response;
    }

    private async Task<(ControlResponse Response, string? RepoName)> ExecuteAsync(ControlRequest request, CancellationToken ct)
    {
        if (!_settings.Current.ExternalControlEnabled)
            return (ControlResponse.Fail(ControlError.Disabled, "External control is turned off in Settings."), null);

        switch (request.Verb)
        {
            case ControlVerb.Ping:
                return (ControlResponse.Success(), null);
            case ControlVerb.Selection:
                return (await OnUiAsync(() => Task.FromResult(ReadSelection())).ConfigureAwait(false), null);
        }

        string? message = null;
        if (request.Verb == ControlVerb.ProposeCommit
            && !ControlInputValidation.NormalizeCommitMessage(request.CommitMessage, out message, out var messageError))
        {
            return (ControlResponse.Fail(ControlError.Usage, messageError), null);
        }

        var repoPath = !string.IsNullOrWhiteSpace(request.Repo) ? request.Repo : request.Cwd;
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            return (ControlResponse.Fail(ControlError.RepoNotFound, "That folder does not exist."), null);

        RepositoryContext ctx;
        try
        {
            ctx = await OnUiAsync(() => _workspace.OpenAsync(repoPath, ct)).ConfigureAwait(false);
        }
        catch (HistoriaGitException ex)
        {
            return (ControlResponse.Fail(ControlError.RepoNotFound, ex.Message), null);
        }

        var name = ctx.DisplayName;
        var root = ctx.RepositoryPath;
        var cwd = string.IsNullOrWhiteSpace(request.Cwd) ? root : request.Cwd;

        GitObjectId? rev = null, otherRev = null;
        if (request.Verb is ControlVerb.Show or ControlVerb.Diff or ControlVerb.Compare)
        {
            if (!ControlInputValidation.TryValidateRevision(request.Rev, out var revError))
                return (ControlResponse.Fail(ControlError.RevNotFound, revError), name);
            rev = await RevisionResolver.ResolveCommitAsync(_cli, root, request.Rev!, ct).ConfigureAwait(false);
            if (rev is null)
                return (ControlResponse.Fail(ControlError.RevNotFound, $"'{request.Rev}' is not a commit in this repository."), name);
        }

        if (request.Verb == ControlVerb.Compare)
        {
            if (!ControlInputValidation.TryValidateRevision(request.OtherRev, out var headError))
                return (ControlResponse.Fail(ControlError.RevNotFound, headError), name);
            otherRev = await RevisionResolver.ResolveCommitAsync(_cli, root, request.OtherRev!, ct).ConfigureAwait(false);
            if (otherRev is null)
                return (ControlResponse.Fail(ControlError.RevNotFound, $"'{request.OtherRev}' is not a commit in this repository."), name);
        }

        string? relativePath = null;
        if (request.Verb is ControlVerb.Diff or ControlVerb.Compare or ControlVerb.Changes
            && !ControlInputValidation.TryResolveRepoRelativePath(root, cwd, request.Path, out relativePath))
        {
            return (ControlResponse.Fail(ControlError.PathInvalid, "That path is not inside the repository."), name);
        }
        if (relativePath is not null)
            relativePath = ControlInputValidation.MatchOnDiskCase(root, relativePath);

        await OnUiAsync(() => _workspace.EnsureInitialized(ctx)).ConfigureAwait(false);
        if (request.Verb != ControlVerb.Open)
            await WaitForGraphAsync(ctx, ct).ConfigureAwait(false);

        var contextLines = _settings.Current.DiffContextLines;
        var state = ctx.State;
        var result = new ControlResponse
        {
            Ok = true,
            Repo = root,
            Commit = rev?.ToString(),
            OtherCommit = otherRev?.ToString(),
            Path = relativePath,
            Line = request.Line,
        };

        switch (request.Verb)
        {
            case ControlVerb.Open:
                break;

            case ControlVerb.Show:
                state.SetOpenDiff(null);
                state.SetSelectedCommit(rev);
                state.RequestRevealCommit(rev!.Value);
                break;

            case ControlVerb.Diff:
                state.SetSelectedCommit(rev);
                state.RequestRevealCommit(rev!.Value);
                state.SetOpenDiff(new DiffRequest
                {
                    Source = DiffSource.Commit,
                    CommitId = rev,
                    Path = relativePath,
                    ParentIndex = 1,
                    ContextLines = contextLines,
                    FocusLine = request.Line,
                });
                break;

            case ControlVerb.Compare:
                state.SetSelectedCommit(otherRev);
                state.RequestRevealCommit(otherRev!.Value);
                state.SetOpenDiff(new DiffRequest
                {
                    Source = DiffSource.Comparison,
                    CommitId = rev,
                    OtherCommitId = otherRev,
                    Path = relativePath,
                    ContextLines = contextLines,
                    FocusLine = request.Line,
                });
                break;

            case ControlVerb.Changes:
                state.SetSelectedCommit(null);
                state.SetOpenDiff(new DiffRequest
                {
                    Source = request.Staged ? DiffSource.StagedChanges : DiffSource.UnstagedChanges,
                    Path = relativePath,
                    ContextLines = contextLines,
                    FocusLine = request.Line,
                });
                break;

            case ControlVerb.ProposeCommit:
                var proposed = await ProposeAsync(ctx, message!, contextLines, ct).ConfigureAwait(false);
                if (proposed is null)
                    return (ControlResponse.Fail(ControlError.NothingStaged, "Nothing is staged."), name);
                result = result with { StagedPaths = proposed.Value.StagedPaths, Draft = proposed.Value.Draft };
                break;
        }

        if (request.WantsFocus)
            _ui.Post(_activateWindow);

        return (result, name);
    }

    private async Task<(IReadOnlyList<string> StagedPaths, ControlDraftOutcome Draft)?> ProposeAsync(
        RepositoryContext ctx, string message, int contextLines, CancellationToken ct)
    {
        var state = ctx.State;

        // The watcher's debounce could miss an agent's very recent `git add`, so ask for a fresh read.
        await state.RefreshStatusAsync(ct).ConfigureAwait(false);
        var staged = state.Status.Staged.Select(f => f.Path).ToList();
        if (staged.Count == 0)
            return null;

        state.SetSelectedCommit(null);
        state.SetOpenDiff(new DiffRequest
        {
            Source = DiffSource.StagedChanges,
            Path = staged[0],
            ContextLines = contextLines,
        });
        state.ProposeCommitMessage(message);

        // The commit box takes the text when it is idle and empty; otherwise it keeps the proposal
        // pending and offers it. Give it a moment to build and decide.
        var deadline = DateTime.UtcNow + _options.DraftSettleTime;
        while (state.PendingCommitMessage is not null && DateTime.UtcNow < deadline)
            await Task.Delay(25, ct).ConfigureAwait(false);

        return (staged, state.PendingCommitMessage is null ? ControlDraftOutcome.Filled : ControlDraftOutcome.Pending);
    }

    private async Task WaitForGraphAsync(RepositoryContext ctx, CancellationToken ct)
    {
        try
        {
            await ctx.FirstGraphPaint.WaitAsync(_options.GraphPaintWait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The graph is slow or not shown (a background tab); navigate anyway.
        }
    }

    private ControlResponse ReadSelection()
    {
        var ctx = _workspace.Focused;
        if (ctx is null)
            return new ControlResponse { Ok = true, Selection = new ControlSelection() };

        var state = ctx.State;
        var diff = state.OpenDiff;
        return new ControlResponse
        {
            Ok = true,
            Selection = new ControlSelection
            {
                Repo = ctx.RepositoryPath,
                Commit = state.SelectedCommitId?.ToString(),
                Diff = diff is null
                    ? null
                    : new ControlSelectionDiff
                    {
                        Source = diff.Source switch
                        {
                            DiffSource.Commit => "commit",
                            DiffSource.Comparison => "comparison",
                            DiffSource.StagedChanges => "staged",
                            _ => "unstaged",
                        },
                        Path = diff.Path,
                        Commit = diff.CommitId?.ToString(),
                        OtherCommit = diff.OtherCommitId?.ToString(),
                    },
            },
        };
    }

    private Task OnUiAsync(Action work) => OnUiAsync(() => { work(); return Task.FromResult(true); });

    /// <summary>Runs <paramref name="work"/> on the UI thread — workspace changes raise events its
    /// controls listen to — and completes with its result without ever blocking that thread.</summary>
    private Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(async () =>
        {
            try
            {
                done.TrySetResult(await work().ConfigureAwait(true));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        return done.Task;
    }
}
