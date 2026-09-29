namespace Historia.Tests;

/// <summary>
/// Verifies that staging via GitGateway runs Git clean filter drivers (such as Git LFS or text transforms)
/// while LibGit2Sharp bypasses them.
/// </summary>
[Trait("tier", "git")]
public sealed class FilterDriverTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Gateway_stages_filtered_blob_while_libgit2_stages_unfiltered()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("filter-driver-seed", b =>
        {
            b.WriteFile(".gitattributes", "*.txt filter=upper\n").Stage();
            b.Commit("init");
        });

        var cli = _host.Cli;
        await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "filter.upper.clean", "tr a-z A-Z"],
            WorkingDirectory = repo.Path,
        });

        var ctx = _host.Open(repo.Path);

        // 1. Stage via gateway — should pass through the filter driver to produce "HELLO"
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "gw.txt"), "hello\n");
        await ctx.Gateway.StageAsync(["gw.txt"]);

        var catGw = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["cat-file", "-p", ":gw.txt"],
            WorkingDirectory = repo.Path,
        });
        Assert.True(catGw.IsSuccess);
        Assert.Equal("HELLO", catGw.StandardOutput.Trim());

        // 2. Stage via LibGit2Sharp directly on the same fixture — produces unfiltered "hello"
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "lib.txt"), "hello\n");
        using (var libRepo = new Repository(repo.Path))
        {
            Commands.Stage(libRepo, "lib.txt");
        }

        var catLib = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["cat-file", "-p", ":lib.txt"],
            WorkingDirectory = repo.Path,
        });
        Assert.True(catLib.IsSuccess);
        Assert.Equal("hello", catLib.StandardOutput.Trim());
    }
}
