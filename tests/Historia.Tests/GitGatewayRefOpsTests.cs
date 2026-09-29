namespace Historia.Tests;

/// <summary>
/// Verifies branch, tag, remote, and checkout operations through GitGateway.
/// </summary>
[Trait("tier", "git")]
public sealed class GitGatewayRefOpsTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Checkout_local_remote_tracking_detached_and_force()
    {
        using var origin = TestRepositoryBuilder.FromTemplate("origin-seed", b =>
        {
            b.WriteFile("file.txt", "remote\n").Stage().Commit("remote init");
        });

        using var local = TestRepositoryBuilder.FromTemplate("local-seed", b =>
        {
            b.WriteFile("file.txt", "local\n").Stage().Commit("local init");
        });

        var cli = _host.Cli;
        await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["remote", "add", "origin", origin.Path],
            WorkingDirectory = local.Path,
        });
        await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["fetch", "origin"],
            WorkingDirectory = local.Path,
        });

        var ctx = _host.Open(local.Path);

        // 1. Local branch checkout
        await ctx.Gateway.CreateBranchAsync("feat", "HEAD", checkout: false);
        await ctx.Gateway.CheckoutAsync("feat", new GitCheckoutOptions());
        var branches = await ctx.Gateway.GetBranchesAsync();
        Assert.True(branches.First(b => b.Name == "feat").IsCurrent);

        // 2. Remote-tracking checkout detaches HEAD at the remote tip
        await ctx.Gateway.CheckoutAsync("origin/main", new GitCheckoutOptions());
        var repoInfo = await ctx.Gateway.GetRepositoryInfoAsync();
        Assert.True(repoInfo.IsHeadDetached);

        // 3. Force checkout overwrites dirty changes
        await ctx.Gateway.CheckoutAsync("feat", new GitCheckoutOptions());
        await File.WriteAllTextAsync(Path.Combine(local.Path, "file.txt"), "dirty uncommitted\n");
        await ctx.Gateway.CheckoutAsync("main", new GitCheckoutOptions { Force = true });
        var text = await File.ReadAllTextAsync(Path.Combine(local.Path, "file.txt"));
        Assert.Equal("local", text.TrimEnd('\r', '\n'));
    }

    [Fact]
    public async Task Create_and_checkout_rollback_on_conflict()
    {
        using var repo = TestRepositoryBuilder.Create();
        var c1 = repo.WriteFile("other.txt", "c1\n").Stage().Commit("c1");
        var c2 = repo.WriteFile("other.txt", "c2\n").Stage().Commit("c2");

        // Dirty conflicting file
        repo.WriteFile("other.txt", "c2 modified\n");

        var ctx = _host.Open(repo.Path);
        await Assert.ThrowsAsync<HistoriaGitException>(async () =>
        {
            await ctx.Gateway.CreateBranchAsync("conflict-branch", c1, checkout: true);
        });

        using var git = new Repository(repo.Path);
        Assert.Null(git.Branches["conflict-branch"]);
        Assert.Equal("main", git.Head.FriendlyName);
    }

    [Fact]
    public async Task Cancelled_checkout_of_new_branch_removes_the_branch()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("a.txt", "1\n").Stage().Commit("c1");

        using var cts = new CancellationTokenSource();
        var cli = CancelOnCheckout(cts);
        var ctx = _host.Open(repo.Path, cli);

        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Gateway.CheckoutAsync(
            "HEAD", new GitCheckoutOptions { CreateBranch = true, NewBranchName = "tmp" }, cts.Token));

        AssertBranchAbsent(repo.Path, "tmp");
        AssertRollbackRequestsAreBounded(cli);
    }

    [Fact]
    public async Task Cancelled_CreateBranchAsync_with_checkout_removes_the_branch()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("a.txt", "1\n").Stage().Commit("c1");

        using var cts = new CancellationTokenSource();
        var cli = CancelOnCheckout(cts);
        var ctx = _host.Open(repo.Path, cli);

        await Assert.ThrowsAnyAsync<Exception>(() => ctx.Gateway.CreateBranchAsync(
            "tmp", "HEAD", checkout: true, cts.Token));

        AssertBranchAbsent(repo.Path, "tmp");
        AssertRollbackRequestsAreBounded(cli);
    }

    private InterceptingGitCliService CancelOnCheckout(CancellationTokenSource cts)
    {
        var cli = new InterceptingGitCliService(_host.Cli);
        cli.Intercept = request =>
        {
            if (request.Arguments.FirstOrDefault() != "checkout")
                return null;

            cts.Cancel();
            return InterceptingGitCliService.Failure(GitErrorKind.Cancelled, -1);
        };
        return cli;
    }

    private static void AssertBranchAbsent(string repoPath, string name)
    {
        using var git = new Repository(repoPath);
        Assert.Null(git.Branches[name]);
        Assert.Equal("main", git.Head.FriendlyName);
    }

    private static void AssertRollbackRequestsAreBounded(InterceptingGitCliService cli)
    {
        var deletes = cli.Requests.Where(r => r.Arguments.Take(2).SequenceEqual(["branch", "-D"])).ToList();
        Assert.Single(deletes);
        Assert.All(deletes, r => Assert.Equal(TimeSpan.FromSeconds(10), r.Timeout));
    }

    [Fact]
    public async Task Delete_branch_merged_unmerged_force_and_current()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("delete-branch-seed", b =>
        {
            b.WriteFile("a.txt", "1\n").Stage().Commit("c1");
        });

        var ctx = _host.Open(repo.Path);

        // 1. Current branch cannot be deleted
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await ctx.Gateway.DeleteBranchAsync("main", force: false);
        });

        // 2. Merged branch deletes with force: false
        await ctx.Gateway.CreateBranchAsync("merged-branch", "HEAD", checkout: false);
        await ctx.Gateway.DeleteBranchAsync("merged-branch", force: false);
        using (var git = new Repository(repo.Path))
        {
            Assert.Null(git.Branches["merged-branch"]);
        }

        // 3. Unmerged branch without upstream refuses non-force delete
        await ctx.Gateway.CreateBranchAsync("unmerged-branch", "HEAD", checkout: true);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "new.txt"), "unmerged\n");
        await ctx.Gateway.StageAsync(["new.txt"]);
        await ctx.Gateway.CommitAsync(new CommitRequest { Message = "feature commit", AuthorName = "A", AuthorEmail = "a@example.com" });
        await ctx.Gateway.CheckoutAsync("main", new GitCheckoutOptions());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await ctx.Gateway.DeleteBranchAsync("unmerged-branch", force: false);
        });

        // 4. Force delete deletes unmerged branch
        await ctx.Gateway.DeleteBranchAsync("unmerged-branch", force: true);
        using (var git = new Repository(repo.Path))
        {
            Assert.Null(git.Branches["unmerged-branch"]);
        }
    }

    [Fact]
    public async Task Rename_branch_renames_target()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("rename-branch-seed", b =>
        {
            b.WriteFile("a.txt", "1\n").Stage().Commit("c1");
        });

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.CreateBranchAsync("old-branch", "HEAD", checkout: false);
        await ctx.Gateway.RenameBranchAsync("old-branch", "new-branch");

        using var git = new Repository(repo.Path);
        Assert.Null(git.Branches["old-branch"]);
        Assert.NotNull(git.Branches["new-branch"]);
    }

    [Fact]
    public async Task Lightweight_and_annotated_tag_round_trip_vs_libgit2()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("tag-roundtrip-seed", b =>
        {
            b.WriteFile("a.txt", "1\n").Stage().Commit("c1");
        });

        var ctx = _host.Open(repo.Path);

        // 1. Lightweight tag
        var light = await ctx.Gateway.CreateTagAsync("v1.0", "HEAD", message: null);
        Assert.Equal("v1.0", light.Name);
        Assert.False(light.IsAnnotated);
        Assert.Null(light.Message);

        using (var git = new Repository(repo.Path))
        {
            var gitTag = git.Tags["v1.0"];
            Assert.NotNull(gitTag);
            Assert.False(gitTag.IsAnnotated);
            Assert.Equal(git.Head.Tip.Id.ToGitObjectId(), light.TargetCommitId);
        }

        // 2. Annotated tag
        var annot = await ctx.Gateway.CreateTagAsync("v2.0", "HEAD", "release 2.0 message");
        Assert.Equal("v2.0", annot.Name);
        Assert.True(annot.IsAnnotated);
        Assert.Equal("release 2.0 message", annot.Message?.Trim());

        using (var git = new Repository(repo.Path))
        {
            var gitTag = git.Tags["v2.0"];
            Assert.NotNull(gitTag);
            Assert.True(gitTag.IsAnnotated);
            Assert.Equal("release 2.0 message", gitTag.Annotation.Message.Trim());
            Assert.Equal(git.Head.Tip.Id.ToGitObjectId(), annot.TargetCommitId);
        }
    }

    [Fact]
    public async Task Both_tag_forms_succeed_with_tag_gpgsign_true()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("tag-gpgsign-seed", b =>
        {
            b.WriteFile("a.txt", "1\n").Stage().Commit("c1");
        });

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "tag.gpgSign", "true"],
            WorkingDirectory = repo.Path,
        });

        var ctx = _host.Open(repo.Path);

        var light = await ctx.Gateway.CreateTagAsync("v-light", "HEAD", message: null);
        Assert.NotNull(light);

        var annot = await ctx.Gateway.CreateTagAsync("v-annot", "HEAD", "annotated message");
        Assert.NotNull(annot);
    }

    [Fact]
    public async Task Checkout_of_name_existing_only_as_origin_name_creates_matching_local_tracking_branch()
    {
        using var origin = TestRepositoryBuilder.FromTemplate("origin-remote-guess", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage().Commit("c1");
        });
        origin.Git("branch", "remote-feat", "HEAD");

        using var local = TestRepositoryBuilder.FromTemplate("local-remote-guess", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage().Commit("c1");
        });

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["remote", "add", "origin", origin.Path],
            WorkingDirectory = local.Path,
        });
        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["fetch", "origin"],
            WorkingDirectory = local.Path,
        });

        var ctx = _host.Open(local.Path);
        await ctx.Gateway.CheckoutAsync("remote-feat", new GitCheckoutOptions());

        using var git = new Repository(local.Path);
        var branch = git.Branches["remote-feat"];
        Assert.NotNull(branch);
        Assert.True(branch.IsCurrentRepositoryHead);
        Assert.Equal("origin/remote-feat", branch.TrackedBranch?.FriendlyName);
    }

    [Fact]
    public async Task Branch_created_from_remote_tracking_ref_has_no_upstream()
    {
        using var origin = TestRepositoryBuilder.FromTemplate("origin-notrack", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage().Commit("c1");
        });

        using var local = TestRepositoryBuilder.FromTemplate("local-notrack", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage().Commit("c1");
        });

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["remote", "add", "origin", origin.Path],
            WorkingDirectory = local.Path,
        });
        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["fetch", "origin"],
            WorkingDirectory = local.Path,
        });

        var ctx = _host.Open(local.Path);
        var created = await ctx.Gateway.CreateBranchAsync("from-remote", "origin/main", checkout: false);
        Assert.Null(created.UpstreamName);

        using var git = new Repository(local.Path);
        Assert.Null(git.Branches["from-remote"].TrackedBranch);
    }

    [Fact]
    public async Task Remote_add_rename_with_non_default_refspec_and_remove()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("remote-ops-seed", b =>
        {
            b.WriteFile("a.txt", "1\n").Stage().Commit("c1");
        });

        var ctx = _host.Open(repo.Path);

        // 1. Add remote
        await ctx.Gateway.AddRemoteAsync("upstream", "https://example.com/upstream.git");
        var remotes = await ctx.Gateway.GetRemotesAsync();
        Assert.Contains(remotes, r => r.Name == "upstream");

        // 2. Add non-default refspec
        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "--add", "remote.upstream.fetch", "+refs/heads/*:refs/remotes/other/*"],
            WorkingDirectory = repo.Path,
        });

        // 3. Rename remote
        var unupdated = await ctx.Gateway.RenameRemoteAsync("upstream", "renamed-upstream");
        Assert.NotEmpty(unupdated);
        Assert.Contains(unupdated, r => r.Contains("other"));

        // 4. Remove remote
        await ctx.Gateway.RemoveRemoteAsync("renamed-upstream");
        var afterRemotes = await ctx.Gateway.GetRemotesAsync();
        Assert.DoesNotContain(afterRemotes, r => r.Name == "renamed-upstream");
    }

    [Fact]
    public async Task Checkout_in_repo_with_smudge_filter_runs_filter()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("smudge-seed", b =>
        {
            b.WriteFile(".gitattributes", "*.txt filter=lower\n").Stage();
            b.WriteFile("hello.txt", "HELLO\n").Stage();
            b.Commit("c1");
            b.WriteFile("hello.txt", "OTHER\n").Stage().Commit("c2");
        });

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "filter.lower.smudge", "tr A-Z a-z"],
            WorkingDirectory = repo.Path,
        });

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.CheckoutAsync("HEAD~1", new GitCheckoutOptions { Force = true });

        var content = await File.ReadAllTextAsync(Path.Combine(repo.Path, "hello.txt"));
        Assert.Equal("hello", content.Trim());
    }
}
