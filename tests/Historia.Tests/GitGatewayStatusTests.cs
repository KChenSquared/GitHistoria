namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class GitGatewayStatusTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Status_classifies_staged_unstaged_and_untracked()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gateway-status-seed", r =>
        {
            r.WriteFile("tracked.txt", "one\n").Stage().Commit("initial");
            r.WriteFile("tracked.txt", "one\ntwo\n");   // unstaged modification
            r.WriteFile("added.txt", "new\n").Stage("added.txt"); // staged add
            r.WriteFile("untracked.txt", "loose\n");    // untracked
        });

        var ctx = _host.Open(repo.Path);
        var status = await ctx.Gateway.GetStatusAsync();

        Assert.Contains(status.Staged, e => e.Path == "added.txt" && e.Kind == FileChangeKind.Added);
        Assert.Contains(status.Unstaged, e => e.Path == "tracked.txt" && e.Kind == FileChangeKind.Modified);
        Assert.Contains(status.Untracked, e => e.Path == "untracked.txt");
        Assert.True(status.IsDirty);
    }

    [Fact]
    public async Task A_staged_rename_reports_the_old_path()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gateway-staged-rename-seed", r =>
        {
            r.WriteFile("original.txt", "rename me, plenty of content so similarity detection kicks in\n")
                .Stage().Commit("initial");
        });
        repo.Git("mv", "original.txt", "renamed.txt");

        var ctx = _host.Open(repo.Path);
        var status = await ctx.Gateway.GetStatusAsync();

        var entry = Assert.Single(status.Staged, e => e.Kind == FileChangeKind.Renamed);
        Assert.Equal("renamed.txt", entry.Path);
        Assert.Equal("original.txt", entry.OldPath);
    }

    [Fact]
    public async Task A_staged_rename_with_edits_is_renamed_with_its_old_path()
    {
        var content = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"Line {i} content for similarity")) + "\n";
        using var repo = TestRepositoryBuilder.FromTemplate("gateway-staged-rename-edit-seed", r =>
        {
            r.WriteFile("original.txt", content).Stage().Commit("initial");
        });
        repo.Git("mv", "original.txt", "renamed.txt");
        File.AppendAllText(Path.Combine(repo.Path, "renamed.txt"), "Extra line\n");
        repo.Git("add", "renamed.txt");

        var ctx = _host.Open(repo.Path);
        var status = await ctx.Gateway.GetStatusAsync();

        var entry = Assert.Single(status.Staged, e => e.Path == "renamed.txt");
        Assert.Equal(FileChangeKind.Renamed, entry.Kind);
        Assert.Equal("original.txt", entry.OldPath);
    }

    [Fact]
    public async Task A_pure_workdir_move_is_one_unstaged_rename()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gateway-workdir-rename-seed", r =>
        {
            r.WriteFile("original.txt", "rename me, plenty of content so similarity detection kicks in\n")
                .Stage().Commit("initial");
        });
        File.Move(Path.Combine(repo.Path, "original.txt"), Path.Combine(repo.Path, "renamed.txt"));

        var ctx = _host.Open(repo.Path);
        var status = await ctx.Gateway.GetStatusAsync();

        var entry = Assert.Single(status.Unstaged, e => e.Kind == FileChangeKind.Renamed);
        Assert.Equal("renamed.txt", entry.Path);
        Assert.Equal("original.txt", entry.OldPath);
        Assert.DoesNotContain(status.Untracked, e => e.Path == "renamed.txt");
        Assert.DoesNotContain(status.Unstaged, e => e.Path == "original.txt" && e.Kind == FileChangeKind.Deleted);
    }

    [Fact]
    public async Task A_workdir_move_with_edits_is_deleted_plus_untracked()
    {
        var content = string.Join("\n", Enumerable.Range(1, 50).Select(i => $"Line {i} content for similarity")) + "\n";
        using var repo = TestRepositoryBuilder.FromTemplate("gateway-workdir-rename-edit-seed", r =>
        {
            r.WriteFile("original.txt", content).Stage().Commit("initial");
        });
        File.Move(Path.Combine(repo.Path, "original.txt"), Path.Combine(repo.Path, "renamed.txt"));
        File.AppendAllText(Path.Combine(repo.Path, "renamed.txt"), "Extra line\n");

        var ctx = _host.Open(repo.Path);
        var status = await ctx.Gateway.GetStatusAsync();

        Assert.Contains(status.Unstaged, e => e.Path == "original.txt" && e.Kind == FileChangeKind.Deleted);
        Assert.Contains(status.Untracked, e => e.Path == "renamed.txt");
        Assert.DoesNotContain(status.Unstaged, e => e.Kind == FileChangeKind.Renamed);
    }

    [Fact]
    public async Task Staging_a_paired_move_expands_to_both_paths()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gateway-stage-expansion-paired-move-seed", r =>
        {
            r.WriteFile("original.txt", "content for move\n").Stage().Commit("initial");
        });
        File.Move(Path.Combine(repo.Path, "original.txt"), Path.Combine(repo.Path, "renamed.txt"));

        var ctx = _host.Open(repo.Path);
        var status = await ctx.Gateway.GetStatusAsync();

        var expanded = StagePathExpansion.ForStaging(status.Unstaged, ["renamed.txt"]);
        Assert.Equal(["renamed.txt", "original.txt"], expanded);
    }

    [Fact]
    public async Task A_plain_modification_has_no_old_path()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gateway-plain-mod-seed", r =>
        {
            r.WriteFile("tracked.txt", "one\n").Stage().Commit("initial");
        });
        repo.WriteFile("tracked.txt", "one\ntwo\n");

        var ctx = _host.Open(repo.Path);
        var status = await ctx.Gateway.GetStatusAsync();

        var entry = Assert.Single(status.Unstaged, e => e.Path == "tracked.txt");
        Assert.Equal(FileChangeKind.Modified, entry.Kind);
        Assert.Null(entry.OldPath);
    }

    [Fact]
    public async Task Cancelling_status_throws_OperationCanceledException()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("gateway-status-cancel-seed", r =>
        {
            r.WriteFile("tracked.txt", "one\n").Stage().Commit("initial");
        });

        var ctx = _host.Open(repo.Path);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ctx.Gateway.GetStatusAsync(cts.Token));
    }
}
