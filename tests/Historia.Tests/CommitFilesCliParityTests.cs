namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class CommitFilesCliParityTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    private static IReadOnlyList<FileStatusEntry> ProjectCommitFiles(Repository repo, GitObjectId id, int parentIndex = 1)
    {
        var commit = repo.Lookup<Commit>(id.ToLibGit2())
            ?? throw new HistoriaGitException(GitErrorKind.RefNotFound, $"Commit {id} not found.");
        var parent = commit.Parents.ElementAtOrDefault(Math.Max(0, parentIndex - 1));

        using var changes = repo.Diff.Compare<TreeChanges>(parent?.Tree, commit.Tree);
        var result = new List<FileStatusEntry>();
        foreach (var change in changes)
        {
            result.Add(new FileStatusEntry
            {
                Path = change.Path,
                Kind = change.Status switch
                {
                    ChangeKind.Added => FileChangeKind.Added,
                    ChangeKind.Deleted => FileChangeKind.Deleted,
                    ChangeKind.Renamed => FileChangeKind.Renamed,
                    ChangeKind.Copied => FileChangeKind.Copied,
                    ChangeKind.TypeChanged => FileChangeKind.TypeChanged,
                    _ => FileChangeKind.Modified,
                },
                OldPath = change.OldPath == change.Path ? null : change.OldPath,
                IsBinary = false,
                IsSubmodule = false,
            });
        }

        return result;
    }

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
        var libExpected = ProjectCommitFiles(git, rootId, parentIndex: 1);
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
        var libExpected = ProjectCommitFiles(git, commitId, parentIndex: 1);
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
        var libParent1 = ProjectCommitFiles(git, mergeId, parentIndex: 1);
        var libParent2 = ProjectCommitFiles(git, mergeId, parentIndex: 2);

        var cliParent0 = await ctx.Gateway.GetCommitFilesAsync(mergeId, parentIndex: 0);
        var cliParent1 = await ctx.Gateway.GetCommitFilesAsync(mergeId, parentIndex: 1);
        var cliParent2 = await ctx.Gateway.GetCommitFilesAsync(mergeId, parentIndex: 2);
        var cliParent3 = await ctx.Gateway.GetCommitFilesAsync(mergeId, parentIndex: 3);

        // parentIndex 0 and 1 both resolve to the first parent
        Assert.Equal(cliParent1.Count, cliParent0.Count);
        for (var i = 0; i < cliParent1.Count; i++)
        {
            Assert.Equal(cliParent1[i].Path, cliParent0[i].Path);
            Assert.Equal(cliParent1[i].Kind, cliParent0[i].Kind);
        }

        // Parity with libgit2 for parent 1 (merges feature.txt)
        Assert.Contains(cliParent1, f => f.Path == "feature.txt" && f.Kind == FileChangeKind.Added);
        Assert.DoesNotContain(cliParent1, f => f.Path == "main.txt");
        Assert.Equal(libParent1.Count, cliParent1.Count);

        // Parity with libgit2 for parent 2 (merges main.txt)
        Assert.Contains(cliParent2, f => f.Path == "main.txt" && f.Kind == FileChangeKind.Added);
        Assert.DoesNotContain(cliParent2, f => f.Path == "feature.txt");
        Assert.Equal(libParent2.Count, cliParent2.Count);

        // parentIndex 3 is past the 2-parent merge count: diffs against empty tree like libgit2 null parent
        var commit = git.Lookup<Commit>(mergeId.ToLibGit2());
        using var changesNull = git.Diff.Compare<TreeChanges>(null, commit.Tree);
        Assert.Equal(changesNull.Count(), cliParent3.Count);
    }

    [Fact]
    public async Task Merge_with_ours_strategy_has_empty_diff_against_parent1()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("commit-files-merge-ours", b =>
        {
            b.WriteFile("base.txt", "base\n").Stage().Commit("base");
            b.Checkout("feature", create: true).WriteFile("feature.txt", "feat\n").Stage().Commit("on feature");
            b.Checkout("main");
            b.Git("merge", "-s", "ours", "-m", "merge ours", "feature");
        });

        await using var ctx = _host.Open(repo.Path);
        var mergeId = new GitObjectId(repo.HeadSha);

        using var git = new Repository(repo.Path);
        var commit = git.Lookup<Commit>(mergeId.ToLibGit2());

        // Parent 1 diff is empty because -s ours keeps main's tree exactly.
        var cliParent1 = await ctx.Gateway.GetCommitFilesAsync(mergeId, parentIndex: 1);
        using var changes1 = git.Diff.Compare<TreeChanges>(commit.Parents.ElementAt(0).Tree, commit.Tree);
        Assert.Empty(cliParent1);
        Assert.Equal(changes1.Count(), cliParent1.Count);

        // Parent 2 diff is non-empty.
        var cliParent2 = await ctx.Gateway.GetCommitFilesAsync(mergeId, parentIndex: 2);
        using var changes2 = git.Diff.Compare<TreeChanges>(commit.Parents.ElementAt(1).Tree, commit.Tree);
        Assert.NotEmpty(cliParent2);
        Assert.Equal(changes2.Count(), cliParent2.Count);
    }

    [Fact]
    public async Task Out_of_range_parent_index_diffs_against_empty_tree_matching_libgit2()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("commit-files-out-of-range", b =>
        {
            b.WriteFile("a.txt", "content\n").Stage().Commit("root");
        });

        await using var ctx = _host.Open(repo.Path);
        var commitId = new GitObjectId(repo.HeadSha);

        using var git = new Repository(repo.Path);
        var commit = git.Lookup<Commit>(commitId.ToLibGit2());
        using var changes = git.Diff.Compare<TreeChanges>(null, commit.Tree);

        // parentIndex: 5 on a 0-parent root commit diffs against empty tree
        var cliActual = await ctx.Gateway.GetCommitFilesAsync(commitId, parentIndex: 5);
        Assert.Equal(changes.Count(), cliActual.Count);
        Assert.Single(cliActual);
        Assert.Equal("a.txt", cliActual[0].Path);
        Assert.Equal(FileChangeKind.Added, cliActual[0].Kind);
    }

    [Fact]
    public async Task Normal_commit_spawns_single_diff_tree_process()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("commit-files-spawns", b =>
        {
            b.WriteFile("a.txt", "line1\n");
            b.Stage().Commit("c1");
            b.WriteFile("a.txt", "line2\n");
            b.Stage().Commit("c2");
        });

        var realCli = _host.Provider.GetRequiredService<IGitCliService>();
        var recordingCli = new DelegatingRecordingCli(realCli);

        var sc = new ServiceCollection();
        sc.AddSingleton<ISettingsService>(_host.Settings);
        sc.AddSingleton<IRepoLayoutService, RepoLayoutService>();
        sc.AddSingleton<IGitExecutableLocator>(_host.Locator);
        sc.AddSingleton<IGitCliService>(recordingCli);
        sc.AddSingleton<ICredentialVaultService, FakeVault>();
        await using var provider = sc.BuildServiceProvider();

        await using var ctx = RepositoryContext.Open(repo.Path, provider);
        await ctx.Watcher.DisposeAsync();
        var commitId = new GitObjectId(repo.HeadSha);
        var runsBefore = recordingCli.RunCount;

        var files = await ctx.Gateway.GetCommitFilesAsync(commitId, parentIndex: 1);
        Assert.Single(files);
        Assert.Equal(1, recordingCli.RunCount - runsBefore);
        Assert.Contains(recordingCli.Requests.Last().Arguments, a => a == "diff-tree");
    }

    [Fact]
    public async Task Failure_with_an_existing_parent_throws_instead_of_diffing_the_empty_tree()
    {
        // An empty-tree diff succeeds wherever the real diff failed for another reason (e.g. a
        // partial clone that can't fetch blobs), reporting every file as added.
        using var repo = TestRepositoryBuilder.FromTemplate("commit-files-real-failure", b =>
        {
            b.WriteFile("a.txt", "1\n").Stage().Commit("c1");
            b.WriteFile("b.txt", "2\n").Stage().Commit("c2");
        });

        var cli = new InterceptingGitCliService(_host.Provider.GetRequiredService<IGitCliService>())
        {
            Intercept = r => r.Arguments[0] == "diff-tree" ? InterceptingGitCliService.Failure() : null,
        };
        await using var ctx = _host.Open(repo.Path, cli);

        await Assert.ThrowsAsync<HistoriaGitException>(() =>
            ctx.Gateway.GetCommitFilesAsync(new GitObjectId(repo.HeadSha), parentIndex: 1));

        Assert.Single(cli.Requests, r => r.Arguments[0] == "diff-tree");
    }

    private sealed class DelegatingRecordingCli(IGitCliService inner) : IGitCliService
    {
        public int RunCount { get; private set; }
        public List<GitCliRequest> Requests { get; } = [];
        public bool IsGitAvailable => inner.IsGitAvailable;

        public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
        {
            RunCount++;
            Requests.Add(request);
            return inner.RunAsync(request, ct);
        }

        public Task<GitCliResult> RunAsync(GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
        {
            RunCount++;
            Requests.Add(request);
            return inner.RunAsync(request, progress, onStdoutLine, ct);
        }
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
        var libExpected = ProjectCommitFiles(git, commitId, parentIndex: 1);
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
