namespace Historia.Tests;

public sealed class IndexCaseCollisionsTests
{
    [Fact]
    public void ParseGroups_groups_colliding_paths_and_leaves_singletons_out()
    {
        var output =
            "100644 1111111111111111111111111111111111111111 0\tFoo.txt\0" +
            "100644 2222222222222222222222222222222222222222 0\tfoo.txt\0" +
            "100644 3333333333333333333333333333333333333333 0\tbar.txt\0";

        var groups = IndexCaseCollisions.ParseGroups(output);

        Assert.Equal(2, groups.Count);
        Assert.False(groups.ContainsKey("bar.txt"));

        var fooPeers = groups["Foo.txt"];
        var singleFooPeer = Assert.Single(fooPeers);
        Assert.Equal("foo.txt", singleFooPeer.Path);
        Assert.Equal("2222222222222222222222222222222222222222", singleFooPeer.BlobId);

        var lowerFooPeers = groups["foo.txt"];
        var singleLowerFooPeer = Assert.Single(lowerFooPeers);
        Assert.Equal("Foo.txt", singleLowerFooPeer.Path);
        Assert.Equal("1111111111111111111111111111111111111111", singleLowerFooPeer.BlobId);
    }

    [Fact]
    public void ParseGroups_skips_non_zero_stages()
    {
        var output =
            "100644 1111111111111111111111111111111111111111 1\tFoo.txt\0" +
            "100644 2222222222222222222222222222222222222222 2\tfoo.txt\0" +
            "100644 3333333333333333333333333333333333333333 3\tFOO.txt\0";

        var groups = IndexCaseCollisions.ParseGroups(output);

        Assert.Empty(groups);
    }

    [Fact]
    public void ParseGroups_handles_paths_with_spaces_and_tabs()
    {
        var output =
            "100644 1111111111111111111111111111111111111111 0\tfolder name/foo\tbar.txt\0" +
            "100644 2222222222222222222222222222222222222222 0\tFOLDER NAME/FOO\tBAR.TXT\0";

        var groups = IndexCaseCollisions.ParseGroups(output);

        Assert.Equal(2, groups.Count);
        Assert.True(groups.ContainsKey("folder name/foo\tbar.txt"));
        Assert.True(groups.ContainsKey("FOLDER NAME/FOO\tBAR.TXT"));

        var peers = groups["folder name/foo\tbar.txt"];
        var peer = Assert.Single(peers);
        Assert.Equal("FOLDER NAME/FOO\tBAR.TXT", peer.Path);
        Assert.Equal("2222222222222222222222222222222222222222", peer.BlobId);
    }

    [Fact]
    public void ParseGroups_three_way_group_lists_other_two_as_peers_of_each_member()
    {
        var output =
            "100644 1111111111111111111111111111111111111111 0\tdir/a\0" +
            "100644 2222222222222222222222222222222222222222 0\tDIR/A\0" +
            "100644 3333333333333333333333333333333333333333 0\tDir/a\0";

        var groups = IndexCaseCollisions.ParseGroups(output);

        Assert.Equal(3, groups.Count);

        var peers1 = groups["dir/a"];
        Assert.Equal(2, peers1.Count);
        Assert.Contains(peers1, p => p.Path == "DIR/A" && p.BlobId == "2222222222222222222222222222222222222222");
        Assert.Contains(peers1, p => p.Path == "Dir/a" && p.BlobId == "3333333333333333333333333333333333333333");

        var peers2 = groups["DIR/A"];
        Assert.Equal(2, peers2.Count);
        Assert.Contains(peers2, p => p.Path == "dir/a" && p.BlobId == "1111111111111111111111111111111111111111");
        Assert.Contains(peers2, p => p.Path == "Dir/a" && p.BlobId == "3333333333333333333333333333333333333333");

        var peers3 = groups["Dir/a"];
        Assert.Equal(2, peers3.Count);
        Assert.Contains(peers3, p => p.Path == "dir/a" && p.BlobId == "1111111111111111111111111111111111111111");
        Assert.Contains(peers3, p => p.Path == "DIR/A" && p.BlobId == "2222222222222222222222222222222222222222");
    }
}
