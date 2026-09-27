namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class RepoConfigReaderTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    private IGitCliService Cli => _host.Provider.GetRequiredService<IGitCliService>();

    [Fact]
    public async Task No_remotes_returns_empty_list()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("empty-remotes", r =>
            r.WriteFile("a", "1").Stage().Commit("c1"));

        var reader = new RepoConfigReader(Cli, Path.Combine(repo.Path, ".git"), repo.Path);
        var remotes = await reader.GetRemotesAsync();

        Assert.Empty(remotes);
    }

    [Fact]
    public async Task Remote_names_with_slashes_and_missing_pushurl_match_libgit2()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("slash-remotes", r =>
        {
            r.WriteFile("a", "1").Stage().Commit("c1");
            r.Git("remote", "add", "vendor/upstream", "https://example.com/vendor/repo.git");
            r.Git("remote", "add", "origin", "https://example.com/main/repo.git");
        });

        var reader = new RepoConfigReader(Cli, Path.Combine(repo.Path, ".git"), repo.Path);
        var remotes = await reader.GetRemotesAsync();

        Assert.Equal(2, remotes.Count);
        Assert.Equal("vendor/upstream", remotes[0].Name);
        Assert.Equal("https://example.com/vendor/repo.git", remotes[0].Url);
        Assert.Equal("https://example.com/vendor/repo.git", remotes[0].PushUrl);

        Assert.Equal("origin", remotes[1].Name);
        Assert.Equal("https://example.com/main/repo.git", remotes[1].Url);
        Assert.Equal("https://example.com/main/repo.git", remotes[1].PushUrl);

        // Parity with LibGit2Sharp directly.
        using var git = new LibGit2Sharp.Repository(repo.Path);
        var libUpstream = git.Network.Remotes["vendor/upstream"];
        Assert.NotNull(libUpstream);
        Assert.Equal(libUpstream.Url, remotes[0].Url);
        Assert.Equal(libUpstream.PushUrl, remotes[0].PushUrl);
    }

    [Fact]
    public async Task InsteadOf_and_pushInsteadOf_parity_with_libgit2()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("insteadof-remotes", r =>
        {
            r.WriteFile("a", "1").Stage().Commit("c1");
            r.Git("config", "url.https://github.com/.insteadOf", "gh:");
            r.Git("config", "url.git@github.com:.pushInsteadOf", "gh:");
            r.Git("remote", "add", "origin", "gh:org/project.git");
        });

        var reader = new RepoConfigReader(Cli, Path.Combine(repo.Path, ".git"), repo.Path);
        var remotes = await reader.GetRemotesAsync();

        var origin = Assert.Single(remotes);
        Assert.Equal("origin", origin.Name);

        // Compare directly to LibGit2Sharp.
        using var git = new LibGit2Sharp.Repository(repo.Path);
        var libOrigin = git.Network.Remotes["origin"];
        Assert.NotNull(libOrigin);

        Assert.Equal("https://github.com/org/project.git", origin.Url);
        Assert.Equal("git@github.com:org/project.git", origin.PushUrl);
        Assert.Equal(libOrigin.Url, origin.Url);
        Assert.Equal(libOrigin.PushUrl, origin.PushUrl);

        // Now set explicit pushurl and verify pushInsteadOf rewrite.
        repo.Git("config", "remote.origin.pushurl", "gh:org/project.git");
        var configPath = Path.Combine(repo.Path, ".git", "config");
        File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow.AddSeconds(1));

        var remotesWithPush = await reader.GetRemotesAsync();
        var originWithPush = Assert.Single(remotesWithPush);
        Assert.Equal("https://github.com/org/project.git", originWithPush.Url);
        Assert.Equal("https://github.com/org/project.git", originWithPush.PushUrl);

        using var gitWithPush = new LibGit2Sharp.Repository(repo.Path);
        var libOriginWithPush = gitWithPush.Network.Remotes["origin"];
        Assert.Equal(libOriginWithPush.Url, originWithPush.Url);
        Assert.Equal(libOriginWithPush.PushUrl, originWithPush.PushUrl);
    }

    [Fact]
    public async Task Stamp_hit_avoids_reread_and_miss_triggers_reread()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("stamp-remotes", r =>
        {
            r.WriteFile("a", "1").Stage().Commit("c1");
            r.Git("remote", "add", "origin", "https://example.com/repo.git");
        });

        var gitDir = Path.Combine(repo.Path, ".git");
        var reader = new RepoConfigReader(Cli, gitDir, repo.Path);

        var first = await reader.GetRemotesAsync();
        Assert.Single(first);
        Assert.Equal(1, reader.ReadCountForTesting);

        // Second call without touching config should hit cache.
        var second = await reader.GetRemotesAsync();
        Assert.Same(first, second);
        Assert.Equal(1, reader.ReadCountForTesting);

        // Modify config by adding another remote.
        repo.Git("remote", "add", "backup", "https://example.com/backup.git");
        // Ensure mtime ticks forward.
        var configPath = Path.Combine(gitDir, "config");
        File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow.AddSeconds(1));

        var third = await reader.GetRemotesAsync();
        Assert.Equal(2, third.Count);
        Assert.Equal(2, reader.ReadCountForTesting);
    }

    [Fact]
    public void Linked_worktree_resolves_common_dir()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("worktree-seed", r =>
            r.WriteFile("a", "1").Stage().Commit("c1"));

        repo.Git("worktree", "add", "wt1", "-b", "branch1");

        var wtPath = Path.GetFullPath(Path.Combine(repo.Path, "wt1"));
        try
        {
            var wtGitDir = File.ReadAllText(Path.Combine(wtPath, ".git")).Trim();
            if (wtGitDir.StartsWith("gitdir: ", StringComparison.OrdinalIgnoreCase))
                wtGitDir = wtGitDir[8..].Trim();
            if (!Path.IsPathRooted(wtGitDir))
                wtGitDir = Path.GetFullPath(Path.Combine(wtPath, wtGitDir));

            var commonDir = RepoConfigReader.ResolveCommonDir(wtGitDir);
            var expectedCommon = Path.GetFullPath(Path.Combine(repo.Path, ".git"));
            Assert.Equal(expectedCommon, commonDir);
        }
        finally
        {
            if (Directory.Exists(wtPath))
            {
                try { repo.Git("worktree", "remove", "--force", "wt1"); } catch { }
            }
        }
    }
}
