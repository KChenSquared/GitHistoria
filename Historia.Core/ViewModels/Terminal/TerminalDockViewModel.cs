namespace Historia.ViewModels;

/// <summary>
/// The bottom terminal dock: the tab strip over the live sessions, which one is active, whether
/// the dock is open and how tall it is. The dock follows the focused repository (showing that
/// repository's terminal, and hiding itself while the focused repository has none), hides itself
/// when the last session goes, and re-opens when an entry point asks for it. The view hosts the
/// terminal control and the splitter and reports the height of the column the dock sits in through
/// <see cref="HostHeightProvider"/>.
/// </summary>
public sealed partial class TerminalDockViewModel : ViewModelBase
{
    /// <summary>The shortest the dock is dragged or restored to.</summary>
    public const double MinDockHeight = 120;

    /// <summary>The part of the centre column that always stays above the dock.</summary>
    public const double MinCentreReserve = 80;

    // On its first reveal this run the dock takes this share of the centre column, regardless of
    // the persisted height; the user's drags then take over.
    private const double FirstOpenFraction = 0.35;

    private static readonly TimeSpan PersistDelay = TimeSpan.FromMilliseconds(250);

    private readonly ITerminalSessionService _sessions;
    private readonly IWorkspaceService _workspace;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;
    private readonly IUiTimer _persistTimer;
    private readonly string _homeDirectory;

    // The dock was hidden automatically because the focused repo has no terminal, as opposed to
    // the user hiding it with the close button. Only an auto-hidden dock re-shows itself when
    // focus lands on a repo that does have one.
    private bool _autoHidden;
    private bool _firstOpenSized;
    private double _dockHeight;

    public TerminalDockViewModel(
        IUiDispatcher ui,
        ITerminalSessionService sessions,
        IWorkspaceService workspace,
        ISettingsService settings,
        IKeyboardCommandRouter keyboard,
        string? homeDirectory = null)
    {
        _ui = ui;
        _sessions = sessions;
        _workspace = workspace;
        _settings = settings;
        _homeDirectory = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _dockHeight = Math.Max(MinDockHeight, settings.Current.TerminalDockHeight);
        Tabs = [];

        _persistTimer = Track(ui.CreateTimer(PersistDelay, OnPersistTick));

        Subscribe(() => _sessions.SessionsChanged += OnSessionsChanged, () => _sessions.SessionsChanged -= OnSessionsChanged);
        Subscribe(() => _sessions.FocusedSessionChanged += OnFocusedSessionChanged, () => _sessions.FocusedSessionChanged -= OnFocusedSessionChanged);
        Subscribe(() => _sessions.SessionExited += OnSessionExited, () => _sessions.SessionExited -= OnSessionExited);
        Subscribe(() => _sessions.ActivationRequested += OnActivationRequested, () => _sessions.ActivationRequested -= OnActivationRequested);
        Subscribe(() => _workspace.FocusedRepositoryChanged += OnFocusedRepositoryChanged, () => _workspace.FocusedRepositoryChanged -= OnFocusedRepositoryChanged);

        Track(keyboard.Register("Ctrl+OemTilde", () =>
        {
            _ui.Post(() =>
            {
                if (!IsDisposed)
                    ToggleFromKeyboard();
            });
            return Task.CompletedTask;
        }));

        RebuildTabs();
    }

    /// <summary>One tab per live session, replaced wholesale whenever the sessions, the focus or an exit changes.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TerminalTabViewModel> Tabs { get; private set; }

    /// <summary>The session the terminal shows: the service's focused session.</summary>
    [ObservableProperty]
    public partial TerminalSession? ActiveSession { get; private set; }

    /// <summary>True while the dock is shown.</summary>
    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    /// <summary>The height the dock currently takes; zero while it is closed.</summary>
    [ObservableProperty]
    public partial double Height { get; private set; }

    /// <summary>Reports the height of the column the dock sits in (the graph area plus the dock),
    /// which bounds how tall the dock may grow. Zero or less means the height is not known yet.</summary>
    public Func<double> HostHeightProvider { get; set; } = static () => 0;

    /// <summary>Raised when the terminal should take keyboard focus. Some requests are queued
    /// through the dispatcher so the focus lands after the dock has become visible.</summary>
    public event EventHandler? FocusTerminalRequested;

    /// <summary>True when an open dock has no session left to show.</summary>
    public static bool ShouldAutoHide(bool open, int sessionCount) => open && sessionCount == 0;

    // ── open / close ────────────────────────────────────────────────────────────────────────

    /// <summary>The terminal shortcut: closes an open dock; otherwise opens it, starting the
    /// focused repository's terminal (or a Home terminal) when there is no session yet.</summary>
    public void ToggleFromKeyboard()
    {
        if (IsOpen)
        {
            SetOpen(false);
            return;
        }

        if (_sessions.Sessions.Count == 0)
        {
            if (_workspace.Focused is { } focused)
                RunLogged("open the repository terminal", () => _sessions.OpenOrFocusAsync(RepoTerminalOrigin.For(focused)));
            else
                RunLogged("open a Home terminal", () => _sessions.OpenNewAsync(TerminalOrigin.Home(_homeDirectory)));
            return;
        }

        SetOpen(true);
        RaiseFocusTerminal();
    }

    // byUser:false is for the automatic show/hide as repo focus moves: hiding that way must
    // not clear the auto-hidden state, so the dock comes back when focus returns to a repo
    // with a terminal.
    internal void SetOpen(bool open, bool byUser = true)
    {
        IsOpen = open;
        if (byUser || open)
            _autoHidden = false;

        if (open && !_firstOpenSized)
        {
            var centre = HostHeightProvider();
            if (centre > 0)
                _dockHeight = centre * FirstOpenFraction;
            _firstOpenSized = true;
        }

        Height = open ? ClampedHeight(_dockHeight) : 0;
        if (open)
            _ui.Post(RaiseFocusTerminal);
    }

    [RelayCommand]
    private void CloseDock() => SetOpen(false);

    private void RaiseFocusTerminal()
    {
        if (!IsDisposed)
            FocusTerminalRequested?.Invoke(this, EventArgs.Empty);
    }

    // ── height ──────────────────────────────────────────────────────────────────────────────

    private double ClampedHeight(double height)
    {
        var host = HostHeightProvider();
        if (host <= 0)
            return Math.Max(MinDockHeight, height);
        return Math.Clamp(height, MinDockHeight, Math.Max(MinDockHeight, host - MinCentreReserve));
    }

    /// <summary>Resizes the open dock by a splitter drag; a negative <paramref name="deltaY"/> (drag up) makes it taller.</summary>
    public void DragBy(double deltaY)
    {
        if (!IsOpen)
            return;
        _dockHeight = ClampedHeight(_dockHeight - deltaY);
        Height = _dockHeight;
    }

    /// <summary>Ends a splitter drag: the height is saved once the drag has been still for a moment.</summary>
    public void CompleteDrag()
    {
        _persistTimer.Stop();
        _persistTimer.Start();
    }

    private void OnPersistTick()
    {
        _persistTimer.Stop();
        if (IsDisposed)
            return;
        var height = _dockHeight;
        RunLogged("save the terminal dock height", () => _settings.UpdateAsync(s => s with { TerminalDockHeight = height }));
    }

    // ── session events ──────────────────────────────────────────────────────────────────────

    // The service raises these on the UI thread already; the work is still queued so it runs
    // after whatever raised the event has finished.
    private void OnSessionsChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (IsDisposed)
                return;
            RebuildTabs();
            if (ShouldAutoHide(IsOpen, _sessions.Sessions.Count))
                SetOpen(false);
        });

    // Closing the dock only hides it; an entry point reopens it.
    private void OnActivationRequested(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (IsDisposed)
                return;
            SetOpen(true);
            RaiseFocusTerminal();
        });

    private void OnFocusedSessionChanged(object? sender, EventArgs e)
        => _ui.Post(() =>
        {
            if (!IsDisposed)
                RebuildTabs();
        });

    private void OnSessionExited(object? sender, TerminalSession session)
        => _ui.Post(() =>
        {
            if (!IsDisposed)
                RebuildTabs();
        });

    // ── follow the focused repository ───────────────────────────────────────────────────────

    private void OnFocusedRepositoryChanged(object? sender, FocusedRepositoryChangedEventArgs e)
        => _ui.Post(() =>
        {
            if (IsDisposed)
                return;
            try
            {
                var match = e.New is { } repo ? FindRepoSession(repo) : null;
                if (match is not null)
                {
                    // The new repo has a terminal: make it the active tab, and bring the dock
                    // back if it was auto-hidden for a previous terminal-less repo.
                    _sessions.Focus(match);
                    if (_autoHidden)
                        SetOpen(true, byUser: false);
                }
                else if (IsOpen && _sessions.Focused?.Origin.Kind == TerminalOriginKind.Repository)
                {
                    // The dock is showing some other repo's terminal and the newly focused repo
                    // has none: hide the dock (all sessions keep running) until focus returns
                    // to a repo that does. A Home terminal is left visible.
                    SetOpen(false, byUser: false);
                    _autoHidden = true;
                }
            }
            catch (Exception ex)
            {
                // Following the focused repository is a convenience; it never surfaces as an error.
                AppLog.Write(AppLogLevel.Warn, "TerminalDock", "Following the focused repository failed.", ex);
            }
        });

    /// <summary>The live session that belongs to <paramref name="repo"/>'s workspace tab: by
    /// tab id, or, as a fallback, by start directory.</summary>
    internal TerminalSession? FindRepoSession(RepositoryContext repo)
    {
        var origin = RepoTerminalOrigin.For(repo);
        return _sessions.Sessions.FirstOrDefault(s =>
            (origin.TabId is { } id && s.Origin.TabId == id)
            || PathsEqual(s.Origin.WorkingDirectory, origin.WorkingDirectory));
    }

    private static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        try
        {
            return string.Equals(
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(a)),
                System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ── tabs and sessions ───────────────────────────────────────────────────────────────────

    private void RebuildTabs()
    {
        var focused = _sessions.Focused;
        ActiveSession = focused;
        Tabs =
        [
            .. _sessions.Sessions.Select(session =>
            {
                var label = session.ReportedTitle ?? session.Origin.Title;
                if (session.State is TerminalSessionState.Exited)
                    label += "  [exited]";
                return new TerminalTabViewModel(session, label, ReferenceEquals(session, focused), ActivateTab, CloseTabAsync);
            }),
        ];
    }

    private void ActivateTab(TerminalSession session)
    {
        _sessions.Focus(session);
        ActiveSession = session;
        RaiseFocusTerminal();
        RebuildTabs();
    }

    private async Task CloseTabAsync(TerminalSession session)
    {
        try
        {
            await _sessions.CloseAsync(session.Id);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "TerminalDock", "Closing a terminal failed.", ex);
        }
    }

    // "+" opens another Home terminal, exactly as the Historia-menu entry does.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task NewHomeTerminalAsync()
    {
        try
        {
            await _sessions.OpenNewAsync(TerminalOrigin.Home(_homeDirectory));
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "TerminalDock", "Opening a Home terminal failed.", ex);
        }
    }

    // Shuts every session down one at a time (not in parallel) so each shell gets a clean kill
    // in turn rather than a burst of simultaneous pipe teardowns. Closing the dock itself only
    // hides it and leaves every session running.
    [RelayCommand]
    private async Task CloseAllAsync()
    {
        try
        {
            foreach (var session in _sessions.Sessions.ToArray())
                await _sessions.CloseAsync(session.Id);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "TerminalDock", "Closing every terminal failed.", ex);
        }
    }

    /// <summary>Replaces the active session when its shell has exited: closes it and starts a
    /// new session for the same origin ("no auto-respawn" until the user asks).</summary>
    [RelayCommand]
    private async Task RestartAsync()
    {
        try
        {
            if (ActiveSession is not { State: TerminalSessionState.Exited } dead)
                return;
            var origin = dead.Origin;
            await _sessions.CloseAsync(dead.Id);
            await _sessions.OpenOrFocusAsync(origin);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "TerminalDock", "Restarting the terminal failed.", ex);
        }
    }

    private static void RunLogged(string what, Func<Task> work)
        => _ = RunLoggedAsync(what, work);

    private static async Task RunLoggedAsync(string what, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "TerminalDock", $"Could not {what}.", ex);
        }
    }
}
