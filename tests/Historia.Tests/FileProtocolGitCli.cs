namespace Historia.Tests;

/// <summary>Passes <c>protocol.file.allow=always</c> to every git child of <paramref name="inner"/>
/// (local-path submodules are blocked by default since CVE-2022-39253). Scoped to one CLI instead
/// of the process environment, so suites that need it can run in parallel with everything else.</summary>
public sealed class FileProtocolGitCli(IGitCliService inner) : IGitCliService
{
    public bool IsGitAvailable => inner.IsGitAvailable;

    public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
        => inner.RunAsync(Allow(request), ct);

    public Task<GitCliResult> RunAsync(
        GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
        => inner.RunAsync(Allow(request), progress, onStdoutLine, ct);

    private static GitCliRequest Allow(GitCliRequest request)
    {
        var env = request.ExtraEnvironment is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(request.ExtraEnvironment);
        env["GIT_CONFIG_COUNT"] = "1";
        env["GIT_CONFIG_KEY_0"] = "protocol.file.allow";
        env["GIT_CONFIG_VALUE_0"] = "always";
        return request with { ExtraEnvironment = env };
    }
}
