namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class RepositoryInfoProjectionTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Detached_head_has_no_branch_name_and_is_detached()
    {
        using var builder = TestRepositoryBuilder.FromTemplate("repoinfo-detached", b =>
        {
            b.WriteFile("f.txt", "1").Stage().Commit("initial");
            b.WriteFile("f.txt", "2").Stage().Commit("second");
            b.Git("checkout", "HEAD~1");
        });

        await using var ctx = _host.Open(builder.Path);
        var info = await ctx.Gateway.GetRepositoryInfoAsync();

        Assert.True(info.IsHeadDetached);
        Assert.False(info.IsHeadUnborn);
        Assert.Equal("(no branch)", info.CurrentBranchName);
    }

    [Fact]
    public async Task Unborn_head_has_null_branch_name_and_is_unborn()
    {
        using var builder = TestRepositoryBuilder.Create();

        await using var ctx = _host.Open(builder.Path);
        var info = await ctx.Gateway.GetRepositoryInfoAsync();

        Assert.True(info.IsHeadUnborn);
        Assert.False(info.IsHeadDetached);
        Assert.Null(info.CurrentBranchName);
    }

    [Fact]
    public async Task Bare_repository_sets_is_bare_and_uses_git_dir_as_working_dir()
    {
        var dir = TestScratch.NewDirectory("historia-bare-");
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("init");
        psi.ArgumentList.Add("--bare");
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();

        await using var ctx = _host.Open(dir);
        var info = await ctx.Gateway.GetRepositoryInfoAsync();

        Assert.True(info.IsBare);
        Assert.Equal(ctx.GitDirectory, info.WorkingDirectory);
    }

    [Fact]
    public async Task Custom_hooks_path_sets_has_commit_hooks_to_true()
    {
        using var builder = TestRepositoryBuilder.FromTemplate("repoinfo-hookspath", b =>
        {
            b.WriteFile("f.txt", "1").Stage().Commit("initial");
            var customHooks = Path.Combine(b.Path, ".custom-hooks");
            Directory.CreateDirectory(customHooks);
            File.WriteAllText(Path.Combine(customHooks, "pre-commit"), "#!/bin/sh\nexit 0\n");
            b.Git("config", "core.hooksPath", ".custom-hooks");
        });

        await using var ctx = _host.Open(builder.Path);
        var info = await ctx.Gateway.GetRepositoryInfoAsync();

        Assert.True(info.HasCommitHooks);
    }

    [Fact]
    public async Task Standard_pre_commit_hook_sets_has_commit_hooks_to_true()
    {
        using var builder = TestRepositoryBuilder.FromTemplate("repoinfo-standardhook", b =>
        {
            b.WriteFile("f.txt", "1").Stage().Commit("initial");
            var hooks = Path.Combine(b.Path, ".git", "hooks");
            Directory.CreateDirectory(hooks);
            File.WriteAllText(Path.Combine(hooks, "commit-msg"), "#!/bin/sh\nexit 0\n");
        });

        await using var ctx = _host.Open(builder.Path);
        var info = await ctx.Gateway.GetRepositoryInfoAsync();

        Assert.True(info.HasCommitHooks);
    }

    [Fact]
    public async Task Cancelled_token_throws_operation_canceled_exception()
    {
        using var builder = TestRepositoryBuilder.FromTemplate("repoinfo-cancelled", b =>
        {
            b.WriteFile("f.txt", "1").Stage().Commit("initial");
        });

        await using var ctx = _host.Open(builder.Path);
        // Prime the snapshot cache so context.RefSnapshots.GetAsync returns immediately
        await ctx.Gateway.GetRepositoryInfoAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ctx.Gateway.GetRepositoryInfoAsync(cts.Token));
    }
}
