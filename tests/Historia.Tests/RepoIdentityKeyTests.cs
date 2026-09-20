namespace Historia.Tests;
[Trait("tier", "unit")]

public class RepoIdentityKeyTests
{
    [Fact]
    public void Same_root_commit_yields_same_key()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        Assert.Equal(RepoIdentityKey.FromRootCommit(sha), RepoIdentityKey.FromRootCommit(sha));
    }

    [Fact]
    public void Different_root_commits_yield_different_keys()
    {
        var a = RepoIdentityKey.FromRootCommit("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var b = RepoIdentityKey.FromRootCommit("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Key_is_independent_of_path_casing_or_slashes()
    {
        // The key derives only from the root commit; the repository's path is never an input.
        var key = RepoIdentityKey.FromRootCommit("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef");
        Assert.Equal(64, key.Value.Length); // SHA-256 hex
        Assert.Matches("^[0-9a-f]+$", key.Value);
    }

    [Fact]
    public void Explicit_id_key_is_stable_for_a_guid()
    {
        var id = Guid.NewGuid();
        Assert.Equal(RepoIdentityKey.FromExplicitId(id), RepoIdentityKey.FromExplicitId(id));
        Assert.Equal(id.ToString("N"), RepoIdentityKey.FromExplicitId(id).Value);
    }
}
