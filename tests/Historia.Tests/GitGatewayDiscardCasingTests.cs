namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class GitGatewayDiscardCasingTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Discard_staged_case_rename_does_not_delete_the_file()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var repo = TestRepositoryBuilder.FromTemplate("casing-staged-mv", r =>
            r.WriteFile("Foo.txt", "line1\n").Stage().Commit("init"));

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "core.ignorecase", "true"],
            WorkingDirectory = repo.Path,
        });

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["mv", "Foo.txt", "foo.txt"],
            WorkingDirectory = repo.Path,
        });

        await File.AppendAllTextAsync(Path.Combine(repo.Path, "foo.txt"), "line2\n");

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.DiscardAsync(["foo.txt"]);

        Assert.True(File.Exists(Path.Combine(repo.Path, "foo.txt")));
        Assert.Equal("line1", (await File.ReadAllTextAsync(Path.Combine(repo.Path, "foo.txt"))).Trim());

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Single(status.Staged);
        Assert.Equal(FileChangeKind.Renamed, status.Staged[0].Kind);
        Assert.Equal("foo.txt", status.Staged[0].Path);
        Assert.Equal("Foo.txt", status.Staged[0].OldPath);
        Assert.Empty(status.Unstaged);
        Assert.Empty(status.Untracked);
    }

    [Fact]
    public async Task Discard_unstaged_case_rename_with_ignorecase_false_restores_old_name()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var repo = TestRepositoryBuilder.FromTemplate("casing-unstaged-false", r =>
            r.WriteFile("Foo.txt", "content\n").Stage().Commit("init"));

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "core.ignorecase", "false"],
            WorkingDirectory = repo.Path,
        });

        var oldFile = Path.Combine(repo.Path, "Foo.txt");
        var tmpFile = Path.Combine(repo.Path, "temp.txt");
        var newFile = Path.Combine(repo.Path, "foo.txt");
        File.Move(oldFile, tmpFile);
        File.Move(tmpFile, newFile);

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.DiscardAsync(["foo.txt", "Foo.txt"]);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.False(status.IsDirty);

        var files = Directory.EnumerateFiles(repo.Path).Select(Path.GetFileName).ToList();
        Assert.Contains("Foo.txt", files);
        Assert.DoesNotContain("foo.txt", files);
        Assert.Equal("content", (await File.ReadAllTextAsync(Path.Combine(repo.Path, "Foo.txt"))).Trim());
    }

    [Fact]
    public async Task Discard_keeps_staged_edit_and_reverts_unstaged_edit()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("discard-keeps-staged", r =>
            r.WriteFile("file.txt", "v1\n").Stage().Commit("init"));

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "core.ignorecase", "true"],
            WorkingDirectory = repo.Path,
        });

        var ctx = _host.Open(repo.Path);
        var fullPath = Path.Combine(repo.Path, "file.txt");
        await File.WriteAllTextAsync(fullPath, "v2\n");
        await ctx.Gateway.StageAsync(["file.txt"]);

        await File.WriteAllTextAsync(fullPath, "v3\n");
        await ctx.Gateway.DiscardAsync(["file.txt"]);

        Assert.Equal("v2", (await File.ReadAllTextAsync(fullPath)).Trim());
        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Single(status.Staged);
        Assert.Equal("file.txt", status.Staged[0].Path);
        Assert.Equal(FileChangeKind.Modified, status.Staged[0].Kind);
        Assert.Empty(status.Unstaged);
        Assert.Empty(status.Untracked);
    }

    [Fact]
    public async Task Discard_intent_to_add_recycles_instead_of_emptying()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("discard-ita", r =>
            r.WriteFile("init.txt", "base\n").Stage().Commit("init"));

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "core.ignorecase", "true"],
            WorkingDirectory = repo.Path,
        });

        var filePath = Path.Combine(repo.Path, "n.txt");
        await File.WriteAllTextAsync(filePath, "non-empty content\n");

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["add", "-N", "n.txt"],
            WorkingDirectory = repo.Path,
        });

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.DiscardAsync(["n.txt"]);

        Assert.False(File.Exists(filePath));
        var ls = await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["ls-files", "n.txt"],
            WorkingDirectory = repo.Path,
        });
        Assert.True(string.IsNullOrWhiteSpace(ls.StandardOutput));

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.False(status.IsDirty);
    }

    [Fact]
    public async Task Discard_on_unborn_head_restores_from_index()
    {
        using var repo = TestRepositoryBuilder.Create();

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "core.ignorecase", "true"],
            WorkingDirectory = repo.Path,
        });

        var aPath = Path.Combine(repo.Path, "a.txt");
        var bPath = Path.Combine(repo.Path, "b.txt");
        await File.WriteAllTextAsync(aPath, "one\n");

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.StageAsync(["a.txt"]);

        await File.WriteAllTextAsync(aPath, "two\n");
        await File.WriteAllTextAsync(bPath, "untracked\n");

        await ctx.Gateway.DiscardAsync(["a.txt", "b.txt"]);

        Assert.Equal("one", (await File.ReadAllTextAsync(aPath)).Trim());
        Assert.False(File.Exists(bPath));

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Single(status.Staged);
        Assert.Equal("a.txt", status.Staged[0].Path);
        Assert.Empty(status.Unstaged);
        Assert.Empty(status.Untracked);
    }

    [Fact]
    public async Task Discard_never_recycles_a_case_variant_of_a_tracked_path()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("discard-case-guard", r =>
            r.WriteFile("foo.txt", "tracked content\n").Stage().Commit("init"));

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "core.ignorecase", "true"],
            WorkingDirectory = repo.Path,
        });

        var spy = new InterceptingGitCliService(_host.Cli);
        var ctx = _host.Open(repo.Path, spy);

        await ctx.Gateway.DiscardAsync(["FOO.TXT"]);

        var fooPath = Path.Combine(repo.Path, "foo.txt");
        Assert.True(File.Exists(fooPath));
        Assert.Equal("tracked content", (await File.ReadAllTextAsync(fooPath)).Trim());

        var rmRequests = spy.Requests.Where(r => r.Arguments.Contains("rm")).ToList();
        Assert.Empty(rmRequests);
    }
}
