namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class FileDiffCliParityTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    private static void AssertParity(FileDiff actual, LibGit2Sharp.PatchEntryChanges? expected, string path)
    {
        Assert.Equal(path, actual.Path);
        if (expected is null)
        {
            Assert.Empty(actual.Hunks);
            Assert.Equal(0, actual.AddedLines);
            Assert.Equal(0, actual.DeletedLines);
            return;
        }

        var expectedKind = expected.Status switch
        {
            LibGit2Sharp.ChangeKind.Added => DiffKind.Added,
            LibGit2Sharp.ChangeKind.Deleted => DiffKind.Deleted,
            LibGit2Sharp.ChangeKind.Renamed => DiffKind.Renamed,
            LibGit2Sharp.ChangeKind.Copied => DiffKind.Copied,
            LibGit2Sharp.ChangeKind.TypeChanged => DiffKind.TypeChanged,
            _ => DiffKind.Modified,
        };
        Assert.Equal(expectedKind, actual.Kind);

        var expectedOldPath = expected.OldPath == path ? null : expected.OldPath;
        Assert.Equal(expectedOldPath, actual.OldPath);
        Assert.Equal(expected.IsBinaryComparison, actual.IsBinary);

        if (expected.IsBinaryComparison)
        {
            Assert.True(actual.IsBinary);
            Assert.Empty(actual.Hunks);
            return;
        }

        Assert.Equal(expected.LinesAdded, actual.AddedLines);
        Assert.Equal(expected.LinesDeleted, actual.DeletedLines);
    }

    [Fact]
    public async Task Modify_unstaged_parity()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("diff-unstaged-mod", b =>
        {
            b.WriteFile("file.txt", "line1\nline2\n");
            b.Stage().Commit("initial");
            b.WriteFile("file.txt", "line1\nmodified line2\nline3\n");
        });

        await using var ctx = _host.Open(repo.Path);
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "file.txt" };

        var actual = await ctx.Gateway.GetFileDiffAsync(req);

        using var git = new LibGit2Sharp.Repository(repo.Path);
        var libPatch = git.Diff.Compare<LibGit2Sharp.Patch>(["file.txt"], includeUntracked: true, null, new LibGit2Sharp.CompareOptions { ContextLines = 3, Similarity = LibGit2Sharp.SimilarityOptions.Renames });
        var expected = libPatch["file.txt"];

        AssertParity(actual, expected, "file.txt");
        Assert.NotEmpty(actual.Hunks);
    }

    [Fact]
    public async Task Add_staged_and_unstaged_parity()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("diff-add-parity", b =>
        {
            b.WriteFile("seed.txt", "seed\n");
            b.Stage().Commit("initial");
            b.WriteFile("new-staged.txt", "staged line\n");
            b.Stage();
        });

        await using var ctx = _host.Open(repo.Path);
        var req = new DiffRequest { Source = DiffSource.StagedChanges, Path = "new-staged.txt" };

        var actual = await ctx.Gateway.GetFileDiffAsync(req);

        using var git = new LibGit2Sharp.Repository(repo.Path);
        var libPatch = git.Diff.Compare<LibGit2Sharp.Patch>(git.Head.Tip.Tree, LibGit2Sharp.DiffTargets.Index, ["new-staged.txt"], null, new LibGit2Sharp.CompareOptions { ContextLines = 3, Similarity = LibGit2Sharp.SimilarityOptions.Renames });
        var expected = libPatch["new-staged.txt"];

        AssertParity(actual, expected, "new-staged.txt");
        Assert.Equal(DiffKind.Added, actual.Kind);
    }

    [Fact]
    public async Task Delete_unstaged_parity()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("diff-del-parity", b =>
        {
            b.WriteFile("del.txt", "to be deleted\n");
            b.Stage().Commit("initial");
            File.Delete(Path.Combine(b.Path, "del.txt"));
        });

        await using var ctx = _host.Open(repo.Path);
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "del.txt" };

        var actual = await ctx.Gateway.GetFileDiffAsync(req);

        using var git = new LibGit2Sharp.Repository(repo.Path);
        var libPatch = git.Diff.Compare<LibGit2Sharp.Patch>(["del.txt"], includeUntracked: true, null, new LibGit2Sharp.CompareOptions { ContextLines = 3, Similarity = LibGit2Sharp.SimilarityOptions.Renames });
        var expected = libPatch["del.txt"];

        AssertParity(actual, expected, "del.txt");
        Assert.Equal(DiffKind.Deleted, actual.Kind);
    }

    [Fact]
    public async Task Rename_workdir_staged_and_commit_parity()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("diff-rename-parity", b =>
        {
            b.WriteFile("orig.txt", "rename content that is identical\n");
            b.Stage().Commit("initial");

            // Staged rename
            b.Git("mv", "orig.txt", "renamed.txt");
        });

        await using var ctx = _host.Open(repo.Path);

        // Staged rename
        var reqStaged = new DiffRequest { Source = DiffSource.StagedChanges, Path = "renamed.txt" };
        var actualStaged = await ctx.Gateway.GetFileDiffAsync(reqStaged);

        using (var git = new LibGit2Sharp.Repository(repo.Path))
        {
            var libPatch = git.Diff.Compare<LibGit2Sharp.Patch>(git.Head.Tip.Tree, LibGit2Sharp.DiffTargets.Index, ["renamed.txt"], null, new LibGit2Sharp.CompareOptions { ContextLines = 3, Similarity = LibGit2Sharp.SimilarityOptions.Renames });
            var expected = libPatch["renamed.txt"];
            AssertParity(actualStaged, expected, "renamed.txt");
        }

        // Commit rename
        repo.Git("commit", "-m", "renamed commit");
        var commitId = new GitObjectId(repo.HeadSha);
        var reqCommit = new DiffRequest { Source = DiffSource.Commit, CommitId = commitId, Path = "renamed.txt" };
        var actualCommit = await ctx.Gateway.GetFileDiffAsync(reqCommit);

        using (var git = new LibGit2Sharp.Repository(repo.Path))
        {
            var c = git.Lookup<LibGit2Sharp.Commit>(repo.HeadSha);
            var libPatch = git.Diff.Compare<LibGit2Sharp.Patch>(c.Parents.First().Tree, c.Tree, ["renamed.txt"], null, new LibGit2Sharp.CompareOptions { ContextLines = 3, Similarity = LibGit2Sharp.SimilarityOptions.Renames });
            var expected = libPatch["renamed.txt"];
            AssertParity(actualCommit, expected, "renamed.txt");
        }
    }

    [Fact]
    public async Task Binary_file_parity()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("diff-binary-parity", b =>
        {
            var bin1 = new byte[] { 0, 1, 2, 3, 4, 5, 255 };
            File.WriteAllBytes(Path.Combine(b.Path, "data.bin"), bin1);
            b.Stage().Commit("binary commit");

            var bin2 = new byte[] { 0, 1, 2, 99, 4, 5, 255 };
            File.WriteAllBytes(Path.Combine(b.Path, "data.bin"), bin2);
        });

        await using var ctx = _host.Open(repo.Path);
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "data.bin" };

        var actual = await ctx.Gateway.GetFileDiffAsync(req);

        using var git = new LibGit2Sharp.Repository(repo.Path);
        var libPatch = git.Diff.Compare<LibGit2Sharp.Patch>(["data.bin"], includeUntracked: true, null, new LibGit2Sharp.CompareOptions { ContextLines = 3, Similarity = LibGit2Sharp.SimilarityOptions.Renames });
        var expected = libPatch["data.bin"];

        AssertParity(actual, expected, "data.bin");
        Assert.True(actual.IsBinary);
    }

    [Fact]
    public async Task Crlf_file_parity()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("diff-crlf-parity", b =>
        {
            b.WriteFile("crlf.txt", "line1\r\nline2\r\n");
            b.Stage().Commit("initial");
            b.WriteFile("crlf.txt", "line1\r\nline2 modified\r\nline3\r\n");
        });

        await using var ctx = _host.Open(repo.Path);
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "crlf.txt" };

        var actual = await ctx.Gateway.GetFileDiffAsync(req);

        using var git = new LibGit2Sharp.Repository(repo.Path);
        var libPatch = git.Diff.Compare<LibGit2Sharp.Patch>(["crlf.txt"], includeUntracked: true, null, new LibGit2Sharp.CompareOptions { ContextLines = 3, Similarity = LibGit2Sharp.SimilarityOptions.Renames });
        var expected = libPatch["crlf.txt"];

        AssertParity(actual, expected, "crlf.txt");
        Assert.NotEmpty(actual.Hunks);
    }

    [Fact]
    public async Task Untracked_file_parity()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("diff-untracked-parity", b =>
        {
            b.WriteFile("tracked.txt", "tracked\n");
            b.Stage().Commit("initial");
            b.WriteFile("untracked.txt", "untracked line 1\nuntracked line 2\n");
        });

        await using var ctx = _host.Open(repo.Path);
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "untracked.txt" };

        var actual = await ctx.Gateway.GetFileDiffAsync(req);

        using var git = new LibGit2Sharp.Repository(repo.Path);
        var libPatch = git.Diff.Compare<LibGit2Sharp.Patch>(["untracked.txt"], includeUntracked: true, null, new LibGit2Sharp.CompareOptions { ContextLines = 3, Similarity = LibGit2Sharp.SimilarityOptions.Renames });
        var expected = libPatch["untracked.txt"];

        AssertParity(actual, expected, "untracked.txt");
        Assert.Equal(DiffKind.Added, actual.Kind);
        Assert.Equal(2, actual.AddedLines);
        Assert.Equal(0, actual.DeletedLines);
    }

    [Fact]
    public async Task Unborn_head_staged_parity()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("diff-unborn-parity", b =>
        {
            b.WriteFile("first.txt", "first file content\n");
            b.Stage();
        });

        await using var ctx = _host.Open(repo.Path);
        var req = new DiffRequest { Source = DiffSource.StagedChanges, Path = "first.txt" };

        var actual = await ctx.Gateway.GetFileDiffAsync(req);

        using var git = new LibGit2Sharp.Repository(repo.Path);
        var libPatch = git.Diff.Compare<LibGit2Sharp.Patch>(git.Head.Tip?.Tree, LibGit2Sharp.DiffTargets.Index, ["first.txt"], null, new LibGit2Sharp.CompareOptions { ContextLines = 3, Similarity = LibGit2Sharp.SimilarityOptions.Renames });
        var expected = libPatch["first.txt"];

        AssertParity(actual, expected, "first.txt");
        Assert.Equal(DiffKind.Added, actual.Kind);
        Assert.Equal(1, actual.AddedLines);
    }

    [Fact]
    public async Task Root_commit_parity()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("diff-root-parity", b =>
        {
            b.WriteFile("root.txt", "root line 1\nroot line 2\n");
            b.Stage().Commit("root commit");
        });

        await using var ctx = _host.Open(repo.Path);
        var rootId = new GitObjectId(repo.HeadSha);
        var req = new DiffRequest { Source = DiffSource.Commit, CommitId = rootId, Path = "root.txt" };

        var actual = await ctx.Gateway.GetFileDiffAsync(req);

        using var git = new LibGit2Sharp.Repository(repo.Path);
        var c = git.Lookup<LibGit2Sharp.Commit>(repo.HeadSha);
        var libPatch = git.Diff.Compare<LibGit2Sharp.Patch>(null, c.Tree, ["root.txt"], null, new LibGit2Sharp.CompareOptions { ContextLines = 3, Similarity = LibGit2Sharp.SimilarityOptions.Renames });
        var expected = libPatch["root.txt"];

        AssertParity(actual, expected, "root.txt");
        Assert.Equal(DiffKind.Added, actual.Kind);
        Assert.Equal(2, actual.AddedLines);
    }

    [Fact]
    public async Task Merge_parents_parity()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("diff-merge-parity", b =>
        {
            b.WriteFile("common.txt", "common\n");
            b.Stage().Commit("base");

            b.Git("checkout", "-b", "feature");
            b.WriteFile("common.txt", "feature change\n");
            b.Stage().Commit("feature commit");

            b.Git("checkout", "main");
            b.WriteFile("other.txt", "main change\n");
            b.Stage().Commit("main commit");

            b.Git("merge", "feature", "--no-ff", "-m", "merge commit");
        });

        await using var ctx = _host.Open(repo.Path);
        var mergeId = new GitObjectId(repo.HeadSha);

        // Diff against parent 1
        var reqP1 = new DiffRequest { Source = DiffSource.Commit, CommitId = mergeId, ParentIndex = 1, Path = "common.txt" };
        var actualP1 = await ctx.Gateway.GetFileDiffAsync(reqP1);

        using (var git = new LibGit2Sharp.Repository(repo.Path))
        {
            var c = git.Lookup<LibGit2Sharp.Commit>(repo.HeadSha);
            var libPatch = git.Diff.Compare<LibGit2Sharp.Patch>(c.Parents.ElementAt(0).Tree, c.Tree, ["common.txt"], null, new LibGit2Sharp.CompareOptions { ContextLines = 3, Similarity = LibGit2Sharp.SimilarityOptions.Renames });
            var expected = libPatch["common.txt"];
            AssertParity(actualP1, expected, "common.txt");
        }

        // Diff against parent 2
        var reqP2 = new DiffRequest { Source = DiffSource.Commit, CommitId = mergeId, ParentIndex = 2, Path = "common.txt" };
        var actualP2 = await ctx.Gateway.GetFileDiffAsync(reqP2);

        using (var git = new LibGit2Sharp.Repository(repo.Path))
        {
            var c = git.Lookup<LibGit2Sharp.Commit>(repo.HeadSha);
            var libPatch = git.Diff.Compare<LibGit2Sharp.Patch>(c.Parents.ElementAt(1).Tree, c.Tree, ["common.txt"], null, new LibGit2Sharp.CompareOptions { ContextLines = 3, Similarity = LibGit2Sharp.SimilarityOptions.Renames });
            var expected = libPatch["common.txt"];
            AssertParity(actualP2, expected, "common.txt");
        }
    }

    [Fact]
    public async Task Comparison_parity()
    {
        var c1Sha = "";
        var c2Sha = "";
        using var repo = TestRepositoryBuilder.FromTemplate("diff-cmp-parity", b =>
        {
            b.WriteFile("file.txt", "v1\n");
            b.Stage().Commit("c1");
            c1Sha = b.HeadSha;

            b.WriteFile("file.txt", "v2\n");
            b.Stage().Commit("c2");
            c2Sha = b.HeadSha;
        });

        await using var ctx = _host.Open(repo.Path);
        var req = new DiffRequest
        {
            Source = DiffSource.Comparison,
            CommitId = new GitObjectId(c1Sha),
            OtherCommitId = new GitObjectId(c2Sha),
            Path = "file.txt",
        };

        var actual = await ctx.Gateway.GetFileDiffAsync(req);

        using var git = new LibGit2Sharp.Repository(repo.Path);
        var left = git.Lookup<LibGit2Sharp.Commit>(c1Sha);
        var right = git.Lookup<LibGit2Sharp.Commit>(c2Sha);
        var libPatch = git.Diff.Compare<LibGit2Sharp.Patch>(left.Tree, right.Tree, ["file.txt"], null, new LibGit2Sharp.CompareOptions { ContextLines = 3, Similarity = LibGit2Sharp.SimilarityOptions.Renames });
        var expected = libPatch["file.txt"];

        AssertParity(actual, expected, "file.txt");
    }

    [Fact]
    public async Task Spaces_and_unicode_path_parity()
    {
        const string tricky = "folder with spaces/日本語 file — with spaces.txt";
        using var repo = TestRepositoryBuilder.FromTemplate("diff-tricky-path", b =>
        {
            b.WriteFile(tricky, "initial line\n");
            b.Stage().Commit("initial");
            b.WriteFile(tricky, "initial line\nmodified line\n");
        });

        await using var ctx = _host.Open(repo.Path);
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = tricky };

        var actual = await ctx.Gateway.GetFileDiffAsync(req);

        using var git = new LibGit2Sharp.Repository(repo.Path);
        var libPatch = git.Diff.Compare<LibGit2Sharp.Patch>([tricky], includeUntracked: true, null, new LibGit2Sharp.CompareOptions { ContextLines = 3, Similarity = LibGit2Sharp.SimilarityOptions.Renames });
        var expected = libPatch[tricky];

        AssertParity(actual, expected, tricky);
    }

    [Fact]
    public async Task Diff_user_configs_do_not_alter_result()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("diff-config-immunity", b =>
        {
            b.WriteFile("file.txt", "line1\nline2\n");
            b.Stage().Commit("initial");

            // Configure diff.noprefix, diff.external, and a textconv driver
            b.Git("config", "diff.noprefix", "true");
            b.Git("config", "diff.external", "false"); // 'false' command fails if called
            b.Git("config", "diff.dummy.textconv", "cat");
            b.WriteFile(".gitattributes", "file.txt diff=dummy\n");
            b.Stage().Commit("attrs");

            b.WriteFile("file.txt", "line1\nmodified line2\nline3\n");
        });

        await using var ctx = _host.Open(repo.Path);
        var req = new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "file.txt" };

        var actual = await ctx.Gateway.GetFileDiffAsync(req);

        using var git = new LibGit2Sharp.Repository(repo.Path);
        var libPatch = git.Diff.Compare<LibGit2Sharp.Patch>(["file.txt"], includeUntracked: true, null, new LibGit2Sharp.CompareOptions { ContextLines = 3, Similarity = LibGit2Sharp.SimilarityOptions.Renames });
        var expected = libPatch["file.txt"];

        AssertParity(actual, expected, "file.txt");
        Assert.Contains("--- a/file.txt", actual.RawPatch);
        Assert.Contains("+++ b/file.txt", actual.RawPatch);
    }

    [Fact]
    public async Task Large_file_exceeding_budget_is_truncated_and_counts_match_libgit2()
    {
        // 6 MB text file staged add
        using var repo = TestRepositoryBuilder.FromTemplate("diff-large-file", b =>
        {
            b.WriteFile("seed.txt", "seed\n");
            b.Stage().Commit("initial");

            var sb = new StringBuilder(6 * 1024 * 1024 + 100);
            for (var i = 0; i < 120_000; i++)
            {
                sb.Append("line of content for large file test ").Append(i).Append('\n');
            }
            File.WriteAllText(Path.Combine(b.Path, "large.txt"), sb.ToString());
            b.Stage();
        });

        await using var ctx = _host.Open(repo.Path);
        var req = new DiffRequest { Source = DiffSource.StagedChanges, Path = "large.txt" };

        var actual = await ctx.Gateway.GetFileDiffAsync(req);

        using var git = new LibGit2Sharp.Repository(repo.Path);
        var libPatch = git.Diff.Compare<LibGit2Sharp.Patch>(git.Head.Tip.Tree, LibGit2Sharp.DiffTargets.Index, ["large.txt"], null, new LibGit2Sharp.CompareOptions { ContextLines = 3, Similarity = LibGit2Sharp.SimilarityOptions.Renames });
        var expected = libPatch["large.txt"];

        Assert.True(actual.IsTruncated);
        Assert.Empty(actual.Hunks);
        Assert.Equal(expected.LinesAdded, actual.AddedLines);
        Assert.Equal(expected.LinesDeleted, actual.DeletedLines);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Raw_patch_keeps_line_endings_and_a_hunk_stages(string eol)
    {
        // Line-mode capture re-joined stdout with Environment.NewLine: an LF patch came back
        // CRLF on Windows (and real CRs were lost), so git apply rejected every staged hunk.
        using var repo = TestRepositoryBuilder.FromTemplate("diff-eol-stage-" + (eol == "\n" ? "lf" : "crlf"), b =>
        {
            b.Git("config", "core.autocrlf", "false");
            var lines = Enumerable.Range(1, 20).Select(i => $"line {i}").ToArray();
            b.WriteFile("f.txt", string.Join(eol, lines) + eol).Stage().Commit("base");
            lines[1] = "changed 2";
            lines[17] = "changed 18";
            b.WriteFile("f.txt", string.Join(eol, lines) + eol);
        });

        await using var ctx = _host.Open(repo.Path);
        var diff = await ctx.Gateway.GetFileDiffAsync(new DiffRequest { Source = DiffSource.UnstagedChanges, Path = "f.txt" });

        Assert.Equal(2, diff.Hunks.Count);
        Assert.Contains("-line 2" + eol + "+changed 2" + eol, diff.RawPatch);
        if (eol == "\n")
            Assert.DoesNotContain("\r", diff.RawPatch);

        var patches = new PatchService(_host.Cli);
        var patch = patches.GeneratePatch(diff, PatchScope.SingleHunk, new HashSet<DiffLineRef> { new(0, 0) });
        await patches.ApplyPatchAsync(ctx, patch, PatchTarget.StagingArea, reverse: false);

        var staged = repo.Git("diff", "--cached", "--no-color", "--", "f.txt");
        Assert.Contains("+changed 2", staged);
        Assert.DoesNotContain("changed 18", staged);
    }
}
