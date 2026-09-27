namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class CommitFilesCliParityTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly LibGit2Service _lib = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Root_commit_parity()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("commit-files-root", b =>
        {
            b.WriteFile("a.txt", "line1\n");
            b.WriteFile("b.txt", "line2\n");
            b.Stage().Commit("root commit");
        });

        await using var ctx = _host.Open(repo.Path);
        var rootId = new GitObjectId(repo.HeadSha);

        using var git = new Repository(repo.Path);
        var libExpected = _lib.ProjectCommitFiles(git, rootId, parentIndex: 1);
        var cliActual = await ctx.Gateway.GetCommitFilesAsync(rootId, parentIndex: 1);

        Assert.Equal(libExpected.Count, cliActual.Count);
        var libSorted = libExpected.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var cliSorted = cliActual.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

        for (var i = 0; i < libSorted.Count; i++)
        {
            Assert.Equal(libSorted[i].Path, cliSorted[i].Path);
            Assert.Equal(libSorted[i].Kind, cliSorted[i].Kind);
            Assert.Equal(libSorted[i].OldPath, cliSorted[i].OldPath);
        }
    }

    [Fact]
    public async Task Add_modify_delete_parity()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("commit-files-amd", b =>
        {
            b.WriteFile("a.txt", "1\n");
            b.WriteFile("b.txt", "2\n");
            b.Stage().Commit("initial");

            b.WriteFile("a.txt", "1 modified\n");
            b.Git("rm", "b.txt");
            b.WriteFile("c.txt", "3 added\n");
            b.Stage().Commit("modify delete add");
        });

        await using var ctx = _host.Open(repo.Path);
        var commitId = new GitObjectId(repo.HeadSha);

        using var git = new Repository(repo.Path);
        var libExpected = _lib.ProjectCommitFiles(git, commitId, parentIndex: 1);
        var cliActual = await ctx.Gateway.GetCommitFilesAsync(commitId, parentIndex: 1);

        Assert.Equal(3, cliActual.Count);
        var libSorted = libExpected.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var cliSorted = cliActual.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

        for (var i = 0; i < libSorted.Count; i++)
        {
            Assert.Equal(libSorted[i].Path, cliSorted[i].Path);
            Assert.Equal(libSorted[i].Kind, cliSorted[i].Kind);
            Assert.Equal(libSorted[i].OldPath, cliSorted[i].OldPath);
        }
    }

    [Fact]
    public async Task Rename_at_50_percent_similarity()
    {
        // 10 lines of equal length (2 bytes each). 5 lines unchanged, 5 lines replaced -> exactly 50%
        const string oldContent = "1\n2\n3\n4\n5\n6\n7\n8\n9\n0\n";
        const string newContent = "1\n2\n3\n4\n5\na\nb\nc\nd\ne\n";

        using var repo = TestRepositoryBuilder.FromTemplate("commit-files-rename-50", b =>
        {
            b.WriteFile("old_50.txt", oldContent);
            b.Stage().Commit("before rename");

            b.Git("mv", "old_50.txt", "renamed_50.txt");
            b.WriteFile("renamed_50.txt", newContent);
            b.Stage().Commit("after rename 50");
        });

        await using var ctx = _host.Open(repo.Path);
        var commitId = new GitObjectId(repo.HeadSha);

        var cliActual = await ctx.Gateway.GetCommitFilesAsync(commitId, parentIndex: 1);
        var entry = Assert.Single(cliActual);
        Assert.Equal("renamed_50.txt", entry.Path);
        Assert.Equal("old_50.txt", entry.OldPath);
        Assert.Equal(FileChangeKind.Renamed, entry.Kind);
    }

    [Fact]
    public async Task Rename_with_edits()
    {
        const string oldContent = "stable_01\nstable_02\nstable_03\nstable_04\nstable_05\nstable_06\nstable_07\nstable_08\nstable_09\nstable_10\n";
        const string newContent = "stable_01\nstable_02\nstable_03\nstable_04\nstable_05\nstable_06\nstable_07\nstable_08\nstable_09\nmodified10\n";

        using var repo = TestRepositoryBuilder.FromTemplate("commit-files-rename-edits", b =>
        {
            b.WriteFile("orig.txt", oldContent);
            b.Stage().Commit("c1");

            b.Git("mv", "orig.txt", "renamed.txt");
            b.WriteFile("renamed.txt", newContent);
            b.Stage().Commit("c2");
        });

        await using var ctx = _host.Open(repo.Path);
        var commitId = new GitObjectId(repo.HeadSha);

        var cliActual = await ctx.Gateway.GetCommitFilesAsync(commitId, parentIndex: 1);
        var entry = Assert.Single(cliActual);
        Assert.Equal("renamed.txt", entry.Path);
        Assert.Equal("orig.txt", entry.OldPath);
        Assert.Equal(FileChangeKind.Renamed, entry.Kind);
    }

    [Fact]
    public async Task Each_parent_of_a_merge()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("commit-files-merge", b =>
        {
            b.WriteFile("base.txt", "base\n").Stage().Commit("base");
            b.Checkout("feature", create: true).WriteFile("feature.txt", "feat\n").Stage().Commit("on feature");
            b.Checkout("main").WriteFile("main.txt", "main\n").Stage().Commit("on main");
            b.Git("merge", "--no-ff", "feature", "-m", "merge feature into main");
        });

        await using var ctx = _host.Open(repo.Path);
        var mergeId = new GitObjectId(repo.HeadSha);

        using var git = new Repository(repo.Path);
        var libParent1 = _lib.ProjectCommitFiles(git, mergeId, parentIndex: 1);
        var libParent2 = _lib.ProjectCommitFiles(git, mergeId, parentIndex: 2);

        var cliParent1 = await ctx.Gateway.GetCommitFilesAsync(mergeId, parentIndex: 1);
        var cliParent2 = await ctx.Gateway.GetCommitFilesAsync(mergeId, parentIndex: 2);

        // Parity with libgit2 for parent 1 (merges feature.txt)
        Assert.Contains(cliParent1, f => f.Path == "feature.txt" && f.Kind == FileChangeKind.Added);
        Assert.DoesNotContain(cliParent1, f => f.Path == "main.txt");
        Assert.Equal(libParent1.Count, cliParent1.Count);

        // Parity with libgit2 for parent 2 (merges main.txt)
        Assert.Contains(cliParent2, f => f.Path == "main.txt" && f.Kind == FileChangeKind.Added);
        Assert.DoesNotContain(cliParent2, f => f.Path == "feature.txt");
        Assert.Equal(libParent2.Count, cliParent2.Count);
    }

    [Fact]
    public async Task Non_ascii_paths_parity_with_libgit2()
    {
        const string unicodePath = "folder/café_☕_тест.txt";
        const string spacedPath = "folder/spaced name file.txt";

        using var repo = TestRepositoryBuilder.FromTemplate("commit-files-non-ascii", b =>
        {
            b.WriteFile(unicodePath, "unicode content\n");
            b.WriteFile(spacedPath, "spaced content\n");
            b.Stage().Commit("special paths");
        });

        await using var ctx = _host.Open(repo.Path);
        var commitId = new GitObjectId(repo.HeadSha);

        using var git = new Repository(repo.Path);
        var libExpected = _lib.ProjectCommitFiles(git, commitId, parentIndex: 1);
        var cliActual = await ctx.Gateway.GetCommitFilesAsync(commitId, parentIndex: 1);

        Assert.Equal(2, cliActual.Count);
        Assert.Contains(cliActual, f => f.Path == unicodePath && f.Kind == FileChangeKind.Added);
        Assert.Contains(cliActual, f => f.Path == spacedPath && f.Kind == FileChangeKind.Added);

        var libSorted = libExpected.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var cliSorted = cliActual.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

        for (var i = 0; i < libSorted.Count; i++)
        {
            Assert.Equal(libSorted[i].Path, cliSorted[i].Path);
            Assert.Equal(libSorted[i].Kind, cliSorted[i].Kind);
        }
    }

    [Fact]
    public void ParseCommitFiles_handles_tabs_newlines_and_non_ascii_in_raw_nul_stream()
    {
        const string rawStdout = "A\0path/with\ttab/file.txt\0M\0path/with\nnewline/file.txt\0R100\0old/café_☕.txt\0new/café_☕.txt\0";
        var entries = GitGateway.ParseCommitFiles(rawStdout);

        Assert.Equal(3, entries.Count);

        Assert.Equal("path/with\ttab/file.txt", entries[0].Path);
        Assert.Equal(FileChangeKind.Added, entries[0].Kind);
        Assert.Null(entries[0].OldPath);

        Assert.Equal("path/with\nnewline/file.txt", entries[1].Path);
        Assert.Equal(FileChangeKind.Modified, entries[1].Kind);
        Assert.Null(entries[1].OldPath);

        Assert.Equal("new/café_☕.txt", entries[2].Path);
        Assert.Equal("old/café_☕.txt", entries[2].OldPath);
        Assert.Equal(FileChangeKind.Renamed, entries[2].Kind);
    }
}
