namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class DiffViewFocusLineTests : IDisposable
{
    private readonly string _repoDir;
    private readonly ServiceProvider _sp;
    private readonly RepositoryContext _context;
    private readonly GitObjectId _commitId;

    public DiffViewFocusLineTests()
    {
        _repoDir = TestScratch.NewDirectory("historia-diff-focus-");
        LibGit2Sharp.Repository.Init(_repoDir);

        var file = System.IO.Path.Combine(_repoDir, "big.txt");
        var lines = Enumerable.Range(1, 200).Select(i => $"line {i}").ToArray();
        File.WriteAllText(file, string.Join("\n", lines) + "\n");

        using (var repo = new LibGit2Sharp.Repository(_repoDir))
        {
            LibGit2Sharp.Commands.Stage(repo, "big.txt");
            var sig = new LibGit2Sharp.Signature("Test", "test@example.com", DateTimeOffset.Now);
            repo.Commit("first", sig, sig, new LibGit2Sharp.CommitOptions());

            lines[4] = "line 5 changed";
            lines[169] = "line 170 changed";
            File.WriteAllText(file, string.Join("\n", lines) + "\n");
            LibGit2Sharp.Commands.Stage(repo, "big.txt");
            var second = repo.Commit("second", sig, sig, new LibGit2Sharp.CommitOptions());
            _commitId = new GitObjectId(second.Sha);
        }

        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(SettingsService.Load());
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

    private DiffRequest Request(int? focusLine) => new()
    {
        Source = DiffSource.Commit,
        CommitId = _commitId,
        Path = "big.txt",
        ParentIndex = 1,
        FocusLine = focusLine,
    };

    private static async Task<(Window Window, Control Presenter)> ShowAsync(DiffView view)
    {
        var window = new Window { Width = 900, Height = 120, Content = view };
        window.Show();
        for (var i = 0; i < 100; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (view.PresenterForTesting is { } presenter and not TextBlock)
            {
                for (var j = 0; j < 20; j++)
                {
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    await Task.Delay(20);
                }
                return (window, presenter);
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("Presenter did not load in time.");
    }

    private static ISettingsService Settings(DiffViewMode mode) =>
        new FixedSettings(new AppSettings { DefaultDiffView = mode });

    [AvaloniaTheory]
    [InlineData(DiffViewMode.Unified)]
    [InlineData(DiffViewMode.SideBySide)]
    public async Task A_focus_line_in_the_second_hunk_scrolls_there(DiffViewMode mode)
    {
        var view = new DiffView(_context, Request(170), Settings(mode), _sp);
        var (window, presenter) = await ShowAsync(view);
        try
        {
            var expected = view.ModelForTesting!.FindDisplayLineForNewLine(170, mode == DiffViewMode.SideBySide)!.Value;
            var range = presenter switch
            {
                CombinedDiffPresenter c => c.VisibleLineRange,
                SideBySideDiffPresenter s => s.VisibleLineRange,
                _ => null,
            };

            Assert.NotNull(range);
            Assert.True(range.Value.Start > 1, $"expected a scrolled view, visible {range}");
            Assert.InRange(expected, range.Value.Start, range.Value.End);
        }
        finally
        {
            view.Dispose();
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(DiffViewMode.Unified)]
    [InlineData(DiffViewMode.SideBySide)]
    public async Task No_focus_line_leaves_the_view_at_the_top(DiffViewMode mode)
    {
        var view = new DiffView(_context, Request(null), Settings(mode), _sp);
        var (window, presenter) = await ShowAsync(view);
        try
        {
            var range = presenter switch
            {
                CombinedDiffPresenter c => c.VisibleLineRange,
                SideBySideDiffPresenter s => s.VisibleLineRange,
                _ => null,
            };

            Assert.NotNull(range);
            Assert.Equal(1, range.Value.Start);
        }
        finally
        {
            view.Dispose();
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(DiffViewMode.Unified)]
    [InlineData(DiffViewMode.SideBySide)]
    public async Task Refocusing_scrolls_back_to_the_line_after_the_user_scrolled_away(DiffViewMode mode)
    {
        var view = new DiffView(_context, Request(170), Settings(mode), _sp);
        var (window, presenter) = await ShowAsync(view);
        try
        {
            (int Start, int End)? Range() => presenter switch
            {
                CombinedDiffPresenter c => c.VisibleLineRange,
                SideBySideDiffPresenter s => s.VisibleLineRange,
                _ => null,
            };
            var expected = view.ModelForTesting!.FindDisplayLineForNewLine(170, mode == DiffViewMode.SideBySide)!.Value;
            bool Showing(int line) => Range() is { } r && line >= r.Start && line <= r.End;

            if (presenter is CombinedDiffPresenter combined)
                combined.ScrollToLine(1);
            else
                ((SideBySideDiffPresenter)presenter).ScrollToLine(1);
            Assert.True(await CenterPaneControllerTests.PumpUntilAsync(window, () => Range()?.Start == 1),
                $"the view never scrolled to the top; visible {Range()}");

            view.RefocusLine();

            Assert.True(await CenterPaneControllerTests.PumpUntilAsync(window, () => Showing(expected)),
                $"refocusing never scrolled back to {expected}; visible {Range()}");
        }
        finally
        {
            view.Dispose();
            window.Close();
        }
    }

    [Fact]
    public void Requests_that_differ_only_by_focus_line_are_different()
    {
        Assert.NotEqual(Request(10), Request(11));
        Assert.NotEqual(Request(10), Request(null));
        Assert.Equal(Request(10), Request(10));
    }

    private sealed class FixedSettings(AppSettings initial) : ISettingsService
    {
        public AppSettings Current { get; private set; } = initial;
        public event EventHandler<SettingsChangedEventArgs>? Changed;
        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
        {
            var old = Current;
            Current = mutate(Current);
            Changed?.Invoke(this, new SettingsChangedEventArgs(old, Current));
            return Task.CompletedTask;
        }
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
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
