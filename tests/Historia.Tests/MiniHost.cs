namespace Historia.Tests;

/// <summary>Minimal DI provider + a real <see cref="RepositoryContext"/> for integration tests.</summary>
public sealed class MiniHost : IAsyncDisposable
{
    private static readonly Lazy<IGitExecutableLocator> SharedLocator = new(() =>
    {
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        return locator;
    });

    private readonly StateDir _state = new();
    public ServiceProvider Provider { get; }
    public SettingsService Settings { get; }

    public MiniHost()
    {
        Settings = SettingsService.Load();
        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(Settings);
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton<LibGit2Service>();
        sc.AddSingleton<IGitExecutableLocator>(SharedLocator.Value);
        sc.AddSingleton<IGitCliService, GitCliService>();
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        Provider = sc.BuildServiceProvider();
    }

    public RepositoryContext Open(string path) => RepositoryContext.Open(path, Provider);

    public async ValueTask DisposeAsync()
    {
        await Provider.DisposeAsync();
        await Settings.DisposeAsync();
        _state.Dispose();
    }
}
