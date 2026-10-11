namespace Historia.Tests;

/// <summary>
/// The fast ref scan types refs from <c>packed-refs</c> peel lines and the commit-graph instead
/// of reading every ref's object. Its entries must equal the typed scan's field for field — the
/// walk key, the decorations and the tag list all read them — on every ref shape, and it must
/// stand aside for the typed scan wherever the files can't answer.
/// </summary>
[Trait("tier", "git")]
public sealed class RefSnapshotFastScanParityTests : IDisposable
{
    private readonly List<TestRepositoryBuilder> _repos = [];

    public void Dispose()
    {
        foreach (var r in _repos)
            r.Dispose();
    }

    /// <summary>Records every git invocation, and runs it.</summary>
    private sealed class RecordingCli(IGitCliService inner) : IGitCliService
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public bool IsGitAvailable => inner.IsGitAvailable;

        public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default) =>
            RunAsync(request, progress: null, onStdoutLine: null, ct);

        public Task<GitCliResult> RunAsync(GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
        {
            Calls.Enqueue(string.Join(' ', request.Arguments.Take(2)));
            return inner.RunAsync(request, progress, onStdoutLine, ct);
        }
    }

    private static readonly Lazy<IGitCliService> RealCli = new(() =>
    {
        var locator = new GitExecutableLocator();
        locator.ResolveAsync(null).GetAwaiter().GetResult();
        return new GitCliService(locator);
    });

    private static (RefSnapshotProvider Provider, RecordingCli Cli) Provider(TestRepositoryBuilder repo, bool withGraph = true)
    {
        var cli = new RecordingCli(RealCli.Value);
        var gitDir = Path.Combine(repo.Path, ".git");
        var graph = withGraph ? new CommitGraphIndex(Path.Combine(gitDir, "objects")) : null;
        return (new RefSnapshotProvider(cli, gitDir, repo.Path, CancellationToken.None, graph), cli);
    }

    private TestRepositoryBuilder NewRepo(params string[] initArgs)
    {
        var repo = TestRepositoryBuilder.Create();
        _repos.Add(repo);
        if (initArgs.Length > 0)
        {
            var gitDir = Path.Combine(repo.Path, ".git");
            foreach (var f in Directory.EnumerateFiles(gitDir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(gitDir, recursive: true);
            repo.Git(["init", "-b", "main", .. initArgs]);
            repo.Git("config", "user.name", "Test User");
            repo.Git("config", "user.email", "test@example.com");
        }
        return repo;
    }

    private TestRepositoryBuilder Shapes()
    {
        var repo = TestRepositoryBuilder.FromTemplate("fast-scan-shapes", BuildShapes);
        _repos.Add(repo);
        return repo;
    }

    /// <summary>Every shape at once: branches, a remote branch and its symbolic <c>origin/HEAD</c>,
    /// a configured upstream, lightweight and annotated tags, a tag of a tag, tags of a tree and of
    /// a blob, a branch set to a blob (packed by hand); a commit-graph written before the last commit (stale), then
    /// everything packed; then loose refs written over it, one shadowing a stale packed entry.</summary>
    internal static void BuildShapes(TestRepositoryBuilder b)
    {
        b.LinearHistory(6);
        b.Git("branch", "feature", "HEAD~2");
        b.Git("update-ref", "refs/remotes/origin/main", "HEAD~1");
        b.Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        b.Git("remote", "add", "origin", "../no-such-remote");
        b.Git("config", "branch.feature.remote", "origin");
        b.Git("config", "branch.feature.merge", "refs/heads/main");
        b.Git("tag", "light", "HEAD~3");
        b.Git("tag", "-a", "-m", "annotated\n\nbody line", "annotated", "HEAD~4");
        b.Git("-c", "advice.nestedTag=false", "tag", "-a", "-m", "tag of a tag", "nested", "annotated");
        b.Git("tag", "-a", "-m", "tag of a tree", "tree-tag", "HEAD^{tree}");
        var blob = b.Git("rev-parse", "HEAD:f.txt").Trim();
        b.Git("tag", "-a", "-m", "tag of a blob", "blob-tag", blob);
        b.Git("tag", "light-blob", blob);
        b.Git("commit-graph", "write", "--reachable");
        b.WriteFile("g.txt", "after the graph\n").Stage().Commit("after the graph");
        b.Git("tag", "-a", "-m", "tags a commit the graph doesn't have", "after-graph");
        b.Git("pack-refs", "--all");
        AddPackedBlobBranch(b, blob);
        b.Git("tag", "loose-light", "HEAD~5");
        b.Git("tag", "-a", "-m", "a loose annotated tag", "loose-annotated", "HEAD~5");
        b.Git("update-ref", "refs/heads/feature", "HEAD~1"); // loose, over a stale packed entry
    }

    /// <summary>Adds <c>refs/heads/blob-branch</c> to packed-refs by hand, in sort order: update-ref
    /// and pack-refs both refuse a non-commit on a branch.</summary>
    private static void AddPackedBlobBranch(TestRepositoryBuilder b, string blob)
    {
        var packed = Path.Combine(b.Path, ".git", "packed-refs");
        var lines = File.ReadAllLines(packed).ToList();
        var at = lines.FindIndex(l => l.EndsWith(" refs/heads/feature", StringComparison.Ordinal));
        lines.Insert(at, $"{blob} refs/heads/blob-branch");
        File.WriteAllText(packed, string.Join('\n', lines) + "\n");
    }

    private static async Task AssertParity(RefSnapshotProvider provider, string expectedKind = "fast")
    {
        var fast = await provider.GetAsync();
        Assert.Equal(expectedKind, provider.LastScanKindForTesting);
        var typed = await provider.ScanTypedForTestingAsync();
        Assert.NotEmpty(typed);
        Assert.Equal(typed, fast.Refs);
    }

    [Fact]
    public async Task Every_ref_shape_matches_the_typed_scan()
    {
        var repo = Shapes();
        var (provider, _) = Provider(repo);
        await AssertParity(provider);

        var snapshot = await provider.GetAsync();
        Assert.Equal("refs/remotes/origin/main", snapshot.Find("refs/remotes/origin/HEAD")!.SymrefTarget);
        Assert.Equal("origin/main", snapshot.Find("refs/heads/feature")!.Upstream);
        Assert.Equal("commit", snapshot.Find("refs/tags/nested")!.PeeledType);
        Assert.Equal("tree", snapshot.Find("refs/tags/tree-tag")!.PeeledType);
        Assert.Equal("blob", snapshot.Find("refs/tags/blob-tag")!.PeeledType);
        Assert.Equal("blob", snapshot.Find("refs/heads/blob-branch")!.ObjectType);
        Assert.StartsWith("annotated\n\nbody line", snapshot.Find("refs/tags/annotated")!.Message);
    }

    [Fact]
    public async Task Without_a_commit_graph_matches_the_typed_scan()
    {
        var repo = Shapes();
        File.Delete(Path.Combine(repo.Path, ".git", "objects", "info", "commit-graph"));
        await AssertParity(Provider(repo).Provider);
    }

    [Fact]
    public async Task Without_a_graph_reader_matches_the_typed_scan()
    {
        await AssertParity(Provider(Shapes(), withGraph: false).Provider);
    }

    [Fact]
    public async Task Refs_to_missing_objects_are_dropped_by_both()
    {
        const string missing = "1111111111111111111111111111111111111111";
        var repo = Shapes();
        var gitDir = Path.Combine(repo.Path, ".git");

        // A tag whose tagged commit goes missing after packing (its peel line stays), and a
        // tag of that tag.
        var orphan = repo.Git("commit-tree", "HEAD^{tree}", "-p", "HEAD", "-m", "orphan").Trim();
        repo.Git("tag", "-a", "lost-target", orphan, "-m", "tags a commit that goes missing");
        repo.Git("-c", "advice.nestedTag=false", "tag", "-a", "lost-inner", "lost-target", "-m", "tags the tag");
        repo.Git("pack-refs", "--all");
        var loose = Path.Combine(gitDir, "objects", orphan[..2], orphan[2..]);
        File.SetAttributes(loose, FileAttributes.Normal);
        File.Delete(loose);

        // A loose branch and a packed lightweight tag naming an object that doesn't exist.
        File.WriteAllText(Path.Combine(gitDir, "refs", "heads", "broken"), missing + "\n");
        var packed = Path.Combine(gitDir, "packed-refs");
        var lines = File.ReadAllLines(packed).ToList();
        lines[0] = lines[0].Replace(" sorted", "", StringComparison.Ordinal);
        lines.Add($"{missing} refs/tags/broken-tag");
        File.WriteAllText(packed, string.Join('\n', lines) + "\n");

        var (provider, _) = Provider(repo);
        await AssertParity(provider);
        var refs = (await provider.GetAsync()).Refs.Select(r => r.CanonicalName).ToList();
        Assert.DoesNotContain("refs/heads/broken", refs);
        Assert.DoesNotContain("refs/tags/broken-tag", refs);
        Assert.DoesNotContain("refs/tags/lost-target", refs);
        Assert.DoesNotContain("refs/tags/lost-inner", refs);
        Assert.Contains("refs/tags/annotated", refs);
    }

    [Fact]
    public async Task Sha256_matches_the_typed_scan()
    {
        var repo = NewRepo("--object-format=sha256");
        repo.WriteFile("a.txt", "a\n").Stage();
        repo.Git("commit", "-m", "first", "--no-gpg-sign");
        repo.WriteFile("a.txt", "b\n").Stage();
        repo.Git("commit", "-m", "second", "--no-gpg-sign");
        repo.Git("tag", "light", "HEAD~1");
        repo.Git("tag", "-a", "annotated", "-m", "annotated");
        repo.Git("commit-graph", "write", "--reachable");
        repo.Git("pack-refs", "--all");

        var (provider, _) = Provider(repo);
        await AssertParity(provider);
        Assert.Equal(64, (await provider.GetAsync()).Find("refs/tags/annotated")!.Peeled!.Value.ToString().Length);
    }

    [Fact]
    public async Task Packed_refs_without_the_fully_peeled_trait_get_the_typed_scan()
    {
        var repo = Shapes();
        var packed = Path.Combine(repo.Path, ".git", "packed-refs");
        var lines = File.ReadAllLines(packed);
        lines[0] = "# pack-refs with: peeled sorted ";
        File.WriteAllText(packed, string.Join('\n', lines) + "\n");

        var (provider, _) = Provider(repo);
        await provider.GetAsync();
        Assert.Equal("typed: no fully-peeled packed-refs", provider.LastScanKindForTesting);
    }

    [Fact]
    public async Task Reftable_gets_the_typed_scan()
    {
        var repo = NewRepo("--ref-format=reftable");
        repo.WriteFile("a.txt", "a\n").Stage();
        repo.Git("commit", "-m", "first", "--no-gpg-sign");
        repo.Git("tag", "-a", "v1", "-m", "v1");

        var (provider, _) = Provider(repo);
        var snapshot = await provider.GetAsync();
        Assert.Equal("typed: reftable", provider.LastScanKindForTesting);
        Assert.True(snapshot.Find("refs/tags/v1")!.IsAnnotatedTag);
    }

    [Fact]
    public async Task Packed_refs_this_reader_rejects_get_the_typed_scan()
    {
        var repo = Shapes();
        var packed = Path.Combine(repo.Path, ".git", "packed-refs");
        var lines = File.ReadAllLines(packed).ToList();
        // git accepts any whitespace after the id; the reader takes only git's own single space.
        var head = repo.Git("rev-parse", "HEAD").Trim();
        lines[0] = lines[0].Replace(" sorted", "", StringComparison.Ordinal);
        lines.Add($"{head}\trefs/heads/tabbed");
        File.WriteAllText(packed, string.Join('\n', lines) + "\n");

        var (provider, _) = Provider(repo);
        var snapshot = await provider.GetAsync();
        Assert.Equal("typed: no fully-peeled packed-refs", provider.LastScanKindForTesting);
        Assert.NotNull(snapshot.Find("refs/heads/tabbed"));
    }

    [Fact]
    public async Task All_packed_refs_in_the_graph_take_one_process()
    {
        var repo = NewRepo();
        repo.LinearHistory(5);
        repo.Git("branch", "side", "HEAD~2");
        repo.Git("tag", "v1", "HEAD~3");
        repo.Git("tag", "v2", "HEAD~1");
        repo.Git("commit-graph", "write", "--reachable");
        repo.Git("pack-refs", "--all");

        var (provider, cli) = Provider(repo);
        await provider.GetAsync();
        Assert.Equal("fast", provider.LastScanKindForTesting);
        Assert.Equal(["for-each-ref --format=" + ForEachRefParser.ListFormat], cli.Calls.ToArray());
        await AssertParity(provider);
    }

    [Fact]
    public async Task Annotated_tags_read_their_details_in_one_batch()
    {
        var repo = Shapes();
        var (provider, cli) = Provider(repo);
        await provider.GetAsync();

        var calls = cli.Calls.ToArray();
        Assert.Equal(1, calls.Count(c => c.StartsWith("for-each-ref", StringComparison.Ordinal)));
        Assert.Equal(1, calls.Count(c => c == "cat-file --batch"));
        Assert.True(calls.Count(c => c.StartsWith("cat-file --batch-check", StringComparison.Ordinal)) <= 2);
    }

    /// <summary>The fast scan on each <c>HISTORIA_PERF_REPOS</c> repository (warm, best of 3) and
    /// the processes it ran. Read-only.</summary>
    [ColdLoadPerfFact]
    public async Task Scans_a_real_repository_quickly()
    {
        var repos = (Environment.GetEnvironmentVariable("HISTORIA_PERF_REPOS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists);
        foreach (var path in repos)
        {
            var best = double.MaxValue;
            string[] calls = [];
            string? kind = null;
            var count = 0;
            RefSnapshotProvider? last = null;
            IReadOnlyList<RefEntry> fast = [];
            for (var round = 0; round < 3; round++)
            {
                var cli = new RecordingCli(RealCli.Value);
                var gitDir = Path.Combine(path, ".git");
                last = new RefSnapshotProvider(cli, gitDir, path, CancellationToken.None,
                    new CommitGraphIndex(Path.Combine(gitDir, "objects")));
                var sw = Stopwatch.StartNew();
                fast = (await last.GetAsync()).Refs;
                count = fast.Count;
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                calls = cli.Calls.ToArray();
                kind = last.LastScanKindForTesting;
            }
            Assert.Equal(await last!.ScanTypedForTestingAsync(), fast);
            _output?.WriteLine($"{path}: {count} refs, {kind}, best {best:F0} ms, processes: {string.Join(" | ", calls.Select(c => c.Split(' ')[0] + " " + c.Split(' ').ElementAtOrDefault(1)?.Split('=')[0]))}");
        }
    }

    private readonly ITestOutputHelper? _output;

    public RefSnapshotFastScanParityTests(ITestOutputHelper output) => _output = output;
}
