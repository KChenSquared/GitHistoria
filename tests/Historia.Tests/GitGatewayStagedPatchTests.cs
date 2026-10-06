namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class GitGatewayStagedPatchTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<IReadOnlyList<(string Path, string Patch)>> Staged(TestRepositoryBuilder repo, bool amend = false)
    {
        await using var ctx = _host.Open(repo.Path);
        return await ctx.Gateway.GetStagedPatchesAsync(amend);
    }

    [Fact]
    public async Task Staged_add_modify_delete_and_rename_each_produce_one_chunk_with_the_right_path()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("mod.txt", "one\ntwo\n");
        repo.WriteFile("del.txt", "gone\n");
        repo.WriteFile("old.txt", "a\nb\nc\nd\ne\nf\ng\nh\ni\nj\n");
        repo.Stage().Commit("initial");

        repo.WriteFile("mod.txt", "one\nTWO\n");
        repo.WriteFile("add.txt", "new\n");
        File.Delete(Path.Combine(repo.Path, "del.txt"));
        repo.Git("mv", "old.txt", "renamed.txt");
        repo.Stage();

        var patches = await Staged(repo);

        Assert.Equal(["add.txt", "del.txt", "mod.txt", "renamed.txt"], patches.Select(p => p.Path).ToArray());
        Assert.Contains("+TWO", patches.Single(p => p.Path == "mod.txt").Patch);
        Assert.Contains("+new", patches.Single(p => p.Path == "add.txt").Patch);
        Assert.Contains("-gone", patches.Single(p => p.Path == "del.txt").Patch);
        Assert.Contains("rename from old.txt", patches.Single(p => p.Path == "renamed.txt").Patch);
    }

    [Fact]
    public async Task Unstaged_edits_are_excluded()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("a.txt", "1\n");
        repo.WriteFile("b.txt", "1\n");
        repo.Stage().Commit("initial");

        repo.WriteFile("a.txt", "2\n");
        repo.Stage("a.txt");
        repo.WriteFile("b.txt", "2\n");

        var patches = await Staged(repo);

        Assert.Equal("a.txt", Assert.Single(patches).Path);
    }

    [Fact]
    public async Task Nothing_staged_yields_no_patches()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("a.txt", "1\n");
        repo.Stage().Commit("initial");

        Assert.Empty(await Staged(repo));
    }

    [Fact]
    public async Task Amend_includes_the_head_commits_own_changes()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("base.txt", "base\n");
        repo.Stage().Commit("first");
        repo.WriteFile("head.txt", "in head\n");
        repo.Stage().Commit("second");
        repo.WriteFile("extra.txt", "staged now\n");
        repo.Stage("extra.txt");

        var normal = await Staged(repo);
        var amend = await Staged(repo, amend: true);

        Assert.Equal(["extra.txt"], normal.Select(p => p.Path).ToArray());
        Assert.Equal(["extra.txt", "head.txt"], amend.Select(p => p.Path).ToArray());
    }

    [Fact]
    public async Task Amending_a_root_commit_diffs_against_the_empty_tree()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("root.txt", "root\n");
        repo.Stage().Commit("root");
        repo.WriteFile("more.txt", "more\n");
        repo.Stage("more.txt");

        var amend = await Staged(repo, amend: true);

        Assert.Equal(["more.txt", "root.txt"], amend.Select(p => p.Path).ToArray());
        Assert.Contains("+root", amend.Single(p => p.Path == "root.txt").Patch);
    }

    [Fact]
    public async Task Paths_with_spaces_and_unicode_are_reported_verbatim()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("with space.txt", "x\n");
        repo.WriteFile("日本語.txt", "y\n");
        repo.Stage();

        var paths = (await Staged(repo)).Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray();

        Assert.Equal(["with space.txt", "日本語.txt"], paths);
    }

    [Fact]
    public async Task A_staged_binary_file_gets_its_own_path()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("text.txt", "t\n");
        repo.Stage().Commit("initial");
        File.WriteAllBytes(Path.Combine(repo.Path, "image.bin"), [0, 1, 2, 3, 0, 255, 0]);
        repo.Stage("image.bin");

        var patches = await Staged(repo);

        var binary = Assert.Single(patches);
        Assert.Equal("image.bin", binary.Path);
        Assert.Contains("Binary files", binary.Patch);
    }

    [Fact]
    public async Task A_mode_only_change_gets_its_own_path()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("run.sh", "echo hi\n");
        repo.WriteFile("other.txt", "o\n");
        repo.Stage().Commit("initial");
        repo.Git("update-index", "--chmod=+x", "run.sh");

        var patches = await Staged(repo);

        var chunk = Assert.Single(patches);
        Assert.Equal("run.sh", chunk.Path);
        Assert.Contains("new mode 100755", chunk.Patch);
    }

    [Fact]
    public async Task A_pure_rename_gets_the_new_path()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("before.txt", "same content\nline two\nline three\nline four\n");
        repo.Stage().Commit("initial");
        repo.Git("mv", "before.txt", "after.txt");

        var patches = await Staged(repo);

        var chunk = Assert.Single(patches);
        Assert.Equal("after.txt", chunk.Path);
        Assert.Contains("similarity index 100%", chunk.Patch);
    }

    [Fact]
    public async Task Patches_keep_lf_line_endings()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("a.txt", "one\n");
        repo.Stage().Commit("initial");
        repo.WriteFile("a.txt", "one\ntwo\n");
        repo.Stage();

        var patch = Assert.Single(await Staged(repo)).Patch;

        Assert.DoesNotContain("\r\n", patch);
    }
}
