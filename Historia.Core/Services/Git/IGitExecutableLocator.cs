
namespace Historia.Services.Git;

/// <summary>
/// Resolves <c>git.exe</c> once at startup and caches the result (§6.2.3). If Git is not
/// found the app still starts; CLI-routed operations are disabled with an explanatory banner.
/// Registered as a singleton.
/// </summary>
public interface IGitExecutableLocator
{
    /// <summary>The resolved executable, or <see cref="GitExecutableInfo.NotFound"/>.</summary>
    GitExecutableInfo Current { get; }

    /// <summary>Runs discovery (PATH, well-known dirs, registry) and captures the version.</summary>
    Task<GitExecutableInfo> ResolveAsync(string? userOverridePath, CancellationToken ct = default);
}
