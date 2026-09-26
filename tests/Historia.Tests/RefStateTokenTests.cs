namespace Historia.Tests;

/// <summary>
/// <see cref="RefStateToken"/> is what lets the ref snapshot be reused, so it must move on every
/// kind of ref write — a missed one would leave both the graph and the tag list stale — and
/// stay put when nothing changed.
/// </summary>
[Trait("tier", "git")]
public sealed class RefStateTokenTests : IDisposable
{
    private readonly TestRepositoryBuilder _repo;
    private readonly string _gitDir;

    public RefStateTokenTests()
    {
        _repo = TestRepositoryBuilder.FromTemplate("linear-5", b => b.LinearHistory(5));
        _gitDir = Path.Combine(_repo.Path, ".git");
    }

    public void Dispose() => _repo.Dispose();

    private long Token() => RefStateToken.Compute(_gitDir).Token;

    [Fact]
    public void Unchanged_refs_give_the_same_token() =>
        Assert.Equal(Token(), Token());

    [Fact]
    public void Loose_ref_write_changes_the_token()
    {
        var before = Token();
        _repo.Git("update-ref", "refs/heads/feature", "HEAD~1");
        Assert.NotEqual(before, Token());
    }

    [Fact]
    public void Pack_refs_and_deleting_a_packed_tag_change_the_token()
    {
        _repo.Git("tag", "v1", "HEAD~2");
        var loose = Token();

        _repo.Git("pack-refs", "--all");
        var packed = Token();
        Assert.NotEqual(loose, packed);

        _repo.Git("tag", "-d", "v1");
        Assert.NotEqual(packed, Token());
    }

    [Fact]
    public void Checkout_changes_the_token_and_head()
    {
        _repo.Git("branch", "other", "HEAD~1");
        var before = RefStateToken.Compute(_gitDir);
        Assert.Equal("refs/heads/main", before.HeadSymref);

        _repo.Git("checkout", "-q", "other");
        var after = RefStateToken.Compute(_gitDir);
        Assert.NotEqual(before.Token, after.Token);
        Assert.Equal("refs/heads/other", after.HeadSymref);

        _repo.Git("checkout", "-q", "--detach", "HEAD");
        var detached = RefStateToken.Compute(_gitDir);
        Assert.Null(detached.HeadSymref);
        Assert.Equal(_repo.HeadSha, detached.HeadSha);
    }

    [Fact]
    public void Stash_push_changes_the_token()
    {
        var before = Token();
        _repo.WriteFile("f.txt", "dirty\n");
        _repo.Git("stash", "push", "-q");
        Assert.NotEqual(before, Token());
    }

    [Fact]
    public void Linked_worktree_sees_shared_refs_and_its_own_head()
    {
        var wtPath = TestScratch.NewDirectory("historia-wt-");
        Directory.Delete(wtPath);
        _repo.Git("worktree", "add", "-q", "-b", "wt-branch", wtPath, "HEAD~1");
        var wtGitDir = _repo.Git("-C", wtPath, "rev-parse", "--absolute-git-dir").Trim();

        Assert.Equal(
            Path.GetFullPath(_gitDir).TrimEnd('\\', '/'),
            RefStateToken.ResolveCommonDir(wtGitDir).TrimEnd('\\', '/'),
            ignoreCase: true);

        var start = RefStateToken.Compute(wtGitDir);
        Assert.Equal("refs/heads/wt-branch", start.HeadSymref);

        // A branch moved from the main worktree is a shared ref.
        _repo.Git("update-ref", "refs/heads/shared", "HEAD");
        var afterShared = RefStateToken.Compute(wtGitDir).Token;
        Assert.NotEqual(start.Token, afterShared);

        // The linked worktree's own HEAD.
        _repo.Git("-C", wtPath, "checkout", "-q", "--detach");
        Assert.NotEqual(afterShared, RefStateToken.Compute(wtGitDir).Token);
    }
}
