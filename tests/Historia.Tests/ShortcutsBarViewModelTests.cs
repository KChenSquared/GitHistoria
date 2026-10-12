namespace Historia.Tests;

/// <summary>
/// The shortcut bars' view model: every command follows whether a repository is focused, the
/// network buttons raise the matching request, stash-all reaches the stash service and the other
/// commands do their work against the focused repository.
/// </summary>
[Trait("tier", "git")]
public sealed class ShortcutsBarViewModelTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo = TestRepositoryBuilder.FromTemplate(
        "shortcuts-bar-seed", b => b.WriteFile("f.txt", "base\n").Stage().Commit("c1"));

    public async ValueTask DisposeAsync()
    {
        _repo.Dispose();
        await _host.DisposeAsync();
    }

    private sealed class RecordingStashService : IStashService
    {
        public List<StashSaveOptions> Saves { get; } = [];
        public bool Result { get; set; } = true;
        public Exception? Throw { get; set; }

        public Task<bool> SaveAsync(RepositoryContext context, StashSaveOptions options, CancellationToken ct = default)
        {
            Saves.Add(options);
            return Throw is null ? Task.FromResult(Result) : Task.FromException<bool>(Throw);
        }

        public Task<IReadOnlyList<StashEntry>> ListAsync(RepositoryContext context, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> StashStagedAsync(RepositoryContext context, string? message, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApplyAndDropAsync(RepositoryContext context, GitObjectId commitId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ApplyAsync(RepositoryContext context, GitObjectId commitId, bool restoreIndex, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DropAsync(RepositoryContext context, GitObjectId commitId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearAsync(RepositoryContext context, CancellationToken ct = default) => throw new NotSupportedException();
        public Task BranchFromAsync(RepositoryContext context, GitObjectId commitId, string branchName, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingTerminalService : ITerminalSessionService
    {
        public List<TerminalOrigin> Opened { get; } = [];
        public IReadOnlyList<TerminalSession> Sessions => [];
        public TerminalSession? Focused => null;

        public Task<TerminalSession> OpenOrFocusAsync(TerminalOrigin origin, CancellationToken ct = default)
        {
            Opened.Add(origin);
            return Task.FromResult<TerminalSession>(null!);
        }

        public Task<TerminalSession> OpenNewAsync(TerminalOrigin origin, CancellationToken ct = default) => throw new NotSupportedException();
        public void RequestActivation() { }
        public void Focus(TerminalSession session) { }
        public Task CloseAsync(Guid sessionId) => throw new NotSupportedException();
        public Task CloseForTabAsync(string tabId) => Task.CompletedTask;
        public event EventHandler? SessionsChanged { add { } remove { } }
        public event EventHandler? FocusedSessionChanged { add { } remove { } }
        public event EventHandler<TerminalSession>? SessionExited { add { } remove { } }
        public event EventHandler? ActivationRequested { add { } remove { } }
        public event EventHandler<TerminalClipboardWriteEventArgs>? ClipboardWriteRequested { add { } remove { } }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(ShortcutsBarViewModelTests owner, bool focused, IUiDispatcher? ui = null)
        {
            Workspace = new FakeWorkspace();
            Settings = new FakeSettingsService();
            Notifications = new FakeNotificationService();
            Dialogs = new FakeDialogService();
            Stash = new RecordingStashService();
            Terminals = new RecordingTerminalService();
            Actions = new RepoActionBus();
            Context = owner._host.Open(owner._repo.Path);
            if (focused)
                Workspace.SetFocused(Context);
            ViewModel = new ShortcutsBarViewModel(
                ui ?? new ImmediateUiDispatcher(), Workspace, Actions, Settings, Notifications, Stash, Dialogs, Terminals);
        }

        public FakeWorkspace Workspace { get; }
        public FakeSettingsService Settings { get; }
        public FakeNotificationService Notifications { get; }
        public FakeDialogService Dialogs { get; }
        public RecordingStashService Stash { get; }
        public RecordingTerminalService Terminals { get; }
        public RepoActionBus Actions { get; }
        public RepositoryContext Context { get; }
        public ShortcutsBarViewModel ViewModel { get; }

        public async ValueTask DisposeAsync()
        {
            ViewModel.Dispose();
            await Context.DisposeAsync();
        }
    }

    private static IEnumerable<System.Windows.Input.ICommand> AllCommands(ShortcutsBarViewModel vm) =>
        [vm.CreateBranchCommand, vm.OpenTerminalCommand, vm.FetchCommand, vm.PullCommand, vm.PushCommand, vm.StashAllCommand];

    [Fact]
    public async Task Every_command_is_disabled_without_a_focused_repository()
    {
        await using var rig = new Rig(this, focused: false);

        Assert.False(rig.ViewModel.HasRepo);
        Assert.All(AllCommands(rig.ViewModel), c => Assert.False(c.CanExecute(null)));
    }

    [Fact]
    public async Task Every_command_is_enabled_with_a_focused_repository()
    {
        await using var rig = new Rig(this, focused: true);

        Assert.True(rig.ViewModel.HasRepo);
        Assert.All(AllCommands(rig.ViewModel), c => Assert.True(c.CanExecute(null)));
    }

    [Fact]
    public async Task Commands_follow_focus_changes_and_raise_CanExecuteChanged()
    {
        await using var rig = new Rig(this, focused: false);
        var changed = 0;
        rig.ViewModel.FetchCommand.CanExecuteChanged += (_, _) => changed++;

        rig.Workspace.SetFocused(rig.Context);
        Assert.True(rig.ViewModel.FetchCommand.CanExecute(null));
        Assert.Equal(1, changed);

        rig.Workspace.SetFocused(null);
        Assert.False(rig.ViewModel.FetchCommand.CanExecute(null));
        Assert.Equal(2, changed);
    }

    [Fact]
    public async Task Focus_change_is_applied_through_the_ui_dispatcher()
    {
        var ui = new RecordingUiDispatcher();
        await using var rig = new Rig(this, focused: false, ui);

        rig.Workspace.SetFocused(rig.Context);
        Assert.False(rig.ViewModel.HasRepo);

        ui.DrainPosted();
        Assert.True(rig.ViewModel.HasRepo);
    }

    [Fact]
    public async Task Focus_change_after_dispose_is_ignored()
    {
        var ui = new RecordingUiDispatcher();
        await using var rig = new Rig(this, focused: false, ui);

        rig.ViewModel.Dispose();
        rig.Workspace.SetFocused(rig.Context);
        ui.DrainPosted();

        Assert.False(rig.ViewModel.HasRepo);
    }

    [Fact]
    public async Task Network_commands_request_the_matching_action()
    {
        await using var rig = new Rig(this, focused: true);
        var requested = new List<RepoNetworkAction>();
        rig.Actions.NetworkActionRequested += (_, r) => requested.Add(r.Action);

        rig.ViewModel.FetchCommand.Execute(null);
        rig.ViewModel.PullCommand.Execute(null);
        rig.ViewModel.PushCommand.Execute(null);

        Assert.Equal([RepoNetworkAction.Fetch, RepoNetworkAction.Pull, RepoNetworkAction.Push], requested);
    }

    [Fact]
    public async Task Stash_all_saves_a_stash_named_after_the_branch_including_untracked_files()
    {
        await using var rig = new Rig(this, focused: true);

        await rig.ViewModel.StashAllCommand.ExecuteAsync(null);

        var save = Assert.Single(rig.Stash.Saves);
        Assert.StartsWith("Stash of ", save.Message);
        Assert.True(save.IncludeUntracked);
        Assert.Equal(NotificationSeverity.Success, Assert.Single(rig.Notifications.Published).Severity);
    }

    [Fact]
    public async Task Stash_all_with_custom_names_asks_for_the_message_and_stops_when_cancelled()
    {
        await using var rig = new Rig(this, focused: true);
        await rig.Settings.UpdateAsync(s => s with { UseCustomStashNames = true });
        rig.Dialogs.EnqueuePrompt(null);

        await rig.ViewModel.StashAllCommand.ExecuteAsync(null);

        Assert.Empty(rig.Stash.Saves);
        Assert.Contains(rig.Dialogs.Calls, c => c.Method == nameof(IDialogService.PromptAsync));
    }

    [Fact]
    public async Task Stash_all_reports_a_failed_stash_instead_of_throwing()
    {
        await using var rig = new Rig(this, focused: true);
        rig.Stash.Throw = new InvalidOperationException("boom");

        await rig.ViewModel.StashAllCommand.ExecuteAsync(null);

        Assert.Equal(NotificationSeverity.Error, Assert.Single(rig.Notifications.Published).Severity);
    }

    [Fact]
    public async Task Stash_all_is_disabled_while_it_runs()
    {
        await using var rig = new Rig(this, focused: true);
        await rig.Settings.UpdateAsync(s => s with { UseCustomStashNames = true });
        var release = new TaskCompletionSource<string?>();
        var dialogs = new GatedPromptDialogService(release.Task);
        var vm = new ShortcutsBarViewModel(new ImmediateUiDispatcher(), rig.Workspace, rig.Actions, rig.Settings, rig.Notifications, rig.Stash, dialogs, rig.Terminals);

        var running = vm.StashAllCommand.ExecuteAsync(null);
        Assert.False(vm.StashAllCommand.CanExecute(null));

        release.SetResult("named");
        await running;

        Assert.True(vm.StashAllCommand.CanExecute(null));
        Assert.Equal("named", Assert.Single(rig.Stash.Saves).Message);
        vm.Dispose();
    }

    private sealed class GatedPromptDialogService(Task<string?> answer) : IDialogService
    {
        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null, string confirmLabel = "OK", bool primaryConfirm = false) => answer;
        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => throw new NotSupportedException();
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => throw new NotSupportedException();
        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false, string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None) => throw new NotSupportedException();
        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel) => throw new NotSupportedException();
        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None) => throw new NotSupportedException();
        public Task ShowMessageAsync(string title, string message) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Open_terminal_opens_or_focuses_the_focused_repositorys_terminal()
    {
        await using var rig = new Rig(this, focused: true);

        await rig.ViewModel.OpenTerminalCommand.ExecuteAsync(null);

        var origin = Assert.Single(rig.Terminals.Opened);
        Assert.Equal(TerminalOriginKind.Repository, origin.Kind);
        Assert.Equal(rig.Context.Id, origin.TabId);
    }

    [Fact]
    public async Task Create_branch_makes_a_branch_at_the_head_and_reports_it()
    {
        await using var rig = new Rig(this, focused: true);
        rig.Dialogs.EnqueuePrompt("  feature/x  ");

        await rig.ViewModel.CreateBranchCommand.ExecuteAsync(null);

        Assert.Contains("feature/x", _repo.Git("branch", "--list", "feature/x"));
        var note = Assert.Single(rig.Notifications.Published);
        Assert.Equal(NotificationSeverity.Success, note.Severity);
        Assert.StartsWith("Created feature/x", note.Message);
    }

    [Fact]
    public async Task Create_branch_does_nothing_when_the_prompt_is_cancelled()
    {
        await using var rig = new Rig(this, focused: true);
        rig.Dialogs.EnqueuePrompt(null);

        await rig.ViewModel.CreateBranchCommand.ExecuteAsync(null);

        Assert.Empty(rig.Notifications.Published);
        Assert.DoesNotContain("feature", _repo.Git("branch", "--list"));
    }

    [Fact]
    public async Task Create_branch_reports_a_git_failure_as_an_error_notice()
    {
        await using var rig = new Rig(this, focused: true);
        rig.Dialogs.EnqueuePrompt("bad name with spaces");

        await rig.ViewModel.CreateBranchCommand.ExecuteAsync(null);

        Assert.Equal(NotificationSeverity.Error, Assert.Single(rig.Notifications.Published).Severity);
    }

    [Fact]
    public async Task Commands_without_their_services_do_nothing()
    {
        await using var rig = new Rig(this, focused: true);
        var bare = new ShortcutsBarViewModel(new ImmediateUiDispatcher(), rig.Workspace);

        bare.FetchCommand.Execute(null);
        await bare.CreateBranchCommand.ExecuteAsync(null);
        await bare.OpenTerminalCommand.ExecuteAsync(null);
        await bare.StashAllCommand.ExecuteAsync(null);

        Assert.True(bare.HasRepo);
        bare.Dispose();
    }

    [Fact]
    public async Task Dispose_unsubscribes_from_the_workspace()
    {
        await using var rig = new Rig(this, focused: false);

        rig.ViewModel.Dispose();
        rig.Workspace.SetFocused(rig.Context);

        Assert.False(rig.ViewModel.HasRepo);
    }
}
