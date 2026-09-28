namespace Historia.Tests;
[Trait("tier", "git")]

public class GitCliServiceTests
{
    private static async Task<GitCliService> MakeAsync()
    {
        var locator = new GitExecutableLocator();
        await locator.ResolveAsync(null);
        return new GitCliService(locator);
    }

    [Fact]
    public async Task Arguments_with_spaces_and_unicode_are_passed_literally_not_shell_interpreted()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("git-cli-seed", r =>
            r.WriteFile("seed", "x").Stage().Commit("seed"));

        const string tricky = "a file — with spaces & 日本語.txt";
        repo.WriteFile(tricky, "content\n");

        var add = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["add", "--", tricky],
            WorkingDirectory = repo.Path,
        });
        Assert.True(add.IsSuccess, add.StandardError);

        var status = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["status", "--porcelain"],
            WorkingDirectory = repo.Path,
        });
        Assert.Contains(tricky, status.StandardOutput);
    }

    [Fact]
    public async Task A_leading_dash_path_after_double_dash_is_not_treated_as_an_option()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("git-cli-seed", r =>
            r.WriteFile("seed", "x").Stage().Commit("seed"));
        repo.WriteFile("-weird-name", "content\n");

        var add = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["add", "--", "-weird-name"],
            WorkingDirectory = repo.Path,
        });

        Assert.True(add.IsSuccess, add.StandardError);
    }

    [Fact]
    public async Task Checkout_of_a_missing_ref_is_classified_not_surfaced_raw()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("git-cli-seed", r =>
            r.WriteFile("seed", "x").Stage().Commit("seed"));

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["checkout", "no-such-branch-xyz"],
            WorkingDirectory = repo.Path,
        });

        Assert.NotEqual(0, result.ExitCode);
        Assert.NotEqual(GitErrorKind.None, result.ErrorKind);
    }

    [Fact]
    public async Task Cancellation_kills_the_process_and_reports_cancelled()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("empty-repo", _ => { });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["log"],
            WorkingDirectory = repo.Path,
        }, cts.Token);

        Assert.Equal(GitErrorKind.Cancelled, result.ErrorKind);
    }

    /// <summary>A blob whose bytes line mode would rewrite: a CRLF, a lone CR and a NUL.</summary>
    private static readonly byte[] AwkwardBytes = "a\r\nb\rc\0d\n"u8.ToArray();

    private static async Task<(GitCliService Cli, TestRepositoryBuilder Repo, string Blob)> AwkwardBlobAsync()
    {
        var cli = await MakeAsync();
        var repo = TestRepositoryBuilder.FromTemplate("git-cli-seed", r =>
            r.WriteFile("seed", "x").Stage().Commit("seed"));
        File.WriteAllBytes(Path.Combine(repo.Path, "awkward.bin"), AwkwardBytes);
        var blob = repo.Git("hash-object", "-w", "awkward.bin").Trim();
        return (cli, repo, blob);
    }

    [Fact]
    public async Task Raw_stdout_keeps_crlf_lone_cr_and_nul()
    {
        var (cli, repo, blob) = await AwkwardBlobAsync();
        using var _ = repo;

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["cat-file", "blob", blob],
            WorkingDirectory = repo.Path,
            RawStandardOutput = true,
        });

        Assert.True(result.IsSuccess, result.StandardError);
        Assert.Equal(AwkwardBytes, result.StandardOutputBytes);
        Assert.Equal("a\r\nb\rc\0d\n", result.StandardOutput);
    }

    [Fact]
    public async Task Line_mode_output_is_unchanged()
    {
        var (cli, repo, blob) = await AwkwardBlobAsync();
        using var _ = repo;

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["cat-file", "blob", blob],
            WorkingDirectory = repo.Path,
        });

        Assert.True(result.IsSuccess, result.StandardError);
        Assert.Null(result.StandardOutputBytes);
        var nl = Environment.NewLine;
        Assert.Equal($"a{nl}b{nl}c\0d{nl}", result.StandardOutput);
    }

    [Fact]
    public async Task Raw_mode_rejects_a_line_callback()
    {
        var cli = await MakeAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => cli.RunAsync(new GitCliRequest
        {
            Arguments = ["version"],
            WorkingDirectory = Path.GetTempPath(),
            RawStandardOutput = true,
        }, progress: null, onStdoutLine: _ => { }));
    }

    [Fact]
    public async Task Raw_mode_honours_the_timeout()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("linear-3000", b => b.LinearHistory(3_000));

        var run = cli.RunAsync(new GitCliRequest
        {
            Arguments = ["log", "--all", "-p"],
            WorkingDirectory = repo.Path,
            RawStandardOutput = true,
            Timeout = TimeSpan.FromMilliseconds(1),
        });

        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))));
        Assert.Equal(GitErrorKind.TimedOut, (await run).ErrorKind);
    }

    [Fact]
    public async Task RunAsync_before_resolution_waits_for_resolution_then_runs()
    {
        var locator = new DelayedLocator();
        var cli = new GitCliService(locator);

        using var repo = TestRepositoryBuilder.FromTemplate("git-cli-seed", r =>
            r.WriteFile("seed", "x").Stage().Commit("seed"));

        var runTask = cli.RunAsync(new GitCliRequest
        {
            Arguments = ["status", "--porcelain"],
            WorkingDirectory = repo.Path,
        });

        // The task should be waiting for locator resolution.
        Assert.False(runTask.IsCompleted);

        // Now resolve git using the real locator.
        var realLocator = new GitExecutableLocator();
        var resolved = await realLocator.ResolveAsync(null);
        locator.Complete(resolved);

        var result = await runTask;
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task RunAsync_fails_classified_when_resolution_finds_no_git()
    {
        var locator = new DelayedLocator();
        var cli = new GitCliService(locator);
        locator.Complete(GitExecutableInfo.NotFound);

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["status"],
            WorkingDirectory = Path.GetTempPath(),
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(GitErrorKind.Unknown, result.ErrorKind);
        Assert.Contains("not found", result.StandardError);
    }

    [Fact]
    public async Task Output_budget_stops_the_process_and_flags_it()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("linear-3000", b => b.LinearHistory(3_000));

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["log", "--all", "-p"],
            WorkingDirectory = repo.Path,
            MaxStandardOutputBytes = 1_000,
        });

        Assert.True(result.OutputTruncated);
        Assert.True(result.StandardOutput.Length <= 5_000);
        Assert.Equal(GitErrorKind.None, result.ErrorKind);
    }

    [Fact]
    public async Task Output_budget_stops_the_process_and_flags_it_in_raw_mode()
    {
        var cli = await MakeAsync();
        using var repo = TestRepositoryBuilder.FromTemplate("linear-3000", b => b.LinearHistory(3_000));

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["log", "--all", "-p"],
            WorkingDirectory = repo.Path,
            RawStandardOutput = true,
            MaxStandardOutputBytes = 1_000,
        });

        Assert.True(result.OutputTruncated);
        Assert.NotNull(result.StandardOutputBytes);
        Assert.True(result.StandardOutputBytes.Length <= 1_000);
        Assert.Equal(GitErrorKind.None, result.ErrorKind);
    }

    private sealed class DelayedLocator : IGitExecutableLocator
    {
        private readonly TaskCompletionSource<GitExecutableInfo> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private GitExecutableInfo _current = GitExecutableInfo.NotFound;

        public GitExecutableInfo Current => _current;

        public Task<GitExecutableInfo> ResolveAsync(string? userOverridePath, CancellationToken ct = default) => _tcs.Task;

        public Task<GitExecutableInfo> WhenResolvedAsync(CancellationToken ct = default) => _tcs.Task;

        public void Complete(GitExecutableInfo info)
        {
            _current = info;
            _tcs.TrySetResult(info);
        }
    }
}
