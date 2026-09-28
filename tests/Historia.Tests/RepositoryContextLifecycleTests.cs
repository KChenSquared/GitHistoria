namespace Historia.Tests;

/// <summary>
/// Lifecycle tests for <see cref="RepositoryContext"/>: open/close, lifetime cancellation,
/// self-operation refresh suppression, and write lock behaviour across tab closure.
/// </summary>
[Trait("tier", "git")]
public sealed class RepositoryContextLifecycleTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Open_and_close_completes_cleanly()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("lifecycle-open-seed", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage();
            b.Commit("initial");
        });

        var ctx = _host.Open(repo.Path);
        Assert.False(ctx.IsBare);
        Assert.Equal(Path.TrimEndingDirectorySeparator(repo.Path), Path.TrimEndingDirectorySeparator(ctx.RepositoryPath));
        Assert.False(ctx.Lifetime.IsCancellationRequested);

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Closing_cancels_lifetime()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("lifecycle-lifetime-seed", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage();
            b.Commit("initial");
        });

        var ctx = _host.Open(repo.Path);
        Assert.False(ctx.Lifetime.IsCancellationRequested);

        await ctx.DisposeAsync();
        Assert.True(ctx.Lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task BeginSelfOperation_tracks_depth_and_scope()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("lifecycle-selfop-seed", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage();
            b.Commit("initial");
        });

        await using var ctx = _host.Open(repo.Path);
        Assert.False(ctx.IsSelfOperationInProgress);

        using (ctx.BeginSelfOperation())
        {
            Assert.True(ctx.IsSelfOperationInProgress);
        }

        Assert.False(ctx.IsSelfOperationInProgress);
    }

    [Fact]
    public async Task Write_lock_waiter_is_cancelled_on_context_close()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("lifecycle-writelock-seed", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage();
            b.Commit("initial");
        });

        var ctx = _host.Open(repo.Path);
        var holdTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquiredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstHolder = Task.Run(async () =>
        {
            await ctx.WithWriteLockAsync(async _ =>
            {
                acquiredTcs.SetResult();
                await holdTcs.Task;
            });
        });

        await acquiredTcs.Task;

        var secondWaiterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWaiter = Task.Run(async () =>
        {
            secondWaiterStarted.SetResult();
            await ctx.WithWriteLockAsync(async _ => await Task.CompletedTask);
        });

        await secondWaiterStarted.Task;
        await Task.Delay(50); // Give secondWaiter time to enter wait

        // Closing the context cancels Lifetime, which aborts the waiting second call.
        await ctx.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await secondWaiter);

        holdTcs.SetResult();
        await firstHolder;
    }

    [Fact]
    public async Task Nested_write_lock_throws()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("lifecycle-nested-seed", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage();
            b.Commit("initial");
        });

        await using var ctx = _host.Open(repo.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await ctx.WithWriteLockAsync(async _ =>
            {
                await ctx.WithWriteLockAsync(async _ => await Task.CompletedTask);
            });
        });
    }
}
