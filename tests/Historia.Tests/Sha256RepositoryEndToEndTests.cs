namespace Historia.Tests;

/// <summary>
/// End-to-end integration tests for SHA-256 repositories:
/// Exercises repository creation with --object-format=sha256, branches, tags, stashes,
/// discovery, open, status, refs, graph walk, commit detail/files, file diff,
/// staging, committing, checkout, and GraphMetaStore disk spill + reopen.
/// </summary>
[Trait("tier", "git")]
public sealed class Sha256RepositoryEndToEndTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private async Task<bool> IsSha256SupportedAsync()
    {
        var probeDir = TestScratch.NewDirectory("sha256-probe-");
        var result = await _host.Cli.RunAsync(new GitCliRequest
        {
            Arguments = ["init", "--object-format=sha256", probeDir],
            WorkingDirectory = probeDir,
        });
        return result.IsSuccess;
    }

    [Fact]
    public async Task Sha256_repository_full_lifecycle_end_to_end()
    {
        if (!await IsSha256SupportedAsync())
        {
            // Git version under test lacks SHA-256 support
            return;
        }

        var repoDir = TestScratch.NewDirectory("sha256-fixture-");
        var cli = _host.Cli;

        // 1. Initialize SHA-256 repo
        var init = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["init", "--object-format=sha256", "-b", "main", repoDir],
            WorkingDirectory = repoDir,
        });
        Assert.True(init.IsSuccess, init.StandardError);

        // Configure local identity
        await cli.RunAsync(new GitCliRequest { Arguments = ["config", "user.name", "SHA256 Tester"], WorkingDirectory = repoDir });
        await cli.RunAsync(new GitCliRequest { Arguments = ["config", "user.email", "tester@example.com"], WorkingDirectory = repoDir });

        // Commit 1 on main
        var file1 = Path.Combine(repoDir, "hello.txt");
        await File.WriteAllTextAsync(file1, "Hello world\n");
        await cli.RunAsync(new GitCliRequest { Arguments = ["add", "hello.txt"], WorkingDirectory = repoDir });
        await cli.RunAsync(new GitCliRequest { Arguments = ["commit", "-m", "commit 1 on main"], WorkingDirectory = repoDir });

        // Commit 2 on main
        await File.AppendAllTextAsync(file1, "Second line added\n");
        await cli.RunAsync(new GitCliRequest { Arguments = ["add", "hello.txt"], WorkingDirectory = repoDir });
        await cli.RunAsync(new GitCliRequest { Arguments = ["commit", "-m", "commit 2 on main"], WorkingDirectory = repoDir });

        // Annotated tag on commit 2
        await cli.RunAsync(new GitCliRequest { Arguments = ["tag", "-a", "v1.0", "-m", "Release 1.0 tag"], WorkingDirectory = repoDir });

        // Branch feature and commit 3
        await cli.RunAsync(new GitCliRequest { Arguments = ["checkout", "-b", "feature"], WorkingDirectory = repoDir });
        var file2 = Path.Combine(repoDir, "feature.txt");
        await File.WriteAllTextAsync(file2, "Feature content\n");
        await cli.RunAsync(new GitCliRequest { Arguments = ["add", "feature.txt"], WorkingDirectory = repoDir });
        await cli.RunAsync(new GitCliRequest { Arguments = ["commit", "-m", "commit 3 on feature"], WorkingDirectory = repoDir });

        // Stash on feature branch
        await File.AppendAllTextAsync(file2, "Uncommitted work for stash\n");
        await cli.RunAsync(new GitCliRequest { Arguments = ["stash", "push", "-m", "WIP on feature branch"], WorkingDirectory = repoDir });

        // Checkout main
        await cli.RunAsync(new GitCliRequest { Arguments = ["checkout", "main"], WorkingDirectory = repoDir });

        // 2. Open via MiniHost
        await using var ctx = _host.Open(repoDir);
        Assert.False(ctx.IsBare);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoDir)), Path.TrimEndingDirectorySeparator(ctx.RepositoryPath));

        var repoInfo = await ctx.Gateway.GetRepositoryInfoAsync();
        Assert.Equal("main", repoInfo.CurrentBranchName);

        // 3. Status
        var status = await ctx.Gateway.GetStatusAsync();
        Assert.False(status.IsDirty);

        // 4. Branches
        var branches = await ctx.Gateway.GetBranchesAsync();
        var mainBranch = Assert.Single(branches, b => b.Name == "main" && b.IsCurrent);
        Assert.Contains(branches, b => b.Name == "feature");
        foreach (var b in branches)
        {
            Assert.NotNull(b.TipCommitId);
            Assert.Equal(32, b.TipCommitId!.Value.HashSize);
            Assert.Equal(64, b.TipCommitId!.Value.ToString().Length);
        }

        // 5. Tags
        var tags = await ctx.Gateway.GetTagsAsync();
        var tag = Assert.Single(tags, t => t.Name == "v1.0");
        Assert.Equal(32, tag.TargetCommitId.HashSize);
        Assert.Equal(64, tag.TargetCommitId.ToString().Length);
        Assert.Equal("Release 1.0 tag\n", tag.Message);

        // 6. Graph walk and layout
        var layoutService = new GraphLayoutService(_host.Settings, _host.Locator);
        var layoutResult = await layoutService.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });
        Assert.True(layoutResult.Layout.RowCount >= 3);
        for (var i = 0; i < layoutResult.Layout.RowCount; i++)
        {
            var commit = layoutResult.Layout.Commits[i];
            Assert.Equal(32, commit.Id.HashSize);
            Assert.Equal(64, commit.Id.ToString().Length);
        }

        // 7. Commit detail and files
        var headSha = mainBranch.TipCommitId!.Value;
        var detail = await ctx.Gateway.GetCommitDetailAsync(headSha);
        Assert.Equal(headSha, detail.Id);
        Assert.Contains("commit 2 on main", detail.Message);

        var commitFiles = await ctx.Gateway.GetCommitFilesAsync(headSha, parentIndex: 1);
        var entry = Assert.Single(commitFiles);
        Assert.Equal("hello.txt", entry.Path);

        // 8. File diff
        var diff = await ctx.Gateway.GetFileDiffAsync(new DiffRequest
        {
            Path = "hello.txt",
            Source = DiffSource.Commit,
            CommitId = headSha,
            ParentIndex = 1,
        });
        Assert.Equal(DiffKind.Modified, diff.Kind);
        Assert.NotEmpty(diff.Hunks);
        var hunk = Assert.Single(diff.Hunks);
        Assert.Contains(hunk.Lines, l => l.Content.Contains("Second line added"));

        // 9. Stage + commit
        var newFile = Path.Combine(repoDir, "stage-file.txt");
        await File.WriteAllTextAsync(newFile, "Staged file content\n");
        var dirtyStatus = await ctx.Gateway.GetStatusAsync();
        Assert.True(dirtyStatus.IsDirty);
        Assert.Contains(dirtyStatus.Untracked, f => f.Path == "stage-file.txt");

        await ctx.Gateway.StageAsync(["stage-file.txt"]);
        var stagedStatus = await ctx.Gateway.GetStatusAsync();
        Assert.Contains(stagedStatus.Staged, f => f.Path == "stage-file.txt");

        var newCommit = await ctx.Gateway.CommitAsync(new CommitRequest
        {
            Message = "commit 4 staged on main",
            AuthorName = "SHA256 Tester",
            AuthorEmail = "tester@example.com",
        });
        Assert.Equal(32, newCommit.HashSize);
        Assert.Equal(64, newCommit.ToString().Length);

        var postCommitStatus = await ctx.Gateway.GetStatusAsync();
        Assert.False(postCommitStatus.IsDirty);

        // 10. Checkout
        await ctx.Gateway.CheckoutAsync("feature", new GitCheckoutOptions());
        var featureRepoInfo = await ctx.Gateway.GetRepositoryInfoAsync();
        Assert.Equal("feature", featureRepoInfo.CurrentBranchName);

        // 11. GraphMetaStore spill + reopen
        await _host.Settings.UpdateAsync(s => s with { GraphMetadataFromDisk = true });
        layoutService.Forget(ctx);

        var diskLayoutResult = await layoutService.ComputeLayoutAsync(ctx, new GraphQuery { RefScope = GraphRefScope.AllRefs });
        Assert.IsType<DiskCommitStore>(diskLayoutResult.Layout.Commits);
        Assert.True(diskLayoutResult.Layout.RowCount >= 4);
        for (var i = 0; i < diskLayoutResult.Layout.RowCount; i++)
        {
            var commit = diskLayoutResult.Layout.Commits[i];
            Assert.Equal(32, commit.Id.HashSize);
            Assert.Equal(64, commit.Id.ToString().Length);
        }

        // Verify disk files on disk
        var hdrPath = GraphCachePaths.File(repoDir, GraphMetaStore.HdrExt);
        Assert.True(File.Exists(hdrPath));

        var existing = GraphMetaStore.ProbeExisting(repoDir, includeRemoteBranches: true);
        Assert.True(existing.Ok);
        Assert.True(existing.RowCount >= 4);

        var probe = GraphMetaStore.Probe(repoDir, existing.WalkKey, includeRemoteBranches: true);
        Assert.True(probe.Ok);

        using var reopenedStore = GraphMetaStore.TryOpen(repoDir, existing.WalkKey, includeRemoteBranches: true, wantCount: 1);
        Assert.NotNull(reopenedStore);
        Assert.Equal(32, reopenedStore[0].Id.HashSize);

        layoutService.Forget(ctx);
    }
}
