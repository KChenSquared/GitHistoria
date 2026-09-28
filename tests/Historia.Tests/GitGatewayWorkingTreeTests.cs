namespace Historia.Tests;

/// <summary>
/// Verifies working tree staging, discard, unstage, and commit operations through GitGateway.
/// </summary>
[Trait("tier", "git")]
public sealed class GitGatewayWorkingTreeTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Stage_then_commit_advances_head()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gw-stage-seed", r =>
        {
            r.WriteFile("a", "1").Stage().Commit("first");
            r.WriteFile("b", "2");
        });

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.StageAsync(["b"]);
        var id = await ctx.Gateway.CommitAsync(new CommitRequest
        {
            Message = "second",
            AuthorName = "Alice",
            AuthorEmail = "alice@example.com",
        });

        using var git = new Repository(repo.Path);
        Assert.Equal(id, git.Head.Tip!.Id.ToGitObjectId());
        Assert.Equal("Alice", git.Head.Tip.Author.Name);
        Assert.Equal("alice@example.com", git.Head.Tip.Author.Email);
    }

    [Fact]
    public async Task DiscardWorkingTree_handles_unstaged_untracked_and_staged()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gw-discard-seed", r =>
        {
            r.WriteFile("tracked.txt", "original\n").Stage().Commit("init");
            r.WriteFile("tracked.txt", "modified\n");
            r.WriteFile("sub/nested.txt", "orig nested\n").Stage("sub/nested.txt").Commit("nested");
            r.WriteFile("sub/nested.txt", "mod nested\n");
            r.WriteFile("untracked.txt", "untracked\n");
            r.WriteFile("sub/untracked2.txt", "untracked2\n");
            r.WriteFile("staged_new.txt", "staged new\n").Stage("staged_new.txt");
            r.WriteFile("rename_me.txt", "rename me\n").Stage("rename_me.txt").Commit("rename init");
        });
        File.Move(Path.Combine(repo.Path, "rename_me.txt"), Path.Combine(repo.Path, "renamed.txt"));

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.DiscardAsync([
            "tracked.txt",
            "sub/nested.txt",
            "untracked.txt",
            "sub/untracked2.txt",
            "staged_new.txt",
            "renamed.txt",
            "rename_me.txt"
        ]);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.False(status.IsDirty);
        Assert.False(File.Exists(Path.Combine(repo.Path, "sub/untracked2.txt")));
        Assert.False(File.Exists(Path.Combine(repo.Path, "renamed.txt")));
        Assert.True(File.Exists(Path.Combine(repo.Path, "rename_me.txt")));
        Assert.Equal("orig nested", (await File.ReadAllTextAsync(Path.Combine(repo.Path, "sub/nested.txt"))).Trim());
    }

    [Fact]
    public async Task Stage_and_discard_5000_paths_with_long_names()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gw-5000-paths", r =>
        {
            r.WriteFile("init.txt", "root\n").Stage().Commit("root");
        });

        var dir = Path.Combine(repo.Path, "nested_folder_with_a_fairly_long_name");
        Directory.CreateDirectory(dir);

        var paths = new List<string>(5000);
        for (var i = 0; i < 5000; i++)
        {
            var relativePath = $"nested_folder_with_a_fairly_long_name/test_file_item_with_long_name_{i:D5}.txt";
            paths.Add(relativePath);
            File.WriteAllText(Path.Combine(repo.Path, relativePath), "initial\n");
        }

        var ctx = _host.Open(repo.Path);

        // 1. Stage all 5000 paths (argv length would exceed limit; stdin pathspec works)
        await ctx.Gateway.StageAsync(paths);
        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Equal(5000, status.Staged.Count);

        // 2. Commit them to HEAD
        await ctx.Gateway.CommitAsync(new CommitRequest
        {
            Message = "commit 5000 files",
            AuthorName = "Dev",
            AuthorEmail = "dev@example.com",
        });

        // 3. Modify them in the working tree
        for (var i = 0; i < 5000; i++)
        {
            File.WriteAllText(Path.Combine(repo.Path, paths[i]), "modified\n");
        }

        // 4. Discard all 5000 paths (exercises git ls-tree chunking over argv limit and restore from HEAD)
        await ctx.Gateway.DiscardAsync(paths);
        var statusAfter = await ctx.Gateway.GetStatusAsync();
        Assert.False(statusAfter.IsDirty);
    }

    [Fact]
    public async Task Stage_with_ignored_path_in_set_succeeds_and_stages_non_ignored()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gw-ignored-seed", r =>
        {
            r.WriteFile(".gitignore", "*.log\n").Stage().Commit("ignore rules");
        });

        await File.WriteAllTextAsync(Path.Combine(repo.Path, "regular.txt"), "regular content\n");
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "ignored.log"), "debug log\n");

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.StageAsync(["regular.txt", "ignored.log"]);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Contains(status.Staged, s => s.Path == "regular.txt");
        Assert.DoesNotContain(status.Staged, s => s.Path == "ignored.log");
    }

    [Fact]
    public async Task Stage_with_path_deleted_since_status_skips_deleted_and_stages_others()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gw-deleted-seed", r =>
        {
            r.WriteFile("base.txt", "base\n").Stage().Commit("init");
        });

        await File.WriteAllTextAsync(Path.Combine(repo.Path, "remain.txt"), "remain content\n");
        var deletedPath = "deleted_before_stage.txt";

        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.StageAsync(["remain.txt", deletedPath]);

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.Contains(status.Staged, s => s.Path == "remain.txt");
    }

    [Fact]
    public async Task Commit_with_core_hooks_path_runs_hook()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gw-hooks-seed", r =>
        {
            r.WriteFile("a.txt", "initial\n").Stage().Commit("init");
        });

        var hooksDir = Path.Combine(repo.Path, ".custom-hooks");
        Directory.CreateDirectory(hooksDir);
        var preCommitHook = Path.Combine(hooksDir, "pre-commit");
        await File.WriteAllTextAsync(preCommitHook, "#!/bin/sh\necho \"pre-commit hook declined\" >&2\nexit 1\n");

        await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["config", "core.hooksPath", hooksDir],
            WorkingDirectory = repo.Path,
        });

        await File.WriteAllTextAsync(Path.Combine(repo.Path, "a.txt"), "modified\n");
        var ctx = _host.Open(repo.Path);
        await ctx.Gateway.StageAsync(["a.txt"]);

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(async () =>
        {
            await ctx.Gateway.CommitAsync(new CommitRequest
            {
                Message = "test hook failure",
                AuthorName = "Bob",
                AuthorEmail = "bob@example.com",
            });
        });

        Assert.Equal(GitErrorKind.HookFailed, ex.Kind);
    }

    [Fact]
    public async Task Signed_commit_args_unchanged()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gw-signed-seed", r =>
        {
            r.WriteFile("a.txt", "initial\n").Stage().Commit("init");
        });

        var spy = new InterceptingGitCliService(_host.Cli)
        {
            Intercept = req =>
            {
                if (req.Arguments.Contains("commit"))
                {
                    return new GitCliResult(0, "[main 1234567] dummy", "", GitErrorKind.None, TimeSpan.Zero);
                }
                if (req.Arguments.Contains("rev-parse") && req.Arguments.Contains("HEAD"))
                {
                    return new GitCliResult(0, "1111222233334444555566667777888899990000\n", "", GitErrorKind.None, TimeSpan.Zero);
                }
                return null;
            }
        };

        var ctx = _host.Open(repo.Path, spy);
        await File.WriteAllTextAsync(Path.Combine(repo.Path, "a.txt"), "modified\n");
        await ctx.Gateway.StageAsync(["a.txt"]);

        var id = await ctx.Gateway.CommitAsync(new CommitRequest
        {
            Message = "signed commit",
            AuthorName = "Signer",
            AuthorEmail = "signer@example.com",
            CommitterName = "Committer",
            CommitterEmail = "committer@example.com",
            Sign = true,
            AmendPreviousCommit = true,
            AllowEmptyCommit = true,
        });

        Assert.Equal("1111222233334444555566667777888899990000", id.ToString());

        var commitReq = spy.Requests.FirstOrDefault(r => r.Arguments.Contains("commit"));
        Assert.NotNull(commitReq);
        Assert.Contains("-S", commitReq.Arguments);
        Assert.Contains("--amend", commitReq.Arguments);
        Assert.Contains("--allow-empty", commitReq.Arguments);
        Assert.Contains("--author", commitReq.Arguments);
        Assert.Contains("Signer <signer@example.com>", commitReq.Arguments);
        Assert.Equal("signed commit", commitReq.StandardInput);
        Assert.Equal("Committer", commitReq.ExtraEnvironment?["GIT_COMMITTER_NAME"]);
        Assert.Equal("committer@example.com", commitReq.ExtraEnvironment?["GIT_COMMITTER_EMAIL"]);
    }
}
