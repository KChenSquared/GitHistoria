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

        var revParseRequest = new GitCliRequest
        {
            Arguments = ["-C", workingDir, "rev-parse", "--is-bare-repository", "--absolute-git-dir"],
            WorkingDirectory = validCwd,
        };

        // Checked only after RunAsync: it waits for git to be located, and tabs restored at
        // startup open while that is still running, when IsGitAvailable still reads false.
        var result = await cli.RunAsync(revParseRequest, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            if (!cli.IsGitAvailable)
            {
                throw new HistoriaGitException(
                    GitErrorKind.Unknown,
                    "Git is required to open a repository. Install Git 2.31 or newer, or set its path in Settings.",
                    result.StandardError);
            }

            if (result.ErrorKind == GitErrorKind.Cancelled)
                throw new OperationCanceledException(ct);

            if (result.ErrorKind == GitErrorKind.TimedOut)
                throw HistoriaGitException.FromCli(result, "Open repository");

            if (result.ErrorKind == GitErrorKind.UnsafeRepository)
            {
                throw new HistoriaGitException(
                    GitErrorKind.UnsafeRepository,
                    GitErrorClassifier.FormatUnsafeRepositoryMessage(path),
                    result.StandardError);
            }

            throw new HistoriaGitException(
                GitErrorKind.NotAGitRepository,
                $"No Git repository was found at or above '{path}'.",
                result.StandardError);
        }

        var lines = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2)
        {
            throw new HistoriaGitException(
                GitErrorKind.NotAGitRepository,
                $"No Git repository was found at or above '{path}'.",
                result.StandardError);
        }

        var isBare = bool.TryParse(lines[0].Trim(), out var b) && b;
        var gitDir = NormalizePath(lines[1].Trim());

        if (isBare)
        {
            return new DiscoveredRepository(gitDir, gitDir, IsBare: true);
        }

        var showToplevelRequest = new GitCliRequest
        {
            Arguments = ["-C", workingDir, "rev-parse", "--show-toplevel"],
            WorkingDirectory = validCwd,
        };

        var toplevelResult = await cli.RunAsync(showToplevelRequest, ct).ConfigureAwait(false);
        string repositoryPath;
        if (toplevelResult.IsSuccess && !string.IsNullOrWhiteSpace(toplevelResult.StandardOutput))
        {
            repositoryPath = NormalizePath(toplevelResult.StandardOutput.Trim());
        }
        else
        {
            // `--show-toplevel` exits 128/1 ("must be run in a work tree") both in a bare repo
            // and when <path> is inside .git; in the second case, retry from the parent of --absolute-git-dir.
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
        }

        return new DiscoveredRepository(gitDir, repositoryPath, IsBare: false);
    }

    private static string NormalizePath(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path; }
    }
}
