namespace Historia.Tests;

/// <summary>
/// <see cref="GraphBackgroundWriter.EnsureCurrentAsync"/> against real repositories: every
/// fixture is built and every graph written by the git CLI, and the checks read what git left on
/// disk. A missing graph gets a full write, a stale or unusable one a <c>--split</c> refresh,
/// and one ref state is never acted on twice.
/// </summary>
[Trait("tier", "git")]
public sealed class CommitGraphMaintenanceTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly List<TestRepositoryBuilder> _repos = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _repos)
            r.Dispose();
        await _host.DisposeAsync();
    }

    private TestRepositoryBuilder NewRepo(int commits)
    {
        var repo = TestRepositoryBuilder.Create();
        _repos.Add(repo);
        for (var i = 0; i < commits; i++)
            repo.WriteFile("f.txt", $"{i}\n").Stage().Commit($"c{i}");
        return repo;
    }

    private (GraphBackgroundWriter Writer, InterceptingGitCliService Cli) NewWriter()
    {
        var cli = new InterceptingGitCliService(_host.Provider.GetRequiredService<IGitCliService>());
        return (new GraphBackgroundWriter(cli), cli);
    }

    private static List<GitCliRequest> Writes(InterceptingGitCliService cli) =>
        [.. cli.Requests.Where(r => r.Arguments.Count > 1 && r.Arguments[0] == "commit-graph" && r.Arguments[1] == "write")];

    private static string InfoDir(string repoPath) => Path.Combine(repoPath, ".git", "objects", "info");

    private static bool GraphContains(string repoPath, string sha) =>
        new CommitGraphIndex(Path.Combine(repoPath, ".git", "objects")).Lookup([GitObjectId.Parse(sha)]) is { } l
        && l.Contains(GitObjectId.Parse(sha));

    [Fact]
    public async Task A_small_repository_without_a_graph_gets_one()
    {
        var repo = NewRepo(3);
        var (writer, cli) = NewWriter();
        await using var ctx = _host.Open(repo.Path);

        await writer.EnsureCurrentAsync(ctx);

        var write = Assert.Single(Writes(cli));
        Assert.DoesNotContain("--split", write.Arguments);
        Assert.True(File.Exists(Path.Combine(InfoDir(repo.Path), "commit-graph")));
        Assert.True(GraphContains(repo.Path, repo.HeadSha));
    }

    [Fact]
    public async Task A_current_graph_runs_no_write()
    {
        var repo = NewRepo(3);
        repo.Git("tag", "v1", "HEAD~1");
        repo.Git("commit-graph", "write", "--reachable");
        var (writer, cli) = NewWriter();
        await using var ctx = _host.Open(repo.Path);

        await writer.EnsureCurrentAsync(ctx);

        Assert.Empty(Writes(cli));
    }

    [Fact]
    public async Task A_new_commit_after_the_write_is_added_with_a_split_refresh()
    {
        var repo = NewRepo(5);
        var (writer, cli) = NewWriter();
        await using var ctx = _host.Open(repo.Path);
        await writer.EnsureCurrentAsync(ctx);
        Assert.Single(Writes(cli));

        var tip = repo.WriteFile("f.txt", "new\n").Stage().Commit("new");
        Assert.False(GraphContains(repo.Path, tip));
        await writer.EnsureCurrentAsync(ctx);

        var writes = Writes(cli);
        Assert.Equal(2, writes.Count);
        Assert.Contains("--split", writes[1].Arguments);
        Assert.True(File.Exists(Path.Combine(InfoDir(repo.Path), "commit-graphs", "commit-graph-chain")));
        Assert.True(GraphContains(repo.Path, tip));
    }

    [Fact]
    public async Task A_stale_graph_git_wrote_itself_is_refreshed_with_split()
    {
        var repo = NewRepo(5);
        repo.Git("commit-graph", "write", "--reachable");
        repo.Git("checkout", "-q", "-b", "side");
        var tip = repo.WriteFile("g.txt", "side\n").Stage().Commit("side");
        var (writer, cli) = NewWriter();
        await using var ctx = _host.Open(repo.Path);

        await writer.EnsureCurrentAsync(ctx);

        var write = Assert.Single(Writes(cli));
        Assert.Contains("--split", write.Arguments);
        Assert.True(GraphContains(repo.Path, tip));
    }

    [Fact]
    public async Task A_generation_1_graph_is_refreshed_once_per_ref_state()
    {
        var repo = NewRepo(5);
        repo.Git("-c", "commitGraph.generationVersion=1", "commit-graph", "write", "--reachable");
        var (writer, cli) = NewWriter();
        await using var ctx = _host.Open(repo.Path);
        Assert.Null(ctx.CommitGraph.Lookup([GitObjectId.Parse(repo.HeadSha)]));

        await writer.EnsureCurrentAsync(ctx);
        await writer.EnsureCurrentAsync(ctx);
        await writer.EnsureCurrentAsync(ctx);

        var write = Assert.Single(Writes(cli));
        Assert.Contains("--split", write.Arguments);
    }

    [Fact]
    public async Task An_empty_repository_is_attempted_at_most_once_per_ref_state()
    {
        var repo = NewRepo(0);
        var (writer, cli) = NewWriter();
        await using var ctx = _host.Open(repo.Path);

        await writer.EnsureCurrentAsync(ctx);
        await writer.EnsureCurrentAsync(ctx);

        Assert.True(Writes(cli).Count <= 1);
        Assert.False(GraphBackgroundWriter.HasCommitGraph(ctx.GitDirectory)); // git writes no file
    }

    [Fact]
    public async Task Two_checks_back_to_back_write_once()
    {
        var repo = NewRepo(3);
        var (writer, cli) = NewWriter();
        await using var ctx = _host.Open(repo.Path);

        await Task.WhenAll(writer.EnsureCurrentAsync(ctx), writer.EnsureCurrentAsync(ctx));

        Assert.Single(Writes(cli));
    }

    [Fact]
    public async Task A_linked_worktree_refreshes_the_shared_graph_and_never_rewrites_it()
    {
        var repo = NewRepo(5);
        repo.Git("commit-graph", "write", "--reachable");
        var linked = Path.Combine(TestScratch.NewDirectory("cg-linked-"), "wt");
        repo.Git("worktree", "add", "-q", "-b", "side", linked);
        File.WriteAllText(Path.Combine(linked, "w.txt"), "wt\n");
        repo.Git("-C", linked, "add", "w.txt");
        repo.Git("-C", linked, "commit", "-q", "-m", "in worktree", "--no-gpg-sign");
        var tip = repo.Git("-C", linked, "rev-parse", "HEAD").Trim();
        var (writer, cli) = NewWriter();
        await using var ctx = _host.Open(linked);

        Assert.True(GraphBackgroundWriter.HasCommitGraph(ctx.GitDirectory));
        await writer.EnsureCurrentAsync(ctx);

        var write = Assert.Single(Writes(cli));
        Assert.Contains("--split", write.Arguments);
        Assert.True(GraphContains(repo.Path, tip));
        Assert.False(Directory.Exists(Path.Combine(ctx.GitDirectory, "objects")));
    }

    [Fact]
    public async Task A_partial_clone_write_never_fetches_from_the_promisor_remote()
    {
        using var source = TestRepositoryBuilder.Create();
        source.WriteFile("gone.txt", "only in history\n").Stage().Commit("add");
        var blob = source.Git("rev-parse", "HEAD:gone.txt").Trim();
        source.Git("rm", "-q", "gone.txt");
        source.Commit("remove");
        source.Git("config", "uploadpack.allowFilter", "true");

        var clone = TestScratch.NewDirectory();
        try
        {
            source.Git("clone", "-q", "--no-checkout", "--filter=blob:none", "--no-local",
                new Uri(source.Path).AbsoluteUri, clone);
            var noLazyFetch = new Dictionary<string, string> { ["GIT_NO_LAZY_FETCH"] = "1" };
            bool LocallyPresent()
            {
                try { source.Git(noLazyFetch, "-C", clone, "cat-file", "-e", blob); return true; }
                catch (InvalidOperationException) { return false; }
            }
            Assert.False(LocallyPresent());
            // Written as a file: update-ref would fetch the object itself to check it exists.
            File.WriteAllText(Path.Combine(clone, ".git", "refs", "tags", "promised"), blob + "\n");

            var (writer, cli) = NewWriter();
            await using (var ctx = _host.Open(clone))
                await writer.EnsureCurrentAsync(ctx);

            Assert.Single(Writes(cli));
            Assert.True(GraphBackgroundWriter.HasCommitGraph(Path.Combine(clone, ".git")));
            Assert.False(LocallyPresent(), "the commit-graph write must not fetch from the promisor remote");
        }
        finally
        {
            foreach (var f in new DirectoryInfo(clone).EnumerateFiles("*", SearchOption.AllDirectories))
                f.Attributes &= ~FileAttributes.ReadOnly;
            try { Directory.Delete(clone, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task A_failing_write_is_marked_once_and_not_retried()
    {
        var repo = NewRepo(3);
        var (writer, cli) = NewWriter();
        cli.Intercept = r => r.Arguments.Count > 0 && r.Arguments[0] == "commit-graph" ? InterceptingGitCliService.Failure() : null;
        await using var ctx = _host.Open(repo.Path);

        using var dir = new StateDir();
        var session = AppLog.StartSessionForTesting(dir.Path);
        try
        {
            await writer.EnsureCurrentAsync(ctx);
            await writer.EnsureCurrentAsync(ctx);
        }
        finally
        {
            session.Dispose();
        }

        Assert.Single(Writes(cli));
        var lines = File.ReadAllLines(session.SessionPath);
        Assert.Single(lines, l => l.Contains("Commit-graph write failed", StringComparison.Ordinal) && l.Contains("exit 128", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_clone_write_records_its_ref_state()
    {
        var repo = NewRepo(3);
        var cli = new RecordingGitCliService();
        var writer = new GraphBackgroundWriter(cli);
        await using var ctx = _host.Open(repo.Path);

        await writer.WriteForCloneAsync(ctx, CancellationToken.None);
        // The recording CLI writes nothing, so only the recorded ref state stops a second write.
        await writer.EnsureCurrentAsync(ctx);

        Assert.Single(cli.Requests);
        Assert.Equal("1", cli.Requests[0].ExtraEnvironment?["GIT_NO_LAZY_FETCH"]);
    }
}
