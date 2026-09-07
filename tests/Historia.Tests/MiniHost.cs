using Microsoft.Extensions.DependencyInjection;
using Historia.Services.Git;
using Historia.Services.Profiles;
using Historia.Services.Settings;
using Historia.Services.Workspace;

namespace Historia.Tests;

/// <summary>Minimal DI provider + a real <see cref="RepositoryContext"/> for integration tests.</summary>
public sealed class MiniHost : IAsyncDisposable
{
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
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        sc.AddSingleton<IGitExecutableLocator>(locator);
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
