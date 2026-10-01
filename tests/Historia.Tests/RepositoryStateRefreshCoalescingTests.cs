namespace Historia.Tests;

/// <summary>
/// Single-flight coalescing and incremental ref publishing.
/// Ensures concurrent calls run the gateway once, mid-flight callers get a trailing run,
/// faults are not cached, joiner cancellation does not cancel shared runs, and branches
/// publish before tags finish.
/// </summary>
[Trait("tier", "git")]
// Serialized: one test watches the process-wide UnobservedTaskException, which any concurrently
// running test can raise.
[Collection("serial")]
public sealed class RepositoryStateRefreshCoalescingTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo = TestRepositoryBuilder.FromTemplate("empty-repo", _ => { });

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _repo.Dispose();
    }

    private static BranchInfo MakeBranch(string name, bool isCurrent = true) => new()
    {
        Name = name,
        CanonicalName = $"refs/heads/{name}",
        IsRemote = false,
        IsCurrent = isCurrent,
        TipCommitId = null
    };

    private static TagInfo MakeTag(string name, GitObjectId? id = null) => new()
    {
        Name = name,
        TargetCommitId = id ?? new GitObjectId("0123456789012345678901234567890123456789"),
        IsAnnotated = false
    };

    [Fact(Timeout = 10000)]
    public async Task Concurrent_refresh_refs_calls_run_the_gateway_once()
    {
        var gateway = new FakeGitGateway();
        var branchesEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var branchesProceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        gateway.OnGetBranches = async ct =>
        {
            branchesEntered.TrySetResult();
            await branchesProceed.Task.WaitAsync(ct);
            return [MakeBranch("main")];
        };

        var ctx = _host.Open(_repo.Path);
        var state = new RepositoryStateService(ctx, gateway: gateway);

        var task1 = state.RefreshRefsAsync();
        await branchesEntered.Task;

        // Call 2 arrives while task1 is in-flight before data is read:
        var task2 = state.RefreshRefsAsync();

        branchesProceed.TrySetResult();
        await Task.WhenAll(task1, task2);

        Assert.Equal(1, gateway.BranchesCalls);
        Assert.Equal(1, gateway.TagsCalls);
    }

    [Fact(Timeout = 10000)]
    public async Task Caller_arriving_mid_flight_gets_a_trailing_refresh()
    {
        var gateway = new FakeGitGateway();
        var tagsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tagsProceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        gateway.OnGetTags = async ct =>
        {
            tagsEntered.TrySetResult();
            await tagsProceed.Task.WaitAsync(ct);
            return [];
        };

        var ctx = _host.Open(_repo.Path);
        var state = new RepositoryStateService(ctx, gateway: gateway);

        var task1 = state.RefreshRefsAsync();
        await tagsEntered.Task;

        // Call 2 arrives mid-flight AFTER data was read:
        var task2 = state.RefreshRefsAsync();

        tagsProceed.TrySetResult();
        await task1;
        await task2;

        Assert.Equal(2, gateway.BranchesCalls);
        Assert.Equal(2, gateway.TagsCalls);
    }

    [Fact(Timeout = 10000)]
    public async Task Faulted_refresh_is_not_cached_for_later_callers()
    {
        var gateway = new FakeGitGateway();
        var shouldThrow = true;
        gateway.OnGetBranches = ct =>
        {
            if (shouldThrow)
                throw new InvalidOperationException("simulated git fault");
            return Task.FromResult<IReadOnlyList<BranchInfo>>([MakeBranch("main")]);
        };

        var ctx = _host.Open(_repo.Path);
        var state = new RepositoryStateService(ctx, gateway: gateway);

        // First call fails:
        await Assert.ThrowsAsync<InvalidOperationException>(() => state.RefreshRefsAsync());

        // Later call after first call settled:
        shouldThrow = false;
        await state.RefreshRefsAsync();

        Assert.Equal(2, gateway.BranchesCalls);
        Assert.Single(state.Branches);
    }

    [Fact(Timeout = 10000)]
    public async Task Joiner_cancellation_does_not_cancel_the_shared_refresh()
    {
        var gateway = new FakeGitGateway();
        var branchesEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var branchesProceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        gateway.OnGetBranches = async ct =>
        {
            branchesEntered.TrySetResult();
            await branchesProceed.Task;
            return [MakeBranch("main")];
        };

        var ctx = _host.Open(_repo.Path);
        var state = new RepositoryStateService(ctx, gateway: gateway);

        var task1 = state.RefreshRefsAsync(CancellationToken.None);
        await branchesEntered.Task;

        using var joinerCts = new CancellationTokenSource();
        var task2 = state.RefreshRefsAsync(joinerCts.Token);

        // Cancel joiner's token:
        joinerCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task2);

        // Shared run completes successfully:
        branchesProceed.TrySetResult();
        await task1;

        Assert.Single(state.Branches);
    }

    [Fact(Timeout = 10000)]
    public async Task When_every_waiter_gives_up_the_refs_refresh_is_cancelled()
    {
        var gateway = new FakeGitGateway();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.OnGetBranches = async ct =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return Array.Empty<BranchInfo>();
        };

        var ctx = _host.Open(_repo.Path);
        var state = new RepositoryStateService(ctx, gateway: gateway);

        using var cts = new CancellationTokenSource();
        var only = state.RefreshRefsAsync(cts.Token);
        await entered.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The next refresh runs afresh rather than joining the cancelled one.
        gateway.OnGetBranches = _ => Task.FromResult<IReadOnlyList<BranchInfo>>([MakeBranch("main")]);
        await state.RefreshRefsAsync();
        Assert.Single(state.Branches);
    }

    [Fact(Timeout = 10000)]
    public async Task When_every_waiter_gives_up_the_status_refresh_is_cancelled()
    {
        var gateway = new FakeGitGateway();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.OnGetStatus = async ct =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return Historia.Models.RepositoryStatus.Empty;
        };

        var ctx = _host.Open(_repo.Path);
        var state = new RepositoryStateService(ctx, gateway: gateway);

        using var cts = new CancellationTokenSource();
        var only = state.RefreshStatusAsync(cts.Token);
        await entered.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact(Timeout = 10000)]
    public async Task A_waiter_without_a_token_keeps_the_refresh_running_when_the_others_leave()
    {
        var gateway = new FakeGitGateway();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        gateway.OnGetBranches = async ct =>
        {
            entered.TrySetResult();
            using (ct.Register(() => cancelled = true))
                await proceed.Task;
            return [MakeBranch("main")];
        };

        var ctx = _host.Open(_repo.Path);
        var state = new RepositoryStateService(ctx, gateway: gateway);

        using var cts = new CancellationTokenSource();
        var leaver = state.RefreshRefsAsync(cts.Token);
        await entered.Task;
        var stayer = state.RefreshRefsAsync();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaver);

        proceed.TrySetResult();
        await stayer;
        Assert.False(cancelled);
        Assert.Single(state.Branches);
    }

    [Fact(Timeout = 10000)]
    public async Task A_caller_that_already_gave_up_starts_no_refresh()
    {
        var gateway = new FakeGitGateway();
        var ctx = _host.Open(_repo.Path);
        var state = new RepositoryStateService(ctx, gateway: gateway);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => state.RefreshRefsAsync(new CancellationToken(canceled: true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => state.RefreshStatusAsync(new CancellationToken(canceled: true)));

        Assert.Equal(0, gateway.BranchesCalls);
        Assert.Equal(0, gateway.StatusCalls);
    }

    [Fact(Timeout = 10000)]
    public async Task Branches_are_published_before_tags_are_read()
    {
        var gateway = new FakeGitGateway();
        var tagsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tagsProceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        gateway.OnGetBranches = ct => Task.FromResult<IReadOnlyList<BranchInfo>>([
            MakeBranch("feature")
        ]);

        gateway.OnGetTags = async ct =>
        {
            tagsEntered.TrySetResult();
            await tagsProceed.Task.WaitAsync(ct);
            return [MakeTag("v1.0")];
        };

        var ctx = _host.Open(_repo.Path);
        var state = new RepositoryStateService(ctx, gateway: gateway);

        RefsChangedEventArgs? eventBeforeTagsRead = null;
        state.RefsChanged += (_, e) =>
        {
            eventBeforeTagsRead ??= e;
        };

        var refreshTask = state.RefreshRefsAsync();

        await tagsEntered.Task;

        // Branches published before tags are read:
        Assert.NotNull(eventBeforeTagsRead);
        Assert.Single(eventBeforeTagsRead!.Branches);
        Assert.Equal("feature", eventBeforeTagsRead.Branches[0].Name);
        Assert.Empty(eventBeforeTagsRead.Tags);

        Assert.Single(state.Branches);
        Assert.Equal("feature", state.Branches[0].Name);

        // Release tags:
        tagsProceed.TrySetResult();
        await refreshTask;

        Assert.Single(state.Tags);
        Assert.Equal("v1.0", state.Tags[0].Name);
    }

    [Fact(Timeout = 10000)]
    public async Task Joiner_cancellation_with_subsequent_fault_does_not_leak_unobserved_task_exception()
    {
        var gateway = new FakeGitGateway();
        var branchesEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var branchesProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        gateway.OnGetBranches = async ct =>
        {
            branchesEntered.TrySetResult();
            await branchesProceed.Task;
            throw new InvalidOperationException("simulated gateway failure after joiner canceled");
        };

        var ctx = _host.Open(_repo.Path);
        var state = new RepositoryStateService(ctx, gateway: gateway);

        using var joinerCts = new CancellationTokenSource();
        var task = state.RefreshRefsAsync(joinerCts.Token);
        await branchesEntered.Task;

        // Joiner cancels early:
        joinerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        // Underlying shared task completes with fault:
        branchesProceed.TrySetResult(true);

        // Wait brief tick for coalescer pump to finish:
        await Task.Delay(50);

        // Verify unobserved task exception is not raised on GC finalizer thread:
        var unobservedSeen = false;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            unobservedSeen = true;
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(unobservedSeen, "Shared coalesced faulted task leaked an unobserved exception to the finalizer thread.");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact(Timeout = 10000)]
    public async Task Disposing_repository_context_during_status_refresh_completes_cleanly_without_ObjectDisposedException()
    {
        var gateway = new FakeGitGateway();
        var statusEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusProceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        gateway.OnGetStatus = async ct =>
        {
            statusEntered.TrySetResult();
            await statusProceed.Task.WaitAsync(ct);
            return Historia.Models.RepositoryStatus.Empty;
        };

        // Finalize earlier tests' faulted tasks now, so the check below sees only this test's.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var ctx = _host.Open(_repo.Path);
        var state = new RepositoryStateService(ctx, gateway: gateway);

        var refreshTask = state.RefreshStatusAsync(ctx.Lifetime);
        await statusEntered.Task;

        // Dispose context while refresh is mid-flight:
        var disposeTask = ctx.DisposeAsync().AsTask();

        // Release status gateway (which will observe cancellation from ctx.Lifetime):
        statusProceed.TrySetResult();

        // Refresh task should complete as canceled (or succeed gracefully), never fault with ObjectDisposedException
        try
        {
            await refreshTask;
        }
        catch (OperationCanceledException)
        {
            // Expected cancellation
        }

        await disposeTask;

        // Verify ctx.Lifetime remains accessible without throwing ObjectDisposedException
        var token = ctx.Lifetime;
        Assert.True(token.IsCancellationRequested);

        // Finalizer check
        var unobservedSeen = false;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            unobservedSeen = true;
            e.SetObserved();
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(unobservedSeen);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    private sealed class FakeGitGateway : IGitGateway
    {
        public string RepositoryPath => "C:\\fake\\repo";

        public Func<CancellationToken, Task<IReadOnlyList<BranchInfo>>>? OnGetBranches { get; set; }
        public Func<CancellationToken, Task<IReadOnlyList<RemoteInfo>>>? OnGetRemotes { get; set; }
        public Func<CancellationToken, Task<IReadOnlyList<TagInfo>>>? OnGetTags { get; set; }
        public Func<CancellationToken, Task<Historia.Models.RepositoryStatus>>? OnGetStatus { get; set; }
        public Func<CancellationToken, Task<RepositoryInfo>>? OnGetRepositoryInfo { get; set; }

        public int BranchesCalls;
        public int RemotesCalls;
        public int TagsCalls;
        public int StatusCalls;

        public Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref BranchesCalls);
            return OnGetBranches != null ? OnGetBranches(ct) : Task.FromResult<IReadOnlyList<BranchInfo>>([]);
        }

        public Task<IReadOnlyList<RemoteInfo>> GetRemotesAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref RemotesCalls);
            return OnGetRemotes != null ? OnGetRemotes(ct) : Task.FromResult<IReadOnlyList<RemoteInfo>>([]);
        }

        public Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref TagsCalls);
            return OnGetTags != null ? OnGetTags(ct) : Task.FromResult<IReadOnlyList<TagInfo>>([]);
        }

        public Task<Historia.Models.RepositoryStatus> GetStatusAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref StatusCalls);
            return OnGetStatus != null ? OnGetStatus(ct) : Task.FromResult(Historia.Models.RepositoryStatus.Empty);
        }

        public Task<RepositoryInfo> GetRepositoryInfoAsync(CancellationToken ct = default)
        {
            return OnGetRepositoryInfo != null ? OnGetRepositoryInfo(ct) : Task.FromResult(new RepositoryInfo
            {
                WorkingDirectory = RepositoryPath,
                GitDirectory = RepositoryPath + "\\.git",
                DisplayName = "fake",
                IsBare = false,
                IsHeadDetached = false,
                IsHeadUnborn = false,
                CurrentBranchName = "main",
            });
        }

        public Task<AheadBehind?> GetDivergenceAsync(string local, string remote, CancellationToken ct = default) => Task.FromResult<AheadBehind?>(null);
        public Task<CommitDetail> GetCommitDetailAsync(GitObjectId id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<FileStatusEntry>> GetCommitFilesAsync(GitObjectId id, int parentIndex, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string? Name, string? Email)> GetConfiguredIdentityAsync(CancellationToken ct = default) => Task.FromResult<(string?, string?)>((null, null));
        public Task<FileDiff> GetFileDiffAsync(DiffRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string? Name, string? Email)> GetGlobalIdentityAsync(CancellationToken ct = default) => Task.FromResult<(string?, string?)>((null, null));
        public Task<bool> GetGpgSignConfiguredAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitObjectId> CommitAsync(CommitRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task MergeAsync(string refish, bool noFastForward, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ResetAsync(string target, GitResetMode mode, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CountCommitsLeavingAsync(string target, CancellationToken ct = default) => throw new NotImplementedException();

        public Task CheckoutAsync(string refName, GitCheckoutOptions options, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BranchInfo> CreateBranchAsync(string name, string startPoint, bool checkout, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteBranchAsync(string name, bool force, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RenameBranchAsync(string oldName, string newName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetUpstreamAsync(string branchName, string upstreamRef, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TagInfo> CreateTagAsync(string name, string target, string? message, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteTagAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();

        public Task AddRemoteAsync(string name, string url, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RemoveRemoteAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetRemoteUrlAsync(string name, string url, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> RenameRemoteAsync(string oldName, string newName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetRemotePushUrlAsync(string name, string? pushUrl, CancellationToken ct = default) => throw new NotImplementedException();

        public Task FetchAsync(string remote, GitFetchOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task PullAsync(string remote, string branch, GitPullOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task PushAsync(string remote, PushSpec spec, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task PruneRemoteAsync(string name, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
