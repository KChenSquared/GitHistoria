namespace Historia.Tests;

/// <summary>
/// <see cref="RepositoryContext.OpenAsync"/> is the app's open path: it must reach the same
/// repository as <see cref="RepositoryContext.Open"/>, still paint the sidebar from the cached
/// state at construction (read alongside discovery, keyed on the repository root), and — through
/// <see cref="WorkspaceService.OpenAsync"/> — discover the repository only once.
/// </summary>
[Trait("tier", "git")]
public sealed class RepositoryContextOpenAsyncTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo = TestRepositoryBuilder.FromTemplate("open-async", b =>
    {
        b.WriteFile("dir/a.txt", "a\n").Stage();
        b.Commit("init");
    });
    private readonly List<ServiceProvider> _providers = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _providers)
            await p.DisposeAsync();
        await _host.DisposeAsync();
        _repo.Dispose();
    }

    private ServiceProvider Provider(IGitCliService cli, bool withStateCache)
    {
        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(_host.Settings);
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton(_host.Locator);
        sc.AddSingleton(cli);
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        if (withStateCache)
            sc.AddSingleton<IRepoStateCache>(new RepoStateCache(_host.Settings));
        var provider = sc.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    private static BranchInfo Branch(string name) => new()
    {
        Name = name,
        CanonicalName = $"refs/heads/{name}",
        IsRemote = false,
        IsCurrent = false,
        TipCommitId = null,
    };

    [Fact]
    public async Task Reaches_the_same_repository_as_the_blocking_open()
    {
        var sp = Provider(_host.Cli, withStateCache: false);
        foreach (var path in new[] { _repo.Path, Path.Combine(_repo.Path, "dir") })
        {
            await using var expected = RepositoryContext.Open(path, sp);
            await using var actual = await RepositoryContext.OpenAsync(path, sp);

            Assert.Equal(expected.RepositoryPath, actual.RepositoryPath);
            Assert.Equal(expected.GitDirectory, actual.GitDirectory);
            Assert.Equal(expected.IsBare, actual.IsBare);
        }
    }

    [Fact]
    public async Task Seeds_the_cached_refs_at_construction_from_any_path_into_the_repository()
    {
        var sp = Provider(_host.Cli, withStateCache: true);
        string root;
        await using (var probe = await RepositoryContext.OpenAsync(_repo.Path, Provider(_host.Cli, withStateCache: false)))
            root = probe.RepositoryPath;
        sp.GetRequiredService<IRepoStateCache>().SaveRefs(root, [Branch("cached-only")], [], []);

        foreach (var path in new[] { _repo.Path, Path.Combine(_repo.Path, "dir"), Path.Combine(_repo.Path, "dir", "..") })
        {
            await using var ctx = await RepositoryContext.OpenAsync(path, sp);
            Assert.Contains(ctx.State.Branches, b => b.Name == "cached-only");
        }
    }

    [Fact]
    public async Task A_workspace_open_discovers_the_repository_once()
    {
        var cli = new InterceptingGitCliService(_host.Cli);
        var sp = Provider(cli, withStateCache: false);
        var ws = new WorkspaceService(sp, cli, new RecentRepositoryService(_host.Settings),
            new ProfileService(_host.Settings, new FakeVault(), cli), new RepoIdentityService(cli));

        var ctx = await ws.OpenAsync(_repo.Path);

        Assert.Single(cli.Requests, r => r.Arguments.Contains("--absolute-git-dir"));
        ctx.MarkFirstGraphPaintDone();
        await ws.DisposeAsync();
    }
}
