namespace Historia.Tests;

/// <summary>
/// Verifies that mutating operations across all seven power-tool services execute inside
/// the repository write lock, ensuring a concurrent stage operation waits rather than
/// colliding with index.lock.
/// </summary>
[Trait("tier", "git")]
public sealed class PowerToolWriteLockTests
{
    [Fact(Timeout = 60000)]
    public async Task Stash_holds_write_lock_and_concurrent_stage_waits()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("pt-stash-seed", r =>
        {
            r.WriteFile("f.txt", "base\n").Stage().Commit("c1");
        });
        b.WriteFile("f.txt", "dirty\n");

        var blockingCli = new BlockingGitCliService(host.Cli, req => req.Arguments is ["stash", "push", ..]);
        await using var ctx = host.Open(b.Path, blockingCli);
        var svc = new StashService(blockingCli);

        File.WriteAllText(Path.Combine(b.Path, "stage-concurrent.txt"), "concurrent\n");

        var toolTask = Task.Run(async () =>
        {
            var saved = await svc.SaveAsync(ctx, new StashSaveOptions { Message = "wip" });
            Assert.True(saved);
        });

        await blockingCli.Holding;

        var stageTask = Task.Run(async () =>
        {
            await ctx.Gateway.StageAsync(["stage-concurrent.txt"]);
        });

        await Task.Delay(100);
        Assert.False(stageTask.IsCompleted);

        blockingCli.Release();
        await Task.WhenAll(toolTask, stageTask);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Contains(status.Staged, f => f.Path == "stage-concurrent.txt");
    }

    [Fact(Timeout = 60000)]
    public async Task CherryPick_holds_write_lock_and_concurrent_stage_waits()
    {
        await using var host = new MiniHost();
        GitObjectId targetCommit = default;
        using var b = TestRepositoryBuilder.FromTemplate("pt-cp-seed", r =>
        {
            r.WriteFile("f.txt", "base\n").Stage().Commit("c1");
            r.WriteFile("f.txt", "c2\n").Stage().Commit("c2");
            targetCommit = GitObjectId.Parse(r.Git("rev-parse", "HEAD").Trim());
            r.Git("checkout", "HEAD~1");
            r.Git("checkout", "-b", "feature");
            r.WriteFile("other.txt", "other\n").Stage().Commit("c3");
        });

        var blockingCli = new BlockingGitCliService(host.Cli, req => req.Arguments.Count > 0 && req.Arguments[0] == "cherry-pick");
        await using var ctx = host.Open(b.Path, blockingCli);
        var svc = new CherryPickService(blockingCli);

        File.WriteAllText(Path.Combine(b.Path, "stage-concurrent.txt"), "concurrent\n");

        var toolTask = Task.Run(async () =>
        {
            await svc.CherryPickAsync(ctx, [targetCommit]);
        });

        await blockingCli.Holding;

        var stageTask = Task.Run(async () =>
        {
            await ctx.Gateway.StageAsync(["stage-concurrent.txt"]);
        });

        await Task.Delay(100);
        Assert.False(stageTask.IsCompleted);

        blockingCli.Release();
        await Task.WhenAll(toolTask, stageTask);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Contains(status.Staged, f => f.Path == "stage-concurrent.txt");
    }

    [Fact(Timeout = 60000)]
    public async Task Rebase_holds_write_lock_and_concurrent_stage_waits()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("pt-rebase-seed", r =>
        {
            r.WriteFile("base.txt", "base\n").Stage().Commit("c1");
            r.WriteFile("main.txt", "main\n").Stage().Commit("c2");
            r.Git("checkout", "HEAD~1");
            r.Git("checkout", "-b", "feature");
            r.WriteFile("feature.txt", "feat\n").Stage().Commit("c3");
        });

        var blockingCli = new BlockingGitCliService(host.Cli, req => req.Arguments.Count > 0 && req.Arguments[0] == "rebase");
        await using var ctx = host.Open(b.Path, blockingCli);
        var svc = new RebaseService(blockingCli);

        var session = await svc.BuildSessionAsync(ctx, "main");

        File.WriteAllText(Path.Combine(b.Path, "stage-concurrent.txt"), "concurrent\n");

        var toolTask = Task.Run(async () =>
        {
            await svc.StartAsync(session, ctx, autostash: false);
        });

        await blockingCli.Holding;

        var stageTask = Task.Run(async () =>
        {
            await ctx.Gateway.StageAsync(["stage-concurrent.txt"]);
        });

        await Task.Delay(100);
        Assert.False(stageTask.IsCompleted);

        blockingCli.Release();
        await Task.WhenAll(toolTask, stageTask);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Contains(status.Staged, f => f.Path == "stage-concurrent.txt");
    }

    [Fact(Timeout = 60000)]
    public async Task Conflict_holds_write_lock_and_concurrent_stage_waits()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("pt-conflict-seed", r =>
        {
            r.WriteFile("f.txt", "line1\nshared\nline3\n").Stage().Commit("base");
            r.Checkout("feature", create: true).WriteFile("f.txt", "line1\nTHEIRS\nline3\n").Stage().Commit("theirs");
            r.Checkout("main").WriteFile("f.txt", "line1\nOURS\nline3\n").Stage().Commit("ours");
            try { r.Git("merge", "feature"); } catch { /* conflict */ }
        });

        var blockingCli = new BlockingGitCliService(host.Cli, req => req.Arguments.Count > 0 && req.Arguments[0] == "checkout" && req.Arguments.Contains("--ours"));
        await using var ctx = host.Open(b.Path, blockingCli);
        var svc = new ConflictService(blockingCli, host.Settings);

        File.WriteAllText(Path.Combine(b.Path, "stage-concurrent.txt"), "concurrent\n");

        var toolTask = Task.Run(async () =>
        {
            await svc.ResolveWithSideAsync(ctx, "f.txt", ConflictSide.Ours);
        });

        await blockingCli.Holding;

        var stageTask = Task.Run(async () =>
        {
            await ctx.Gateway.StageAsync(["stage-concurrent.txt"]);
        });

        await Task.Delay(100);
        Assert.False(stageTask.IsCompleted);

        blockingCli.Release();
        await Task.WhenAll(toolTask, stageTask);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Contains(status.Staged, f => f.Path == "stage-concurrent.txt");
    }

    // `git submodule` is a shell script in Git for Windows: list and deinit each start sh.exe,
    // which costs seconds and slows sharply when the suite runs in parallel.
    [Fact(Timeout = 60000)]
    public async Task Submodule_holds_write_lock_and_concurrent_stage_waits()
    {
        await using var host = new MiniHost();
        using var inner = TestRepositoryBuilder.FromTemplate("pt-sub-inner", r =>
            r.WriteFile("lib.txt", "lib\n").Stage().Commit("lib c1"));
        // The submodule is set up with plain git: only RemoveAsync is under test here, and
        // SubmoduleServiceTests covers AddAsync.
        using var outer = TestRepositoryBuilder.FromTemplate("pt-sub-outer", r =>
        {
            r.WriteFile("app.txt", "app\n").Stage().Commit("app c1");
            r.Git("-c", "protocol.file.allow=always", "submodule", "add", inner.Path.Replace('\\', '/'), "vendor/lib");
            r.Commit("add submodule");
        });

        var fileCli = new FileProtocolGitCli(host.Cli);
        var blockingCli = new BlockingGitCliService(fileCli, req => req.Arguments.Count > 0 && req.Arguments[0] == "submodule" && req.Arguments.Contains("deinit"));
        await using var ctx = host.Open(outer.Path, blockingCli);
        var svc = new SubmoduleService(blockingCli);

        var subs = await svc.ListAsync(ctx);
        var sub = Assert.Single(subs);

        File.WriteAllText(Path.Combine(outer.Path, "stage-concurrent.txt"), "concurrent\n");

        var toolTask = Task.Run(async () =>
        {
            await svc.RemoveAsync(ctx, sub);
        });

        await blockingCli.Holding;

        var stageTask = Task.Run(async () =>
        {
            await ctx.Gateway.StageAsync(["stage-concurrent.txt"]);
        });

        await Task.Delay(100);
        Assert.False(stageTask.IsCompleted);

        blockingCli.Release();
        await Task.WhenAll(toolTask, stageTask);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Contains(status.Staged, f => f.Path == "stage-concurrent.txt");
    }

    [Fact(Timeout = 60000)]
    public async Task Worktree_holds_write_lock_and_concurrent_stage_waits()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("pt-wt-seed", r =>
        {
            r.WriteFile("f.txt", "base\n").Stage().Commit("c1");
        });

        var wtPath = Path.Combine(Path.GetDirectoryName(b.Path)!, Path.GetFileName(b.Path) + "-wt");
        var blockingCli = new BlockingGitCliService(host.Cli, req => req.Arguments.Count > 0 && req.Arguments[0] == "worktree" && req.Arguments.Contains("add"));
        await using var ctx = host.Open(b.Path, blockingCli);
        var svc = new WorktreeService(blockingCli);

        File.WriteAllText(Path.Combine(b.Path, "stage-concurrent.txt"), "concurrent\n");

        var toolTask = Task.Run(async () =>
        {
            await svc.AddAsync(ctx, new WorktreeAddRequest { Path = wtPath, NewBranch = "feature-wt" });
        });

        await blockingCli.Holding;

        var stageTask = Task.Run(async () =>
        {
            await ctx.Gateway.StageAsync(["stage-concurrent.txt"]);
        });

        await Task.Delay(100);
        Assert.False(stageTask.IsCompleted);

        blockingCli.Release();
        await Task.WhenAll(toolTask, stageTask);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Contains(status.Staged, f => f.Path == "stage-concurrent.txt");
    }

    [Fact(Timeout = 60000)]
    public async Task Patch_holds_write_lock_and_concurrent_stage_waits()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("pt-patch-seed", r =>
        {
            r.WriteFile("f.txt", "line 1\nline 2\nline 3\n").Stage().Commit("c1");
            r.WriteFile("f.txt", "line 1\nline 2 CHANGED\nline 3\n");
        });

        using var repo = new Repository(b.Path);
        var diff = repo.ProjectDiff(new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "f.txt" });
        var normalSvc = new PatchService(host.Cli);
        var patch = normalSvc.GeneratePatch(diff, PatchScope.SingleHunk, new HashSet<DiffLineRef> { new(0, 0) });

        var blockingCli = new BlockingGitCliService(host.Cli, req => req.Arguments.Count > 0 && req.Arguments[0] == "apply" && !req.Arguments.Contains("--check"));
        await using var ctx = host.Open(b.Path, blockingCli);
        var svc = new PatchService(blockingCli);

        File.WriteAllText(Path.Combine(b.Path, "stage-concurrent.txt"), "concurrent\n");

        var toolTask = Task.Run(async () =>
        {
            await svc.ApplyPatchAsync(ctx, patch, PatchTarget.StagingArea, reverse: false);
        });

        await blockingCli.Holding;

        var stageTask = Task.Run(async () =>
        {
            await ctx.Gateway.StageAsync(["stage-concurrent.txt"]);
        });

        await Task.Delay(100);
        Assert.False(stageTask.IsCompleted);

        blockingCli.Release();
        await Task.WhenAll(toolTask, stageTask);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Contains(status.Staged, f => f.Path == "stage-concurrent.txt");
    }

    private sealed class BlockingGitCliService(IGitCliService inner, Func<GitCliRequest, bool> shouldBlock) : IGitCliService
    {
        private readonly TaskCompletionSource _holding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;

        public Task Holding => _holding.Task;
        public void Release() => _release.TrySetResult();
        public bool IsGitAvailable => inner.IsGitAvailable;

        public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
            => RunAsync(request, null, null, ct);

        public async Task<GitCliResult> RunAsync(
            GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
        {
            if (shouldBlock(request) && Interlocked.CompareExchange(ref _blocked, 1, 0) == 0)
            {
                _holding.TrySetResult();
                await _release.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            return await inner.RunAsync(request, progress, onStdoutLine, ct).ConfigureAwait(false);
        }
    }
}
