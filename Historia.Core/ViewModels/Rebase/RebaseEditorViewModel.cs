namespace Historia.ViewModels;

/// <summary>
/// The interactive-rebase todo editor for one upstream ref. <see cref="LoadCommand"/> builds the
/// session for the focused repository and fills <see cref="Items"/> (oldest first); the rows can be
/// reordered and given an action, and <see cref="StartCommand"/> runs the rebase. Anything that
/// rewrites history asks for confirmation first; the one exception is every commit left as a pick
/// in its original order.
/// </summary>
public sealed partial class RebaseEditorViewModel : ViewModelBase
{
    private readonly IRebaseService _rebase;
    private readonly INotificationService _notifications;
    private readonly INavigationBus _nav;
    private readonly IWorkspaceService _workspace;
    private readonly IDialogService _dialogs;
    private readonly string _onto;

    private RebaseSession? _session;

    public RebaseEditorViewModel(
        IRebaseService rebase,
        INotificationService notifications,
        INavigationBus nav,
        IWorkspaceService workspace,
        IDialogService dialogs,
        string onto)
    {
        _rebase = rebase;
        _notifications = notifications;
        _nav = nav;
        _workspace = workspace;
        _dialogs = dialogs;
        _onto = onto;
        Items = [];
        Autostash = true;
    }

    /// <summary>The todo rows in rebase order. Reordering moves rows in place; a load replaces the collection.</summary>
    [ObservableProperty]
    public partial ObservableCollection<RebaseTodoItemViewModel> Items { get; private set; }

    /// <summary>Stash local changes before the rebase and restore them after.</summary>
    [ObservableProperty]
    public partial bool Autostash { get; set; }

    /// <summary>True once a session is built and the list can be edited.</summary>
    [ObservableProperty]
    public partial bool IsLoaded { get; private set; }

    /// <summary>Why no session could be built, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>The branch, the target and the commit count, once loaded.</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    public bool HasError => ErrorMessage is not null;

    [RelayCommand]
    private async Task LoadAsync()
    {
        ErrorMessage = null;
        IsLoaded = false;

        var repo = _workspace.Focused;
        if (repo is null || string.IsNullOrWhiteSpace(_onto))
        {
            ErrorMessage = "No repository open, or no upstream ref was specified.";
            return;
        }

        RebaseSession session;
        try
        {
            session = await _rebase.BuildSessionAsync(repo, _onto, repo.Lifetime);
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                ErrorMessage = $"Could not build the rebase session: {ex.Message}";
            return;
        }

        if (IsDisposed)
            return;

        _session = session;
        Items = new ObservableCollection<RebaseTodoItemViewModel>(session.Entries.Select(e => new RebaseTodoItemViewModel(e, Move)));
        UpdateMoveFlags();
        Summary = $"{session.SourceBranchName} onto {session.OntoRefName} — {session.Entries.Count} commit(s), oldest first. Squash/fixup melds into the row above.";
        IsLoaded = true;
    }

    [RelayCommand]
    private void Cancel() => _nav.RequestNavigate("repo");

    [RelayCommand]
    private async Task StartAsync()
    {
        if (_session is null)
            return;
        var repo = _workspace.Focused;
        if (repo is null)
            return;

        try
        {
            var entries = Items.Select(i => i.Entry).ToList();
            var session = _session with { Entries = entries };
            var issues = _rebase.Validate(session);
            if (issues.Any(i => !i.RequiresConfirmation))
            {
                _notifications.Publish(NotificationSeverity.Error, string.Join("\n", issues.Select(i => i.Message)));
                return;
            }

            // The pick-only, unchanged-order fast path doesn't rewrite anything;
            // everything else (a reorder, a reword/edit, a squash/fixup, a drop) gives commits new
            // SHAs and needs the destructive confirm, naming what changes.
            if (!IsPickOnlyUnchangedOrder(session))
            {
                var rewritten = entries.Count(e => e.Action is RebaseAction.Reword or RebaseAction.Edit);
                var dropped = entries.Count(e => e.Action == RebaseAction.Drop);
                var squashed = entries.Count(e => e.Action is RebaseAction.Squash or RebaseAction.Fixup);

                var parts = new List<string>();
                if (rewritten > 0) parts.Add($"{rewritten} rewritten");
                if (dropped > 0) parts.Add($"{dropped} dropped");
                if (squashed > 0) parts.Add($"{squashed} squashed");
                var summary = parts.Count > 0
                    ? $"{string.Join(", ", parts)} commit(s)."
                    : "Reordering commits still gives them new SHAs.";

                var effect = $"Rebase {session.SourceBranchName} onto {session.OntoRefName}? This rewrites history: {summary}";
                if (issues.Count > 0)
                    effect += "\n\n" + string.Join("\n", issues.Select(i => i.Message));

                if (!await _dialogs.ConfirmDestructiveAsync("Rewrite history", effect, "Start rebase"))
                    return;
            }

            await _rebase.StartAsync(session, repo, Autostash, repo.Lifetime);
            await repo.State.RefreshAllAsync(repo.Lifetime);
            var conflicts = repo.State.Status.Conflicted.Count;
            _notifications.Publish(conflicts > 0 ? NotificationSeverity.Warning : NotificationSeverity.Success,
                conflicts > 0 ? $"Rebase paused on {conflicts} conflict(s)." : "Rebase complete.");
            _nav.RequestNavigate("repo");
        }
        catch (Exception ex)
        {
            _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("Rebase"), ex.NotificationDetails());
        }
    }

    /// <summary>The only rebase that doesn't rewrite anything is every entry left as Pick,
    /// in the exact order the session was built in — <see cref="RebaseService.StartAsync"/>'s
    /// non-interactive fast path. A reorder of pick-only entries still gets new commit SHAs even
    /// though the fast path's own check (all-Pick) would take it, so this checks order too.</summary>
    private bool IsPickOnlyUnchangedOrder(RebaseSession session)
        => _session is not null
           && session.Entries.Count == _session.Entries.Count
           && session.Entries.Select((e, i) => e.Action == RebaseAction.Pick && e.CommitId == _session.Entries[i].CommitId)
               .All(same => same);

    private void Move(RebaseTodoItemViewModel row, int offset)
    {
        var from = Items.IndexOf(row);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= Items.Count)
            return;

        Items.Move(from, to);
        UpdateMoveFlags();
    }

    private void UpdateMoveFlags()
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].CanMoveUp = i > 0;
            Items[i].CanMoveDown = i < Items.Count - 1;
        }
    }
}
