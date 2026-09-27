namespace Historia.Tests;

/// <summary>
/// libgit2 checkout conflicts — classification to UncommittedChanges, path list capping,
/// and atomic create-and-checkout rollback (F1–F4).
/// </summary>
[Trait("tier", "git")]
public sealed class CheckoutConflictTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Create_branch_at_head_with_a_dirty_tree_succeeds()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("f.txt", "1\n").Stage().Commit("c1");
        repo.WriteFile("other.txt", "base\n").Stage().Commit("c2");
        var headSha = repo.HeadSha;

        repo.WriteFile("f.txt", "1 modified\n");
        repo.WriteFile("staged.txt", "staged\n");
        repo.Stage("staged.txt");
        repo.WriteFile("untracked.txt", "untracked\n");

        await using var ctx = _host.Open(repo.Path);
        var info = await ctx.Gateway.CreateBranchAsync("branch-at-head", headSha, checkout: true);

        Assert.True(info.IsCurrent);
        await ctx.WithRepositoryAsync(r =>
        {
            Assert.Equal("branch-at-head", r.Head.FriendlyName);
            Assert.Equal(headSha, r.Head.Tip.Sha);
        });
    }

    [Fact]
    public async Task Create_branch_at_a_conflicting_commit_throws_uncommitted_changes()
    {
        using var repo = TestRepositoryBuilder.Create();
        var c1 = repo.WriteFile("f.txt", "1\n").WriteFile("other.txt", "c1\n").Stage().Commit("c1");
        repo.WriteFile("other.txt", "c2\n").Stage().Commit("c2");

        repo.WriteFile("other.txt", "c2 edited\n");

        await using var ctx = _host.Open(repo.Path);
        var ex = await Assert.ThrowsAsync<HistoriaGitException>(
            () => ctx.Gateway.CreateBranchAsync("conflict-branch", c1, checkout: true));

        Assert.Equal(GitErrorKind.UncommittedChanges, ex.Kind);
        Assert.Contains("other.txt", ex.Message);
        Assert.NotNull(ex.Details);
        Assert.Contains("other.txt", ex.Details);
    }

    [Fact]
    public async Task A_refused_create_and_checkout_leaves_no_branch_behind()
    {
        using var repo = TestRepositoryBuilder.Create();
        var c1 = repo.WriteFile("f.txt", "1\n").WriteFile("other.txt", "c1\n").Stage().Commit("c1");
        var c2 = repo.WriteFile("other.txt", "c2\n").Stage().Commit("c2");

        repo.WriteFile("other.txt", "c2 edited\n");

        await using var ctx = _host.Open(repo.Path);
        await Assert.ThrowsAsync<HistoriaGitException>(
            () => ctx.Gateway.CreateBranchAsync("orphan-check", c1, checkout: true));

        await ctx.WithRepositoryAsync(r =>
        {
            Assert.Null(r.Branches["orphan-check"]);
            Assert.Equal("main", r.Head.FriendlyName);
            Assert.Equal(c2, r.Head.Tip.Sha);
        });
        Assert.Equal("c2 edited\n", File.ReadAllText(Path.Combine(repo.Path, "other.txt")));
    }

    [Fact]
    public async Task A_refused_create_can_be_retried_after_the_conflict_is_cleared()
    {
        using var repo = TestRepositoryBuilder.Create();
        var c1 = repo.WriteFile("f.txt", "1\n").WriteFile("other.txt", "c1\n").Stage().Commit("c1");
        repo.WriteFile("other.txt", "c2\n").Stage().Commit("c2");

        repo.WriteFile("other.txt", "c2 edited\n");

        await using var ctx = _host.Open(repo.Path);
        await Assert.ThrowsAsync<HistoriaGitException>(
            () => ctx.Gateway.CreateBranchAsync("retry-branch", c1, checkout: true));

        repo.WriteFile("other.txt", "c2\n");

        var info = await ctx.Gateway.CreateBranchAsync("retry-branch", c1, checkout: true);
        Assert.True(info.IsCurrent);
        await ctx.WithRepositoryAsync(r =>
        {
            Assert.Equal("retry-branch", r.Head.FriendlyName);
            Assert.Equal(c1, r.Head.Tip.Sha);
        });
    }

    [Fact]
    public async Task Checkout_of_a_conflicting_branch_throws_uncommitted_changes()
    {
        using var repo = TestRepositoryBuilder.Create();
        var c1 = repo.WriteFile("f.txt", "1\n").WriteFile("other.txt", "c1\n").Stage().Commit("c1");
        var c2 = repo.WriteFile("other.txt", "c2\n").Stage().Commit("c2");
        repo.Git("branch", "target-branch", c1);

        repo.WriteFile("other.txt", "c2 edited\n");

        await using var ctx = _host.Open(repo.Path);
        var ex = await Assert.ThrowsAsync<HistoriaGitException>(
            () => ctx.Gateway.CheckoutAsync("target-branch", new GitCheckoutOptions()));

        Assert.Equal(GitErrorKind.UncommittedChanges, ex.Kind);
        Assert.Contains("other.txt", ex.Message);
        Assert.NotNull(ex.Details);
        Assert.Contains("other.txt", ex.Details);
        await ctx.WithRepositoryAsync(r =>
        {
            Assert.Equal("main", r.Head.FriendlyName);
            Assert.Equal(c2, r.Head.Tip.Sha);
        });
    }

    [Fact]
    public async Task Conflict_message_caps_the_path_list()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("f1.txt", "1\n")
            .WriteFile("f2.txt", "1\n")
            .WriteFile("f3.txt", "1\n")
            .WriteFile("f4.txt", "1\n")
            .WriteFile("f5.txt", "1\n")
            .Stage().Commit("c1");
        var c1Sha = repo.HeadSha;

        repo.WriteFile("f1.txt", "2\n")
            .WriteFile("f2.txt", "2\n")
            .WriteFile("f3.txt", "2\n")
            .WriteFile("f4.txt", "2\n")
            .WriteFile("f5.txt", "2\n")
            .Stage().Commit("c2");

        repo.WriteFile("f1.txt", "dirty1\n");
        repo.WriteFile("f2.txt", "dirty2\n");
        repo.WriteFile("f3.txt", "dirty3\n");
        repo.WriteFile("f4.txt", "dirty4\n");
        repo.WriteFile("f5.txt", "dirty5\n");

        await using var ctx = _host.Open(repo.Path);
        var ex = await Assert.ThrowsAsync<HistoriaGitException>(
            () => ctx.Gateway.CreateBranchAsync("cap-test", c1Sha, checkout: true));

        Assert.Equal(GitErrorKind.UncommittedChanges, ex.Kind);
        Assert.Contains("and 2 more", ex.Message);
        Assert.NotNull(ex.Details);
        Assert.Contains("f1.txt", ex.Details);
        Assert.Contains("f2.txt", ex.Details);
        Assert.Contains("f3.txt", ex.Details);
        Assert.Contains("f4.txt", ex.Details);
        Assert.Contains("f5.txt", ex.Details);
    }

    [Fact]
    public async Task A_non_conflicting_checkout_to_another_commit_carries_changes()
    {
        using var repo = TestRepositoryBuilder.Create();
        var c1 = repo.WriteFile("a.txt", "same\n").WriteFile("other.txt", "c1\n").Stage().Commit("c1");
        repo.WriteFile("other.txt", "c2\n").Stage().Commit("c2");

        repo.WriteFile("a.txt", "same edited\n");

        await using var ctx = _host.Open(repo.Path);
        var info = await ctx.Gateway.CreateBranchAsync("non-conflicting", c1, checkout: true);

        Assert.True(info.IsCurrent);
        await ctx.WithRepositoryAsync(r =>
        {
            Assert.Equal("non-conflicting", r.Head.FriendlyName);
            Assert.Equal(c1, r.Head.Tip.Sha);
        });
        Assert.Equal("same edited\n", File.ReadAllText(Path.Combine(repo.Path, "a.txt")));
    }
}
