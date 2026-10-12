namespace Historia.ViewModels;

/// <summary>
/// The commands behind the two shortcut bars above the side panes: create a branch, open the
/// repository's terminal, fetch, pull, push and stash all. One view model serves both bars; each
/// bar binds only the commands it shows. Every command is enabled while a repository is focused
/// (<see cref="HasRepo"/>), and the stash command is also disabled while a stash is running.
/// A service the host did not register leaves the commands that need it as no-ops.
/// </summary>
public sealed partial class ShortcutsBarViewModel : ViewModelBase
{
    private readonly IWorkspaceService? _workspace;
    private readonly IRepoActionBus? _actions;
    private readonly ISettingsService? _settings;
    private readonly INotificationService? _notifications;
    private readonly IStashService? _stash;
    private readonly IDialogService? _dialogs;
    private readonly ITerminalSessionService? _terminals;
    private readonly IUiDispatcher _ui;

    public ShortcutsBarViewModel(
        IUiDispatcher ui,
        IWorkspaceService? workspace = null,
        IRepoActionBus? actions = null,
        ISettingsService? settings = null,
        INotificationService? notifications = null,
        IStashService? stash = null,
        IDialogService? dialogs = null,
        ITerminalSessionService? terminals = null)
    {
        _ui = ui;
        _workspace = workspace;
        _actions = actions;
        _settings = settings;
        _notifications = notifications;
        _stash = stash;
        _dialogs = dialogs;
        _terminals = terminals;

        HasRepo = _workspace?.Focused is not null;
        if (_workspace is not null)
            Subscribe(() => _workspace.FocusedRepositoryChanged += OnFocusChanged, () => _workspace.FocusedRepositoryChanged -= OnFocusChanged);
    }

    /// <summary>True while a repository tab is focused; the shortcuts act on it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateBranchCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenTerminalCommand))]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullCommand))]
    [NotifyCanExecuteChangedFor(nameof(PushCommand))]
    [NotifyCanExecuteChangedFor(nameof(StashAllCommand))]
    public partial bool HasRepo { get; private set; }

    // The workspace raises on the thread that changed focus; the state is bound, so it is read on
    // the UI thread.
    private void OnFocusChanged(object? sender, FocusedRepositoryChangedEventArgs e)
        => _ui.Post(() =>
        {
            if (IsDisposed)
                return;
            HasRepo = _workspace?.Focused is not null;
        });

    [RelayCommand(CanExecute = nameof(HasRepo))]
    private async Task CreateBranchAsync()
    {
        try
        {
            if (_workspace?.Focused is not { } ctx || _dialogs is null)
                return;
            var name = await _dialogs.PromptAsync("New branch", "Branch name:");
            if (string.IsNullOrWhiteSpace(name))
                return;
            name = name.Trim();

            var selected = ctx.State.SelectedCommitId;
            var startPoint = selected?.ToString() ?? ctx.State.CurrentBranch?.CanonicalName ?? "HEAD";
            try
            {
                await ctx.Gateway.CreateBranchAsync(name, startPoint, checkout: false, ctx.Lifetime);
                await ctx.State.RefreshAllAsync(ctx.Lifetime);
                _notifications?.Publish(NotificationSeverity.Success,
                    selected is { } id ? $"Created {name} at {id.ToShortString(7)}." : $"Created {name}.");
            }
            catch (Exception ex)
            {
                _notifications?.Publish(NotificationSeverity.Error,
                    $"Could not create branch: {ex.Message}", ex.NotificationDetails());
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "ShortcutsBar", "Creating a branch from the shortcuts bar failed.", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(HasRepo))]
    private async Task OpenTerminalAsync()
    {
        try
        {
            if (_workspace?.Focused is not { } ctx || _terminals is null)
                return;
            await _terminals.OpenOrFocusAsync(RepoTerminalOrigin.For(ctx));
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "ShortcutsBar", "Opening the repository terminal from the shortcuts bar failed.", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(HasRepo))]
    private void Fetch() => _actions?.RequestNetworkAction(RepoNetworkAction.Fetch);

    [RelayCommand(CanExecute = nameof(HasRepo))]
    private void Pull() => _actions?.RequestNetworkAction(RepoNetworkAction.Pull);

    [RelayCommand(CanExecute = nameof(HasRepo))]
    private void Push() => _actions?.RequestNetworkAction(RepoNetworkAction.Push);

    // The async command disables itself while it runs, so a second click cannot start a second stash.
    [RelayCommand(CanExecute = nameof(HasRepo))]
    private async Task StashAllAsync()
    {
        try
        {
            if (_workspace?.Focused is not { } ctx || _stash is null || _settings is null || _dialogs is null || _notifications is null)
                return;
            await StashActions.StashAllAsync(ctx, _stash, _settings, _dialogs, _notifications);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "ShortcutsBar", "Stashing from the shortcuts bar failed.", ex);
        }
    }
}
