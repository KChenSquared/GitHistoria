namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class GitGatewayCaseCollisionStatusTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    private async Task<(TestRepositoryBuilder Repo, string LosingPath, string WinningPath)?> CreateCollisionFixtureAsync()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var repo = TestRepositoryBuilder.Create();

        var probePath = Path.Combine(repo.Path, "probe");
        await File.WriteAllTextAsync(probePath, "probe");
        var isCaseInsensitive = File.Exists(Path.Combine(repo.Path, "PROBE"));
        File.Delete(probePath);

        if (!isCaseInsensitive)
        {
            repo.Dispose();
            return null;
        }

        var hash1 = await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["hash-object", "-w", "--stdin"],
            WorkingDirectory = repo.Path,
            StandardInput = "blob Foo\n",
        });
        var oid1 = hash1.StandardOutput.Trim();

        var hash2 = await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["hash-object", "-w", "--stdin"],
            WorkingDirectory = repo.Path,
            StandardInput = "blob foo\n",
        });
        var oid2 = hash2.StandardOutput.Trim();

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["update-index", "--add", "--cacheinfo", $"100644,{oid1},Foo.txt"],
            WorkingDirectory = repo.Path,
        });

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["update-index", "--add", "--cacheinfo", $"100644,{oid2},foo.txt"],
            WorkingDirectory = repo.Path,
        });

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["commit", "-m", "init collision"],
            WorkingDirectory = repo.Path,
        });

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["checkout-index", "-f", "-a"],
            WorkingDirectory = repo.Path,
        });

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "core.ignorecase", "true"],
            WorkingDirectory = repo.Path,
        });

        var content = (await File.ReadAllTextAsync(Path.Combine(repo.Path, "Foo.txt"))).Trim();
        string winningPath = content == "blob Foo" ? "Foo.txt" : "foo.txt";
        string losingPath = content == "blob Foo" ? "foo.txt" : "Foo.txt";

        return (repo, losingPath, winningPath);
    }

    [Fact]
    public async Task Twin_content_is_hidden_from_unstaged()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        var ctx = _host.Open(repo.Path);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Empty(status.Unstaged);
        Assert.Equal([fixture.Value.LosingPath], status.HiddenCaseCollisions);
        Assert.False(status.IsDirty);
    }

    [Fact]
    public async Task Real_edit_of_a_collided_file_still_shows()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        await File.AppendAllTextAsync(Path.Combine(repo.Path, "Foo.txt"), "real edit\n");

        var ctx = _host.Open(repo.Path);
        var status = await ctx.Gateway.GetStatusAsync();

        Assert.Equal(2, status.Unstaged.Count);
        Assert.All(status.Unstaged, u => Assert.Equal(FileChangeKind.Modified, u.Kind));
        Assert.Contains(status.Unstaged, u => u.Path == "Foo.txt");
        Assert.Contains(status.Unstaged, u => u.Path == "foo.txt");
        Assert.Empty(status.HiddenCaseCollisions);
        Assert.True(status.IsDirty);
    }

    [Fact]
    public async Task Discard_of_a_real_edit_ends_clean()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        await File.AppendAllTextAsync(Path.Combine(repo.Path, "Foo.txt"), "real edit\n");

        var ctx = _host.Open(repo.Path);
        var beforeStatus = await ctx.Gateway.GetStatusAsync();
        Assert.Equal(2, beforeStatus.Unstaged.Count);

        await ctx.Gateway.DiscardAsync(["Foo.txt", "foo.txt"]);

        var afterStatus = await ctx.Gateway.GetStatusAsync();
        Assert.Empty(afterStatus.Unstaged);
        Assert.Equal([fixture.Value.LosingPath], afterStatus.HiddenCaseCollisions);
        Assert.False(afterStatus.IsDirty);
    }

    [Fact]
    public async Task Ignorecase_false_shows_git_status_verbatim()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "core.ignorecase", "false"],
            WorkingDirectory = repo.Path,
        });

        var ctx = _host.Open(repo.Path);
        var status = await ctx.Gateway.GetStatusAsync();

        Assert.Single(status.Unstaged);
        Assert.Equal(fixture.Value.LosingPath, status.Unstaged[0].Path);
        Assert.Empty(status.HiddenCaseCollisions);
        Assert.True(status.IsDirty);
    }

    [Fact]
    public async Task Steady_state_refresh_spawns_no_extra_process()
    {
        var fixture = await CreateCollisionFixtureAsync();
        if (fixture is null)
            return;

        using var repo = fixture.Value.Repo;
        var spy = new InterceptingGitCliService(_host.Cli);
        var ctx = _host.Open(repo.Path, spy);

        var firstStatus = await ctx.Gateway.GetStatusAsync();
        Assert.Empty(firstStatus.Unstaged);
        Assert.Single(firstStatus.HiddenCaseCollisions);

        var countBefore = spy.Requests.Count;
        var secondStatus = await ctx.Gateway.GetStatusAsync();
        Assert.Empty(secondStatus.Unstaged);
        Assert.Single(secondStatus.HiddenCaseCollisions);

        var secondRequests = spy.Requests.Skip(countBefore).ToList();
        Assert.DoesNotContain(secondRequests, r => r.Arguments.Contains("ls-files"));
        Assert.DoesNotContain(secondRequests, r => r.Arguments.Contains("hash-object"));
        Assert.DoesNotContain(secondRequests, r => r.Arguments.Contains("config"));
    }

    [Fact]
    public async Task Repo_without_collisions_never_lists_the_index()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var repo = TestRepositoryBuilder.FromTemplate("no-collision-cache", r =>
            r.WriteFile("file.txt", "v1\n").Stage().Commit("init"));

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "core.ignorecase", "true"],
            WorkingDirectory = repo.Path,
        });

        await File.WriteAllTextAsync(Path.Combine(repo.Path, "file.txt"), "v2\n");

        var spy = new InterceptingGitCliService(_host.Cli);
        var ctx = _host.Open(repo.Path, spy);

        var firstStatus = await ctx.Gateway.GetStatusAsync();
        Assert.Single(firstStatus.Unstaged);
        Assert.Empty(firstStatus.HiddenCaseCollisions);

        var countBefore = spy.Requests.Count;
        var secondStatus = await ctx.Gateway.GetStatusAsync();
        Assert.Single(secondStatus.Unstaged);
        Assert.Empty(secondStatus.HiddenCaseCollisions);

        var secondRequests = spy.Requests.Skip(countBefore).ToList();
        Assert.DoesNotContain(secondRequests, r => r.Arguments.Contains("ls-files"));
    }
}
