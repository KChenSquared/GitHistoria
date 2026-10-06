namespace HistoriaApp.Tests;

/// <summary>Pure helpers for network action targeting — refspec construction and upstream checking.</summary>
[Trait("tier", "unit")]
public sealed class RepoNetworkTargetingTests
{
    [Fact]
    public void BuildPushSpec_uses_colon_notation_when_names_differ()
    {
        var spec = RepoNetworkActionRunner.BuildPushSpec("feature", "feat-x", targetHasUpstream: true);
        Assert.Equal("feature:feat-x", spec.RefSpec);
        Assert.False(spec.SetUpstream);
    }

    [Fact]
    public void BuildPushSpec_uses_single_branch_name_when_names_equal_or_remote_null()
    {
        var specEqual = RepoNetworkActionRunner.BuildPushSpec("feature", "feature", targetHasUpstream: true);
        Assert.Equal("feature", specEqual.RefSpec);

        var specNull = RepoNetworkActionRunner.BuildPushSpec("feature", null, targetHasUpstream: true);
        Assert.Equal("feature", specNull.RefSpec);
    }

    [Fact]
    public void BuildPushSpec_requests_upstream_when_target_lacks_one()
    {
        var spec = RepoNetworkActionRunner.BuildPushSpec("feature", "feat-x", targetHasUpstream: false);
        Assert.Equal("feature:feat-x", spec.RefSpec);
        Assert.True(spec.SetUpstream);
    }

    [Fact]
    public void SetUpstream_follows_the_target_not_the_current_branch()
    {
        BranchInfo[] branches =
        [
            new()
            {
                Name = "main",
                CanonicalName = "refs/heads/main",
                IsRemote = false,
                IsCurrent = true,
                UpstreamName = null,
            },
            new()
            {
                Name = "feature",
                CanonicalName = "refs/heads/feature",
                IsRemote = false,
                IsCurrent = false,
                UpstreamName = "origin/feat-x",
            },
        ];

        // Current 'main' has no upstream, but target 'feature' does:
        var targetHasUpstream = RepoNetworkActionRunner.TargetHasUpstream(branches, "feature");
        Assert.True(targetHasUpstream);

        var spec = RepoNetworkActionRunner.BuildPushSpec("feature", "feat-x", targetHasUpstream);
        Assert.False(spec.SetUpstream);

        // Target 'main' has no upstream:
        var mainHasUpstream = RepoNetworkActionRunner.TargetHasUpstream(branches, "main");
        Assert.False(mainHasUpstream);

        var mainSpec = RepoNetworkActionRunner.BuildPushSpec("main", null, mainHasUpstream);
        Assert.True(mainSpec.SetUpstream);
    }

    [Fact]
    public void TargetHasUpstream_returns_false_for_an_unknown_branch()
    {
        BranchInfo[] branches =
        [
            new()
            {
                Name = "main",
                CanonicalName = "refs/heads/main",
                IsRemote = false,
                IsCurrent = true,
                UpstreamName = "origin/main",
            },
        ];

        Assert.False(RepoNetworkActionRunner.TargetHasUpstream(branches, "nonexistent"));
    }

    [Fact]
    public void PruneEffect_names_the_remote_and_scope()
    {
        var text = RepoNetworkActionRunner.PruneEffect("origin");

        Assert.Contains("'origin/'", text);
        Assert.Contains("on origin", text);
        Assert.Contains("local branches are not touched", text);
        Assert.DoesNotContain("Are you sure", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TargetHasUpstream_ignores_remote_branches()
    {
        BranchInfo[] branches =
        [
            new()
            {
                Name = "origin/feature",
                CanonicalName = "refs/remotes/origin/feature",
                IsRemote = true,
                IsCurrent = false,
                UpstreamName = "origin/feature",
            },
        ];

        Assert.False(RepoNetworkActionRunner.TargetHasUpstream(branches, "origin/feature"));
    }

    [Fact]
    public void RemoteBranchDeleteEffect_names_ref_and_keeps_local()
    {
        var text = RepoNetworkActionRunner.RemoteBranchDeleteEffect("origin", "feat");

        Assert.Contains("origin/feat", text);
        Assert.Contains("on origin", text);
        Assert.Contains("local branch", text);
        Assert.Contains("kept", text);
        Assert.DoesNotContain("Are you sure", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpLabel_is_readable_for_delete_and_unchanged_for_others()
    {
        Assert.Equal("Delete remote branch", RepoNetworkActionRunner.OpLabel(RepoNetworkAction.DeleteRemoteBranch));
        Assert.Equal("Fetch", RepoNetworkActionRunner.OpLabel(RepoNetworkAction.Fetch));
        Assert.Equal("Pull", RepoNetworkActionRunner.OpLabel(RepoNetworkAction.Pull));
        Assert.Equal("Push", RepoNetworkActionRunner.OpLabel(RepoNetworkAction.Push));
        Assert.Equal("Prune", RepoNetworkActionRunner.OpLabel(RepoNetworkAction.Prune));
    }
}

