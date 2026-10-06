namespace Historia.Tests;

/// <summary>Stale lock files: which ones are offered, that removal re-checks each file and stays
/// inside the qualifying set, that it backs off while the repository is busy, and that a fetch
/// broken by a leftover ref lock works again afterwards.</summary>
[Trait("tier", "git")]
public sealed class RepoLockFilesTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        foreach (var r in _repos)
            r.Dispose();
    }

    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly DateTime Old = Now - TimeSpan.FromMinutes(10);

    private TestRepositoryBuilder NewRepo(string key)
    {
        var b = TestRepositoryBuilder.FromTemplate(key, tpl =>
        {
            tpl.WriteFile("a.txt", "x\n").Stage();
            tpl.Commit("c1");
        });
        _repos.Add(b);
        return b;
    }

    private static string Touch(string repoPath, string relative, DateTime writtenUtc)
    {
        var path = Path.Combine(repoPath, ".git", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    [Fact]
    public void Find_offers_only_old_qualifying_locks()
    {
        var repo = NewRepo("lockfiles-find");
        var ctx = _host.Open(repo.Path);

        Touch(repo.Path, "refs/remotes/origin/feature/a.lock", Old);
        Touch(repo.Path, "packed-refs.lock", Old);
        Touch(repo.Path, "index.lock", Old);
        Touch(repo.Path, "HEAD.lock", Old);
        Touch(repo.Path, "config.lock", Old);
        Touch(repo.Path, "refs/heads/fresh.lock", Now - TimeSpan.FromMinutes(1));
        Touch(repo.Path, "objects/info/commit-graph.lock", Old);
        Touch(repo.Path, "logs/HEAD.lock", Old);
        File.WriteAllText(Path.Combine(repo.Path, "outside.lock"), "");
        File.SetLastWriteTimeUtc(Path.Combine(repo.Path, "outside.lock"), Old);

        var found = RepoLockFiles.Find(ctx, Now).Select(f => f.RelativePath).ToArray();

        Assert.Equal(
            ["HEAD.lock", "config.lock", "index.lock", "packed-refs.lock", "refs/remotes/origin/feature/a.lock"],
            found);
    }

    [Fact]
    public void Find_returns_nothing_for_a_clean_repository()
    {
        var repo = NewRepo("lockfiles-clean");
        var ctx = _host.Open(repo.Path);
        Assert.Empty(RepoLockFiles.Find(ctx, Now));
    }

    [Fact(Timeout = 30000)]
    public async Task Remove_deletes_the_found_locks_and_skips_one_touched_since()
    {
        var repo = NewRepo("lockfiles-remove");
        var ctx = _host.Open(repo.Path);
        var stale = Touch(repo.Path, "refs/remotes/origin/x.lock", Old);
        var touched = Touch(repo.Path, "refs/remotes/origin/y.lock", Old);

        var found = RepoLockFiles.Find(ctx, DateTime.UtcNow);
        Assert.Equal(2, found.Count);
        File.SetLastWriteTimeUtc(touched, DateTime.UtcNow);

        var result = await RepoLockFiles.RemoveStaleLocksAsync(ctx, found);

        Assert.False(result.Busy);
        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Skipped);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(touched));
        Assert.Null(ctx.CurrentNetworkOperation);
    }

    [Fact(Timeout = 30000)]
    public async Task Remove_never_deletes_a_path_outside_the_qualifying_set()
    {
        var repo = NewRepo("lockfiles-outside");
        var ctx = _host.Open(repo.Path);
        var outside = Path.Combine(repo.Path, "a.txt");
        var forged = new StaleLockFile(outside, "a.txt", File.GetLastWriteTimeUtc(outside));
        File.SetLastWriteTimeUtc(outside, Old);
        forged = forged with { LastWriteUtc = Old };

        var result = await RepoLockFiles.RemoveStaleLocksAsync(ctx, [forged]);

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(outside));
    }

    [Fact(Timeout = 30000)]
    public async Task Remove_backs_off_while_another_network_operation_holds_the_repository()
    {
        var repo = NewRepo("lockfiles-busy");
        var ctx = _host.Open(repo.Path);
        var stale = Touch(repo.Path, "refs/remotes/origin/x.lock", Old);
        var found = RepoLockFiles.Find(ctx, DateTime.UtcNow);

        Assert.True(ctx.TryBeginNetworkOperation(new NetworkOperationInfo(NetworkOperationOwner.AutoFetch, "Auto-fetch"), out var held));
        using (held)
        {
            var result = await RepoLockFiles.RemoveStaleLocksAsync(ctx, found);
            Assert.True(result.Busy);
            Assert.Equal("Auto-fetch", result.BusyWith?.Label);
            Assert.Equal(0, result.Removed);
        }
        Assert.True(File.Exists(stale));
    }

    [Fact(Timeout = 60000)]
    public async Task A_fetch_broken_by_a_leftover_ref_lock_works_after_removal()
    {
        var remote = NewRepo("lockfiles-remote");
        remote.Git("branch", "gone");
        var local = TestRepositoryBuilder.Create();
        _repos.Add(local);
        local.Git("remote", "add", "origin", remote.Path);
        local.Git("fetch", "origin");

        // The remote branch is deleted; pruning it needs refs/remotes/origin/gone.lock, which a
        // killed git process left behind.
        remote.Git("branch", "-D", "gone");
        Touch(local.Path, "refs/remotes/origin/gone.lock", Old);

        var failure = Assert.Throws<InvalidOperationException>(() => local.Git("fetch", "--prune", "origin"));
        Assert.Equal(GitErrorKind.LockedIndex, GitErrorClassifier.Classify(1, failure.Message));

        var ctx = _host.Open(local.Path);
        var found = RepoLockFiles.Find(ctx, DateTime.UtcNow);
        Assert.Equal("refs/remotes/origin/gone.lock", Assert.Single(found).RelativePath);
        var result = await RepoLockFiles.RemoveStaleLocksAsync(ctx, found);
        Assert.Equal(1, result.Removed);

        local.Git("fetch", "--prune", "origin");
        Assert.DoesNotContain("origin/gone", local.Git("branch", "-r"));
    }
}
