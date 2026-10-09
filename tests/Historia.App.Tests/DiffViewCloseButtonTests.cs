namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class DiffViewCloseButtonTests : IDisposable
{
    private readonly string _repoDir;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;

    public DiffViewCloseButtonTests()
    {
        _repoDir = TestScratch.NewDirectory("historia-diff-close-");
        LibGit2Sharp.Repository.Init(_repoDir);

        File.WriteAllText(System.IO.Path.Combine(_repoDir, "test.txt"), "line 1\n");
        using (var repo = new LibGit2Sharp.Repository(_repoDir))
        {
            LibGit2Sharp.Commands.Stage(repo, "test.txt");
            var sig = new LibGit2Sharp.Signature("Test", "test@example.com", DateTimeOffset.Now);
            repo.Commit("Initial commit", sig, sig, new LibGit2Sharp.CommitOptions());
        }
        File.WriteAllText(System.IO.Path.Combine(_repoDir, "test.txt"), "line 1 changed\n");

        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(new InMemorySettingsService());
        sc.AddSingleton<IRepoLayoutService>(new FakeLayoutService());
        sc.AddSingleton<ICredentialVaultService>(new FakeVault());
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
        sc.AddSingleton<IGitCliService, GitCliService>();
        _sp = sc.BuildServiceProvider();

        _context = RepositoryContext.Open(_repoDir, _sp);
    }

    public void Dispose()
    {
        try { _context.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        try { _sp.Dispose(); } catch { }
        TestScratch.Delete(_repoDir);
    }

    [AvaloniaFact]
    public void Close_button_reads_X_and_keeps_Close_as_its_tooltip_and_automation_name()
    {
        var request = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        var view = new DiffView(_context, request, _sp.GetService<ISettingsService>(), _sp);

        var close = view.CloseButtonForTesting;

        Assert.Equal("X", close.Content);
        Assert.Equal("Close", ToolTip.GetTip(close));
        Assert.Equal("Close", Avalonia.Automation.AutomationProperties.GetName(close));
    }

    [AvaloniaFact]
    public void Clicking_close_clears_the_open_diff()
    {
        var request = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "test.txt" };
        _context.State.SetOpenDiff(request);
        var view = new DiffView(_context, request, _sp.GetService<ISettingsService>(), _sp);
        Assert.NotNull(_context.State.OpenDiff);

        view.CloseButtonForTesting.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Null(_context.State.OpenDiff);
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
