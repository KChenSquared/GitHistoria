namespace Historia.Tests;

[Trait("tier", "unit")]
public sealed class RepoStateCacheCompatTests : IDisposable
{
    private readonly StateDir _state = new();

    public void Dispose() => _state.Dispose();

    private static string CacheFileFor(string repoPath, string kind)
    {
        var norm = Path.GetFullPath(repoPath).Replace('\\', '/').TrimEnd('/');
        if (OperatingSystem.IsWindows())
            norm = norm.ToLowerInvariant();
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(norm)));
        return Path.Combine(AppPaths.CacheDirectory, $"repo-{kind}-{hash}.json.gz");
    }

    private static void WriteGzJson(string repoPath, string kind, string json)
    {
        var path = CacheFileFor(repoPath, kind);

        Directory.CreateDirectory(AppPaths.CacheDirectory);
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var writer = new StreamWriter(gz, Encoding.UTF8);
        writer.Write(json);
    }

    [Fact]
    public void Read_legacy_json_gz_with_40_char_string_hashes_loads_GitObjectId()
    {
        var settings = new FakeSettingsService(new AppSettings { PersistRepoCache = true });
        var cache = new RepoStateCache(settings);
        const string repoPath = "C:/compat-test-repo";

        const string refsJson = """
        {
          "version": 2,
          "branches": [
            {
              "name": "main",
              "canonicalName": "refs/heads/main",
              "isRemote": false,
              "isCurrent": true,
              "tipCommitId": "1111111111111111111111111111111111111111",
              "remoteName": "origin",
              "upstreamName": "refs/remotes/origin/main"
            },
            {
              "name": "null-tip",
              "canonicalName": "refs/heads/null-tip",
              "isRemote": false,
              "isCurrent": false,
              "tipCommitId": null
            }
          ],
          "tags": [
            {
              "name": "v1.0",
              "targetCommitId": "2222222222222222222222222222222222222222",
              "isAnnotated": true
            },
            {
              "name": "zero-tag",
              "targetCommitId": "0000000000000000000000000000000000000000",
              "isAnnotated": false
            }
          ],
          "remotes": []
        }
        """;

        const string ptJson = """
        {
          "version": 2,
          "stashes": [
            {
              "index": 0,
              "commitId": "3333333333333333333333333333333333333333",
              "message": "stash0",
              "authorName": "Alice",
              "authorEmail": "alice@example.com",
              "createdAt": "2026-01-01T00:00:00+00:00",
              "branchName": "main",
              "fileCount": 1,
              "includesUntracked": false
            }
          ],
          "submodules": [
            {
              "name": "sub1",
              "path": "sub1",
              "url": "https://example.com/sub",
              "headCommitId": "4444444444444444444444444444444444444444",
              "registeredCommitId": "5555555555555555555555555555555555555555",
              "status": 0
            }
          ],
          "worktrees": [
            {
              "path": "C:/wt",
              "branch": "wt-branch",
              "headCommitId": "6666666666666666666666666666666666666666",
              "isMainWorktree": false,
              "isLocked": false,
              "isPrunable": false
            }
          ]
        }
        """;

        WriteGzJson(repoPath, "refs", refsJson);
        WriteGzJson(repoPath, "pt", ptJson);

        var snapshot = cache.TryLoad(repoPath);
        Assert.NotNull(snapshot);

        Assert.Equal(2, snapshot.Branches!.Count);
        Assert.Equal(GitObjectId.Parse("1111111111111111111111111111111111111111"), snapshot.Branches[0].TipCommitId);
        Assert.Null(snapshot.Branches[1].TipCommitId);

        Assert.Equal(2, snapshot.Tags!.Count);
        Assert.Equal(GitObjectId.Parse("2222222222222222222222222222222222222222"), snapshot.Tags[0].TargetCommitId);
        Assert.Equal(GitObjectId.Zero, snapshot.Tags[1].TargetCommitId);

        Assert.Single(snapshot.Stashes!);
        Assert.Equal(GitObjectId.Parse("3333333333333333333333333333333333333333"), snapshot.Stashes![0].CommitId);

        Assert.Single(snapshot.Submodules!);
        Assert.Equal(GitObjectId.Parse("4444444444444444444444444444444444444444"), snapshot.Submodules![0].HeadCommitId);
        Assert.Equal(GitObjectId.Parse("5555555555555555555555555555555555555555"), snapshot.Submodules![0].RegisteredCommitId);

        Assert.Single(snapshot.Worktrees!);
        Assert.Equal(GitObjectId.Parse("6666666666666666666666666666666666666666"), snapshot.Worktrees![0].HeadCommitId);
    }

    [Fact]
    public void Read_sha256_64_char_string_hashes_loads_GitObjectId()
    {
        var settings = new FakeSettingsService(new AppSettings { PersistRepoCache = true });
        var cache = new RepoStateCache(settings);
        const string repoPath = "C:/sha256-test-repo";

        const string sha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        string refsJson = $$"""
        {
          "version": 2,
          "branches": [
            {
              "name": "main",
              "canonicalName": "refs/heads/main",
              "isRemote": false,
              "isCurrent": true,
              "tipCommitId": "{{sha256}}"
            }
          ],
          "tags": [],
          "remotes": []
        }
        """;

        WriteGzJson(repoPath, "refs", refsJson);

        var snapshot = cache.TryLoad(repoPath);
        Assert.NotNull(snapshot);
        var tip = snapshot.Branches![0].TipCommitId;
        Assert.NotNull(tip);
        Assert.Equal(32, tip.Value.HashSize);
        Assert.Equal(sha256, tip.Value.ToString());
    }

    [Fact]
    public void Read_pre_gitobjectid_fixture_files_loads_all_entities_and_ids()
    {
        var settings = new FakeSettingsService(new AppSettings { PersistRepoCache = true });
        var cache = new RepoStateCache(settings);
        const string repoPath = "C:/compat-fixture-repo";

        var fixtureDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "repo-state-cache-pre-gitobjectid");
        Directory.CreateDirectory(AppPaths.CacheDirectory);
        // The fixture names carry the Windows key; copy each under this platform's key for repoPath.
        foreach (var file in Directory.GetFiles(fixtureDir, "*.json.gz"))
        {
            var kind = Path.GetFileName(file).Split('-')[1];
            File.Copy(file, CacheFileFor(repoPath, kind), overwrite: true);
        }

        var snapshot = cache.TryLoad(repoPath);
        Assert.NotNull(snapshot);

        Assert.Equal(2, snapshot.Branches!.Count);
        Assert.Equal(GitObjectId.Parse("1111111111111111111111111111111111111111"), snapshot.Branches[0].TipCommitId);
        Assert.Null(snapshot.Branches[1].TipCommitId);

        Assert.Equal(2, snapshot.Tags!.Count);
        Assert.Equal(GitObjectId.Parse("2222222222222222222222222222222222222222"), snapshot.Tags[0].TargetCommitId);
        Assert.Equal(GitObjectId.Zero, snapshot.Tags[1].TargetCommitId);

        Assert.Single(snapshot.Stashes!);
        Assert.Equal(GitObjectId.Parse("3333333333333333333333333333333333333333"), snapshot.Stashes![0].CommitId);

        Assert.Single(snapshot.Submodules!);
        Assert.Equal(GitObjectId.Parse("4444444444444444444444444444444444444444"), snapshot.Submodules![0].HeadCommitId);
        Assert.Equal(GitObjectId.Parse("5555555555555555555555555555555555555555"), snapshot.Submodules![0].RegisteredCommitId);

        Assert.Single(snapshot.Worktrees!);
        Assert.Equal(GitObjectId.Parse("6666666666666666666666666666666666666666"), snapshot.Worktrees![0].HeadCommitId);
    }
}
