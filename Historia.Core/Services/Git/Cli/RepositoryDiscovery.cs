namespace Historia.Services.Git;

/// <summary>
/// Result of repository discovery via Git CLI.
/// </summary>
/// <param name="GitDirectory">The path to the git directory (e.g. <c>.git</c>, or the bare repo path).</param>
/// <param name="RepositoryPath">The working tree root directory, or the bare repo path.</param>
/// <param name="IsBare">Whether the discovered repository is bare.</param>
public sealed record DiscoveredRepository(string GitDirectory, string RepositoryPath, bool IsBare);

/// <summary>
/// Discovers Git repositories through <c>git rev-parse</c>.
/// Resolves working trees, subdirectories, linked worktrees, submodules, bare repositories,
/// and SHA-256 repositories.
/// </summary>
public static class RepositoryDiscovery
{
    public static DiscoveredRepository Discover(string path, IServiceProvider services) =>
        Discover(path, services.GetRequiredService<IGitCliService>());

    public static DiscoveredRepository Discover(string path, IGitCliService cli) =>
        Task.Run(() => DiscoverAsync(path, cli, CancellationToken.None)).GetAwaiter().GetResult();

    public static async Task<DiscoveredRepository> DiscoverAsync(string path, IGitCliService cli, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(cli);

        var workingDir = Directory.Exists(path) ? path : (File.Exists(path) ? (Path.GetDirectoryName(path) ?? path) : path);
        var validCwd = Directory.Exists(workingDir)
            ? workingDir
            : (Directory.Exists(Path.GetDirectoryName(workingDir)) ? Path.GetDirectoryName(workingDir)! : AppContext.BaseDirectory);

        // One rev-parse answers all three questions for a working tree. In a bare repository, or
        // from inside .git, --show-toplevel fails after git has already printed the first two
        // answers — so stdout, not the exit code, says whether this is a repository at all.
        var revParseRequest = new GitCliRequest
        {
            Arguments = ["-C", workingDir, "rev-parse", "--is-bare-repository", "--absolute-git-dir", "--show-toplevel"],
            WorkingDirectory = validCwd,
        };

        // Checked only after RunAsync: it waits for git to be located, and tabs restored at
        // startup open while that is still running, when IsGitAvailable still reads false.
        var result = await cli.RunAsync(revParseRequest, ct).ConfigureAwait(false);
        if (result.ErrorKind == GitErrorKind.Cancelled)
            throw new OperationCanceledException(ct);
        if (result.ErrorKind == GitErrorKind.TimedOut)
            throw HistoriaGitException.FromCli(result, "Open repository");

        var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2 || !bool.TryParse(lines[0].Trim(), out var isBare))
            throw DiscoveryFailure(result, path, cli);

        var gitDir = NormalizePath(lines[1].Trim());

        if (isBare)
        {
            return new DiscoveredRepository(gitDir, gitDir, IsBare: true);
        }

        if (result.IsSuccess && lines.Length >= 3)
            return new DiscoveredRepository(gitDir, NormalizePath(lines[2].Trim()), IsBare: false);

        // Not bare, but --show-toplevel failed ("must be run in a work tree"): <path> is inside
        // .git. Ask again from the parent of --absolute-git-dir.
        string repositoryPath;
        var parentDir = Path.GetDirectoryName(gitDir);
        if (!string.IsNullOrEmpty(parentDir))
        {
            var retryCwd = Directory.Exists(parentDir) ? parentDir : validCwd;
            var retryRequest = new GitCliRequest
            {
                Arguments = ["-C", parentDir, "rev-parse", "--show-toplevel"],
                WorkingDirectory = retryCwd,
            };
            var retryResult = await cli.RunAsync(retryRequest, ct).ConfigureAwait(false);
            if (retryResult.IsSuccess && !string.IsNullOrWhiteSpace(retryResult.StandardOutput))
            {
                repositoryPath = NormalizePath(retryResult.StandardOutput.Trim());
            }
            else
            {
                repositoryPath = NormalizePath(parentDir);
            }
        }
        else
        {
            repositoryPath = gitDir;
        }

        return new DiscoveredRepository(gitDir, repositoryPath, IsBare: false);
    }

    private static Exception DiscoveryFailure(GitCliResult result, string path, IGitCliService cli)
    {
        if (!result.IsSuccess && !cli.IsGitAvailable)
        {
            return new HistoriaGitException(
                GitErrorKind.Unknown,
                $"Git is required to open a repository. Install Git {GitExecutableInfo.MinimumVersion} or newer, or set its path in Settings.",
                result.StandardError);
        }

        if (result.ErrorKind == GitErrorKind.UnsafeRepository)
        {
            return new HistoriaGitException(
                GitErrorKind.UnsafeRepository,
                GitErrorClassifier.FormatUnsafeRepositoryMessage(path),
                result.StandardError);
        }

        return new HistoriaGitException(
            GitErrorKind.NotAGitRepository,
            $"No Git repository was found at or above '{path}'.",
            result.StandardError);
    }

    private static string NormalizePath(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path; }
    }
}
