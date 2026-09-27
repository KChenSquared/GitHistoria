namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class BranchProjectionParityTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Local_remote_tracking_detached_unborn_parity_with_libgit2()
    {
        using var builder = TestRepositoryBuilder.FromTemplate("branch-parity-basic", b =>
        {
            b.WriteFile("f.txt", "1").Stage().Commit("initial");
            b.Git("branch", "feature");
            b.Git("checkout", "feature");
            b.WriteFile("f.txt", "2").Stage().Commit("feature commit");
            b.Git("checkout", "main");
            b.Git("remote", "add", "origin", b.Path);
            b.Git("fetch", "origin");
            b.Git("branch", "--set-upstream-to=origin/main", "main");
        });

        await using var ctx = _host.Open(builder.Path);
        var actual = await ctx.Gateway.GetBranchesAsync();

        using var repo = new Repository(builder.Path);
        var expected = repo.Branches
            .Select(b => new BranchInfo
            {
                Name = b.FriendlyName,
                CanonicalName = b.CanonicalName,
                IsRemote = b.IsRemote,
                RemoteName = SafeRemoteName(b),
                IsCurrent = b.IsCurrentRepositoryHead,
                TipCommitId = b.Tip?.Id.ToGitObjectId(),
                UpstreamName = b.TrackedBranch?.FriendlyName,
            })
            .OrderBy(b => b.CanonicalName, StringComparer.Ordinal)
            .ToList();

        var sortedActual = actual.OrderBy(b => b.CanonicalName, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, sortedActual);
    }

    [Fact]
    public async Task Detached_head_has_no_current_branch()
    {
        using var builder = TestRepositoryBuilder.FromTemplate("branch-parity-detached", b =>
        {
            b.WriteFile("f.txt", "1").Stage().Commit("initial");
            b.WriteFile("f.txt", "2").Stage().Commit("second");
            b.Git("checkout", "HEAD~1");
        });

        await using var ctx = _host.Open(builder.Path);
        var branches = await ctx.Gateway.GetBranchesAsync();

        Assert.DoesNotContain(branches, b => b.IsCurrent);
    }

    [Fact]
    public async Task Unborn_head_has_empty_branch_list()
    {
        using var builder = TestRepositoryBuilder.Create();

        await using var ctx = _host.Open(builder.Path);
        var branches = await ctx.Gateway.GetBranchesAsync();

        Assert.Empty(branches);
    }

    [Fact]
    public async Task Remote_name_with_slash_projects_correct_remote_name()
    {
        using var builder = TestRepositoryBuilder.FromTemplate("branch-parity-remote-slash", b =>
        {
            b.WriteFile("f.txt", "1").Stage().Commit("initial");
            b.Git("remote", "add", "team/infra", b.Path);
            b.Git("fetch", "team/infra");
        });

        await using var ctx = _host.Open(builder.Path);
        var branches = await ctx.Gateway.GetBranchesAsync();

        var remoteBranch = Assert.Single(branches, b => b.IsRemote && b.CanonicalName.Contains("team/infra/main"));
        Assert.Equal("team/infra", remoteBranch.RemoteName);
        Assert.Equal("team/infra/main", remoteBranch.Name);
    }

    [Fact]
    public async Task Orphan_remote_tracking_ref_without_configured_remote()
    {
        using var builder = TestRepositoryBuilder.FromTemplate("branch-parity-orphan-remote", b =>
        {
            b.WriteFile("f.txt", "1").Stage().Commit("initial");
            b.Git("update-ref", "refs/remotes/orphan/feat", "HEAD");
        });

        await using var ctx = _host.Open(builder.Path);
        var branches = await ctx.Gateway.GetBranchesAsync();

        var orphan = Assert.Single(branches, b => b.CanonicalName == "refs/remotes/orphan/feat");
        Assert.True(orphan.IsRemote);
        Assert.Equal("orphan", orphan.RemoteName);
        Assert.Equal("orphan/feat", orphan.Name);
    }

    [Fact]
    public async Task Two_thousand_remote_branches_match_libgit2()
    {
        using var builder = TestRepositoryBuilder.FromTemplate("branch-parity-2k", b =>
        {
            b.WriteFile("f.txt", "1").Stage().Commit("initial");
            var headSha = b.HeadSha;
            var sb = new StringBuilder();
            sb.Append("# pack-refs with: peeled-tags fully-peeled sorted\n");
            for (var i = 0; i < 2000; i++)
            {
                sb.Append($"{headSha} refs/remotes/origin/br-{i:D4}\n");
            }
            File.WriteAllText(Path.Combine(b.Path, ".git", "packed-refs"), sb.ToString());
        });

        await using var ctx = _host.Open(builder.Path);
        var actual = await ctx.Gateway.GetBranchesAsync();

        using var repo = new Repository(builder.Path);
        Assert.Equal(repo.Branches.Count(), actual.Count);
    }

    [Fact]
    public async Task SetUpstreamAsync_followed_by_GetBranchesAsync_shows_new_upstream_without_ref_movement()
    {
        using var builder = TestRepositoryBuilder.FromTemplate("branch-parity-setupstream", b =>
        {
            b.WriteFile("f.txt", "1").Stage().Commit("initial");
            b.Git("remote", "add", "origin", b.Path);
            b.Git("fetch", "origin");
        });

        await using var ctx = _host.Open(builder.Path);

        var before = await ctx.Gateway.GetBranchesAsync();
        var mainBefore = Assert.Single(before, b => b.Name == "main" && !b.IsRemote);
        Assert.Null(mainBefore.UpstreamName);

        await ctx.Gateway.SetUpstreamAsync("main", "origin/main");

        var after = await ctx.Gateway.GetBranchesAsync();
        var mainAfter = Assert.Single(after, b => b.Name == "main" && !b.IsRemote);
        Assert.Equal("origin/main", mainAfter.UpstreamName);
    }

    private static string? SafeRemoteName(Branch branch)
    {
        if (!branch.IsRemote)
            return null;
        try { return branch.RemoteName; }
        catch { return null; }
    }
}
