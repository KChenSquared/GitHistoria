namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class RepositoryCloseRaceTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Concurrent_DisposeAsync_calls_share_one_dispose()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("close-race-seed-1", r =>
        {
            r.WriteFile("f.txt", "1\n").Stage().Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        var d1 = ctx.DisposeAsync().AsTask();
        var d2 = ctx.DisposeAsync().AsTask();

        await Task.WhenAll(d1, d2);
    }

    [Fact]
    public async Task A_second_dispose_after_completion_is_a_no_op()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("close-race-seed-2", r =>
        {
            r.WriteFile("f.txt", "1\n").Stage().Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        await ctx.DisposeAsync();
        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Closing_a_tab_twice_concurrently_raises_no_exception()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("close-race-seed-3", r =>
        {
            r.WriteFile("f.txt", "1\n").Stage().Commit("c1");
        });

        var cli = _host.Provider.GetRequiredService<IGitCliService>();
        var recents = new RecentRepositoryService(_host.Settings);
        var profiles = new ProfileService(_host.Settings, new FakeVault(), cli);
        var identity = new RepoIdentityService(cli);
        var ws = new WorkspaceService(_host.Provider, cli, recents, profiles, identity);
        var ctx = await ws.OpenAsync(repo.Path);

        var openChangedCount = 0;
        ws.OpenRepositoriesChanged += (_, _) => Interlocked.Increment(ref openChangedCount);

        var unobservedExceptions = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> unobservedHandler = (_, e) =>
        {
            lock (unobservedExceptions)
                unobservedExceptions.Add(e.Exception);
        };
        TaskScheduler.UnobservedTaskException += unobservedHandler;

        try
        {
            var c1 = ws.CloseAsync(ctx);
            var c2 = ws.CloseAsync(ctx);

            await Task.WhenAll(c1, c2);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= unobservedHandler;
        }

        Assert.Equal(1, openChangedCount);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.Empty(unobservedExceptions);
    }
}
