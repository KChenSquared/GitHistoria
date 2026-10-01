namespace Historia.Tests;

/// <summary>
/// Verifies the per-context write lock serializes mutating Git operations, allows concurrent readers,
/// rejects re-entrant calls, cancels waiters on tab lifetime expiration, and releases on exceptions.
/// </summary>
[Trait("tier", "git")]
public sealed class RepositoryWriteLockTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Two_writers_serialize()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
        {
            b.WriteFile("a.txt", "initial\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        var firstHolding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        var writer1 = Task.Run(async () =>
        {
            await ctx.WithWriteLockAsync(async ct =>
            {
                lock (order) order.Add("writer1-enter");
                firstHolding.SetResult();
                await releaseFirst.Task;
                lock (order) order.Add("writer1-exit");
            });
        });

        await firstHolding.Task;

        var writer2 = Task.Run(async () =>
        {
            await ctx.WithWriteLockAsync(ct =>
            {
                lock (order) order.Add("writer2-enter");
                return Task.CompletedTask;
            });
        });

        await Task.Delay(50);
        lock (order)
        {
            Assert.DoesNotContain("writer2-enter", order);
        }

        releaseFirst.SetResult();
        await Task.WhenAll(writer1, writer2);

        lock (order)
        {
            Assert.Equal(["writer1-enter", "writer1-exit", "writer2-enter"], order);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Reader_runs_while_writer_holds_write_lock()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
        {
            b.WriteFile("a.txt", "initial\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        var writerHolding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writerTask = Task.Run(async () =>
        {
            await ctx.WithWriteLockAsync(async ct =>
            {
                writerHolding.SetResult();
                await releaseWriter.Task;
            });
        });

        await writerHolding.Task;

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.NotNull(status);

        releaseWriter.SetResult();
        await writerTask;
    }

    [Fact]
    public async Task Nested_acquire_throws_InvalidOperationException()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
        {
            b.WriteFile("a.txt", "initial\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await ctx.WithWriteLockAsync(async ct =>
            {
                await ctx.WithWriteLockAsync(innerCt => Task.CompletedTask, ct);
            });
        });
    }

    [Fact(Timeout = 30000)]
    public async Task Tab_close_cancels_waiter()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
        {
            b.WriteFile("a.txt", "initial\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        var writer1Holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writer1 = Task.Run(async () =>
        {
            await ctx.WithWriteLockAsync(async ct =>
            {
                writer1Holding.SetResult();
                await releaseWriter1.Task;
            });
        });

        await writer1Holding.Task;

        var waiterCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = Task.Run(async () =>
        {
            try
            {
                await ctx.WithWriteLockAsync(ct => Task.CompletedTask);
            }
            catch (OperationCanceledException)
            {
                waiterCanceled.SetResult(true);
            }
        });

        await ctx.DisposeAsync();

        var wasCanceled = await waiterCanceled.Task;
        Assert.True(wasCanceled);

        releaseWriter1.SetResult();
        await writer1;
        await waiter;
    }

    [Fact]
    public async Task Lock_released_on_exception()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
        {
            b.WriteFile("a.txt", "initial\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await ctx.WithWriteLockAsync<int>(ct => throw new InvalidOperationException("boom"));
        });

        var acquired = false;
        await ctx.WithWriteLockAsync(ct =>
        {
            acquired = true;
            return Task.CompletedTask;
        });

        Assert.True(acquired);
    }

    [Fact]
    public async Task PerfLog_records_wait_and_hold_marks()
    {
        // Flow-local: setting PerfLog.Enabled would turn on perf logging for every parallel test.
        PerfLog.OverrideEnabledForTesting(true);
        try
        {
            using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
            {
                b.WriteFile("a.txt", "initial\n").Stage();
                b.Commit("c1");
            });
            var ctx = _host.Open(repo.Path);

            var startLineCount = File.Exists(PerfLog.LogPath)
                ? File.ReadAllLines(PerfLog.LogPath).Length
                : 0;

            await ctx.WithWriteLockAsync(async ct =>
            {
                await Task.Delay(15, ct);
            });

            var lines = File.ReadAllLines(PerfLog.LogPath).Skip(startLineCount).ToList();
            Assert.Contains(lines, l => l.Contains("write lock hold") && l.Contains(nameof(PerfLog_records_wait_and_hold_marks)));
        }
        finally
        {
            PerfLog.OverrideEnabledForTesting(null);
        }
    }
}
