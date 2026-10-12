namespace HistoriaApp.Tests;

/// <summary>
/// The rebase editor's markup against its view model: the error line when no session can be built,
/// one row per commit with its action picker and move buttons, and the Start button reaching the
/// destructive confirmation once the order changed.
/// </summary>
[Trait("tier", "ui")]
public sealed class RebaseEditorViewTests : IDisposable
{
    private readonly string _repoDir = TestScratch.NewDirectory("historia-rebase-view-");
    private readonly ServiceProvider _repoServices;
    private readonly RepositoryContext _context;

    public RebaseEditorViewTests()
    {
        RepoTemplates.Initial(_repoDir);

        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(new InMemorySettingsService());
        sc.AddSingleton<IRepoLayoutService>(new FakeLayoutService());
        sc.AddSingleton<ICredentialVaultService>(new FakeVault());
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
        sc.AddSingleton<IGitCliService, GitCliService>();
        _repoServices = sc.BuildServiceProvider();
        _context = RepositoryContext.Open(_repoDir, _repoServices);
    }

    public void Dispose()
    {
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _repoServices.Dispose(); } catch { }
        TestScratch.Delete(_repoDir);
    }

    private sealed class Harness(RebaseEditorView view, Window window, RecordingRebase rebase, RecordingDialogs dialogs, List<string> navigations) : IDisposable
    {
        public RebaseEditorView View { get; } = view;
        public RecordingRebase Rebase { get; } = rebase;
        public RecordingDialogs Dialogs { get; } = dialogs;
        public List<string> Navigations { get; } = navigations;

        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        public void Dispose()
        {
            View.Dispose();
            window.Close();
        }
    }

    private Harness Create(bool focused = true)
    {
        var rebase = new RecordingRebase();
        var dialogs = new RecordingDialogs();
        var nav = new NavigationBus();
        var navigations = new List<string>();
        nav.NavigationRequested += navigations.Add;

        var sc = new ServiceCollection();
        sc.AddSingleton<IRebaseService>(rebase);
        sc.AddSingleton<INotificationService, NotificationService>();
        sc.AddSingleton<INavigationBus>(nav);
        sc.AddSingleton<IWorkspaceService>(new FocusedWorkspace(focused ? _context : null));
        sc.AddSingleton<IDialogService>(dialogs);

        var view = new RebaseEditorView(sc.BuildServiceProvider(), "main");
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        var h = new Harness(view, window, rebase, dialogs, navigations);
        h.Pump();
        return h;
    }

    private static List<Button> Buttons(Control view, string caption) =>
        view.GetVisualDescendants().OfType<Button>().Where(b => (b.Content as string) == caption).ToList();

    private static List<TextBlock> Messages(Control view) =>
        view.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text is "first" or "second" or "third")
            .OrderBy(t => t.TranslatePoint(default, view)!.Value.Y)
            .ToList();

    [AvaloniaFact]
    public void Without_a_repository_the_reason_shows_and_the_editor_stays_hidden()
    {
        using var h = Create(focused: false);

        var error = h.View.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text == "No repository open, or no upstream ref was specified.");
        Assert.True(error.IsEffectivelyVisible);
        Assert.All(Buttons(h.View, "Start rebase"), b => Assert.False(b.IsEffectivelyVisible));
        Assert.Empty(h.View.GetVisualDescendants().OfType<ComboBox>());
    }

    [AvaloniaFact]
    public void Each_commit_gets_a_row_with_its_action_and_move_buttons()
    {
        using var h = Create();

        Assert.Equal(["first", "second", "third"], Messages(h.View).Select(t => t.Text));
        Assert.Contains(h.View.GetVisualDescendants().OfType<TextBlock>(),
            t => t.Text == "feature onto main — 3 commit(s), oldest first. Squash/fixup melds into the row above.");
        var combos = h.View.GetVisualDescendants().OfType<ComboBox>().ToList();
        Assert.Equal(3, combos.Count);
        Assert.All(combos, c => Assert.Equal(RebaseAction.Pick, c.SelectedItem));
        Assert.Equal(Enum.GetValues<RebaseAction>().Cast<object>(), combos[0].ItemsSource!.Cast<object>());

        var up = Buttons(h.View, "▲");
        var down = Buttons(h.View, "▼");
        Assert.False(up[0].IsEffectivelyEnabled);
        Assert.True(up[1].IsEffectivelyEnabled);
        Assert.True(down[0].IsEffectivelyEnabled);
        Assert.False(down[2].IsEffectivelyEnabled);
        Assert.True(h.View.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked);
    }

    [AvaloniaFact]
    public void Moving_a_row_down_reorders_the_list_and_updates_the_buttons()
    {
        using var h = Create();

        var down = Buttons(h.View, "▼")[0];
        down.Command!.Execute(down.CommandParameter);
        h.Pump();

        Assert.Equal(["second", "first", "third"], h.View.ViewModel.Items.Select(i => i.Message));
        Assert.Equal(["second", "first", "third"], Messages(h.View).Select(t => t.Text));
        Assert.False(Buttons(h.View, "▲")[0].IsEffectivelyEnabled);
        Assert.True(Buttons(h.View, "▲")[1].IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void Picking_an_action_updates_the_row_and_starting_asks_to_rewrite_history()
    {
        using var h = Create();
        var combo = h.View.GetVisualDescendants().OfType<ComboBox>().ToList()[1];

        combo.SelectedItem = RebaseAction.Drop;
        var start = Buttons(h.View, "Start rebase").Single();
        start.Command!.Execute(start.CommandParameter);
        h.Pump();

        Assert.Equal(RebaseAction.Drop, h.View.ViewModel.Items[1].Action);
        var call = Assert.Single(h.Dialogs.DestructiveCalls);
        Assert.Equal("Rewrite history", call.Title);
        Assert.Equal("Start rebase", call.ConfirmLabel);
        Assert.Equal("Rebase feature onto main? This rewrites history: 1 dropped commit(s).", call.Effect);
        Assert.Empty(h.Rebase.Started);
    }

    [AvaloniaFact]
    public void Cancel_returns_to_the_repository()
    {
        using var h = Create();

        var cancel = Buttons(h.View, "Cancel").Single();
        cancel.Command!.Execute(cancel.CommandParameter);

        Assert.Equal(["repo"], h.Navigations);
    }

    private sealed class RecordingRebase : IRebaseService
    {
        public List<RebaseSession> Started { get; } = [];

        public Task<RebaseSession> BuildSessionAsync(RepositoryContext context, string upstreamRef, CancellationToken ct = default)
        {
            RebaseEntry Entry(char id, string message) => new()
            {
                CommitId = new GitObjectId(new string(id, 40)),
                MessageShort = message,
                AuthorName = "A",
                AuthorEmail = "a@example.com",
            };

            return Task.FromResult(new RebaseSession
            {
                OntoCommitId = new GitObjectId(new string('f', 40)),
                OntoRefName = upstreamRef,
                UpstreamRefName = upstreamRef,
                SourceBranchName = "feature",
                Entries = [Entry('1', "first"), Entry('2', "second"), Entry('3', "third")],
            });
        }

        public IReadOnlyList<RebaseValidationIssue> Validate(RebaseSession session) => [];

        public Task StartAsync(RebaseSession session, RepositoryContext context, bool autostash, CancellationToken ct = default)
        {
            Started.Add(session);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDialogs : IDialogService
    {
        public List<(string Title, string Effect, string ConfirmLabel)> DestructiveCalls { get; } = [];

        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
        {
            DestructiveCalls.Add((title, effect, confirmLabel));
            return Task.FromResult(false);
        }

        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false, string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None) => Task.FromResult(false);
        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None) => Task.FromResult(-1);
        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null, string confirmLabel = "OK", bool primaryConfirm = false) => Task.FromResult<string?>(null);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }

    private sealed class FocusedWorkspace(RepositoryContext? focused) : IWorkspaceService
    {
        public RepositoryContext? Focused => focused;
        public IReadOnlyList<RepositoryContext> OpenRepositories => focused is null ? [] : [focused];

#pragma warning disable CS0067
        public event EventHandler<FocusedRepositoryChangedEventArgs>? FocusedRepositoryChanged;
        public event EventHandler<OpenRepositoriesChangedEventArgs>? OpenRepositoriesChanged;
#pragma warning restore CS0067

        public void Focus(RepositoryContext context) { }
        public Task<RepositoryContext> OpenAsync(string repositoryPath, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> InitAsync(string path, string? initialBranch, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> CloneAsync(string url, string targetDirectory, CloneOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RepositoryContext> OpenSubmoduleAsync(RepositoryContext owner, SubmoduleInfo submodule, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CloseAsync(RepositoryContext context) => Task.CompletedTask;
        public void EnsureInitialized(RepositoryContext context) { }
        public void Move(RepositoryContext context, int newIndex) { }
        public Task RestoreSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeLayoutService : IRepoLayoutService
    {
        public IReadOnlyList<GraphColumn> GlobalColumns => GraphColumnDefaults.All;
        public event EventHandler<RepoLayoutChangedEventArgs>? Changed { add { } remove { } }
        public RepoLayout Get(string repoPath) => new();
        public Task UpdateAsync(string repoPath, Func<RepoLayout, RepoLayout> mutate) => Task.CompletedTask;
        public Task UpdateGlobalAsync(Func<IReadOnlyList<GraphColumn>, IReadOnlyList<GraphColumn>> mutate) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeVault : ICredentialVaultService
    {
        public Task StoreTokenAsync(string key, string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteTokenAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}
