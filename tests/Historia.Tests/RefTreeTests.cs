namespace Historia.Tests;

/// <summary>the pure sidebar ref-tree builder.</summary>
[Trait("tier", "unit")]
public class RefTreeTests
{
    private static BranchInfo B(string name) => new()
    {
        Name = name,
        CanonicalName = "refs/heads/" + name,
        IsRemote = false,
        IsCurrent = false,
        TipCommitId = null,
    };

    [Fact]
    public void Nests_slash_separated_segments()
    {
        var tree = RefTree.Build([B("feature/auth/login"), B("feature/auth/logout")], b => b.Name);

        var feature = Assert.Single(tree);
        Assert.Equal("feature", feature.Segment);
        Assert.True(feature.IsFolder);
        var auth = Assert.Single(feature.Children);
        Assert.Equal("feature/auth", auth.FullPath);
        Assert.Equal(2, auth.Children.Count);
        Assert.All(auth.Children, c => Assert.False(c.IsFolder));
        Assert.Equal("login", auth.Children[0].Segment); // ordinal within the leaf kind
    }

    [Fact]
    public void Sorts_flat_alphabetically_without_hoisting_folders()
    {
        // "aaa" is a folder, the rest are leaves — the order is purely alphabetical
        // (case-insensitive), folders do not jump to the top.
        var tree = RefTree.Build([B("zzz"), B("aaa/child"), B("main"), B("Bar/x")], b => b.Name);

        Assert.Equal(new[] { "aaa", "Bar", "main", "zzz" }, tree.Select(n => n.Segment).ToArray());
    }

    [Fact]
    public void A_name_that_is_both_a_folder_and_a_branch_becomes_a_folder_with_itself_first()
    {
        var tree = RefTree.Build([B("feature"), B("feature/x")], b => b.Name);

        var feature = Assert.Single(tree);
        Assert.True(feature.IsFolder);
        Assert.Equal(2, feature.Children.Count);
        Assert.False(feature.Children[0].IsFolder);          // the branch itself, first
        Assert.Equal("feature", feature.Children[0].Segment);
        Assert.Equal("x", feature.Children[1].Segment);
    }

    [Fact]
    public void Single_segment_names_are_plain_leaves()
    {
        var tree = RefTree.Build([B("main"), B("dev")], b => b.Name);
        Assert.Equal(2, tree.Count);
        Assert.All(tree, n => Assert.False(n.IsFolder));
    }

    [Fact]
    public void Path_selector_can_strip_a_remote_prefix()
    {
        var remote = new BranchInfo
        {
            Name = "origin/feature/x", CanonicalName = "refs/remotes/origin/feature/x",
            IsRemote = true, IsCurrent = false, TipCommitId = null, RemoteName = "origin",
        };
        var tree = RefTree.Build([remote], b => b.Name["origin/".Length..]);

        var feature = Assert.Single(tree);
        Assert.Equal("feature", feature.Segment);
        Assert.Equal("x", Assert.Single(feature.Children).Segment);
    }
}
