using Historia.Models;
using Historia.Services.Git;
using Xunit;

namespace Historia.Tests;

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
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("seed", "x").Stage().Commit("seed");

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
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("seed", "x").Stage().Commit("seed");
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
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("seed", "x").Stage().Commit("seed");

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
        using var repo = TestRepositoryBuilder.Create();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["log"],
            WorkingDirectory = repo.Path,
        }, cts.Token);

        Assert.Equal(GitErrorKind.Cancelled, result.ErrorKind);
    }
}
