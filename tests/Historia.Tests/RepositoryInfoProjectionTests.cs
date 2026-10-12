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
        TestGit.Run(dir, ["init", "--bare"], testIdentity: false);

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

    [Fact]
    public async Task Cancellation_during_hooks_lookup_throws_and_caches_nothing()
    {
        using var builder = TestRepositoryBuilder.FromTemplate("repoinfo-cancel-midspawn", b =>
        {
            b.WriteFile("f.txt", "1").Stage().Commit("initial");
            var customHooks = Path.Combine(b.Path, ".custom-hooks");
            Directory.CreateDirectory(customHooks);
            File.WriteAllText(Path.Combine(customHooks, "pre-commit"), "#!/bin/sh\nexit 0\n");
            b.Git("config", "core.hooksPath", ".custom-hooks");
        });

        using var cts = new CancellationTokenSource();
        var armed = true;
        var cli = new InterceptingGitCliService(_host.Cli)
        {
            Intercept = r =>
            {
                if (!armed || !r.Arguments.Contains("--git-path"))
                    return null;
                cts.Cancel();
                return InterceptingGitCliService.Failure(GitErrorKind.Cancelled, exitCode: -1);
            },
        };
        await using var ctx = _host.Open(builder.Path, cli);

        // The CLI reports a cancelled spawn as a result; the lookup must still throw.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ctx.Gateway.GetRepositoryInfoAsync(cts.Token));

        // A cached <gitdir>/hooks fallback would hide core.hooksPath until the config changes.
        armed = false;
        var info = await ctx.Gateway.GetRepositoryInfoAsync();
        Assert.True(info.HasCommitHooks);
    }

    [Fact]
    public async Task Detached_head_reports_head_commit_id()
    {
        string headSha = "";
        using var builder = TestRepositoryBuilder.FromTemplate("repoinfo-detached-commit-id", b =>
        {
            b.WriteFile("f.txt", "1").Stage().Commit("initial");
            b.WriteFile("f.txt", "2").Stage().Commit("second");
            b.Git("checkout", "HEAD~1");
            headSha = b.HeadSha;
        });

        await using var ctx = _host.Open(builder.Path);
        var info = await ctx.Gateway.GetRepositoryInfoAsync();

        Assert.True(info.IsHeadDetached);
        Assert.Equal(new GitObjectId(headSha), info.HeadCommitId);
    }

    [Fact]
    public async Task Attached_head_reports_head_commit_id()
    {
        string headSha = "";
        using var builder = TestRepositoryBuilder.FromTemplate("repoinfo-attached-commit-id", b =>
        {
            b.WriteFile("f.txt", "1").Stage().Commit("initial");
            headSha = b.HeadSha;
        });

        await using var ctx = _host.Open(builder.Path);
        var info = await ctx.Gateway.GetRepositoryInfoAsync();

        Assert.False(info.IsHeadDetached);
        Assert.Equal(new GitObjectId(headSha), info.HeadCommitId);
    }

    [Fact]
    public async Task Unborn_head_has_no_head_commit_id()
    {
        using var builder = TestRepositoryBuilder.Create();

        await using var ctx = _host.Open(builder.Path);
        var info = await ctx.Gateway.GetRepositoryInfoAsync();

        Assert.True(info.IsHeadUnborn);
        Assert.Null(info.HeadCommitId);
    }

    [Fact]
    public async Task Submodule_after_update_is_detached()
    {
        using var inner = TestRepositoryBuilder.Create();
        inner.WriteFile("sub.txt", "sub1").Stage().Commit("sub initial");

        using var super = TestRepositoryBuilder.Create();
        super.Git("config", "protocol.file.allow", "always");
        super.WriteFile("readme.txt", "super").Stage().Commit("super initial");
        super.Git(
            new Dictionary<string, string> { ["GIT_ALLOW_PROTOCOL"] = "file" },
            "-c", "protocol.file.allow=always", "submodule", "add", inner.Path.Replace('\\', '/'), "mysub");
        super.Stage().Commit("add submodule");

        var cloneDir = TestScratch.NewDirectory("historia-submodule-clone-");
        super.Git(
            new Dictionary<string, string> { ["GIT_ALLOW_PROTOCOL"] = "file" },
            "-c", "protocol.file.allow=always", "clone", "--recurse-submodules", super.Path, cloneDir);

        var lsTree = super.Git("ls-tree", "HEAD", "mysub").Trim();
        var parts = lsTree.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var registeredSha = parts[2];

        var subPath = Path.Combine(cloneDir, "mysub");
        await using var subCtx = _host.Open(subPath);
        var info = await subCtx.Gateway.GetRepositoryInfoAsync();

        Assert.True(info.IsHeadDetached);
        Assert.Equal(new GitObjectId(registeredSha), info.HeadCommitId);
        var normGitDir = info.GitDirectory.Replace('\\', '/');
        Assert.EndsWith(".git/modules/mysub", normGitDir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Submodule_in_linked_worktree_is_detached()
    {
        using var inner = TestRepositoryBuilder.Create();
        inner.WriteFile("sub.txt", "sub1").Stage().Commit("sub initial");

        using var super = TestRepositoryBuilder.Create();
        super.Git("config", "protocol.file.allow", "always");
        super.WriteFile("readme.txt", "super").Stage().Commit("super initial");
        super.Git(
            new Dictionary<string, string> { ["GIT_ALLOW_PROTOCOL"] = "file" },
            "-c", "protocol.file.allow=always", "submodule", "add", inner.Path.Replace('\\', '/'), "mysub");
        super.Stage().Commit("add submodule");

        var wtDir = TestScratch.NewDirectory("historia-super-wt-");
        super.Git("worktree", "add", wtDir, "HEAD");
        super.Git(
            new Dictionary<string, string> { ["GIT_ALLOW_PROTOCOL"] = "file" },
            "-C", wtDir, "-c", "protocol.file.allow=always", "submodule", "update", "--init");

        var lsTree = super.Git("ls-tree", "HEAD", "mysub").Trim();
        var parts = lsTree.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var registeredSha = parts[2];

        var subPath = Path.Combine(wtDir, "mysub");
        await using var subCtx = _host.Open(subPath);
        var info = await subCtx.Gateway.GetRepositoryInfoAsync();

        Assert.True(info.IsHeadDetached);
        Assert.Equal(new GitObjectId(registeredSha), info.HeadCommitId);
        var normGitDir = info.GitDirectory.Replace('\\', '/');
        Assert.Contains("/worktrees/", normGitDir, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("/modules/mysub", normGitDir, StringComparison.OrdinalIgnoreCase);
    }
}
