namespace Historia.Tests;

/// <summary>
/// <see cref="CommitGraphIndex"/> reads git's commit-graph files directly. Every fixture's graph
/// is written by the git CLI, and every corrected commit date is checked against the test's own
/// computation from <c>git rev-list</c>: cd = max(ct, max cd(parents) + 1), the definition in
/// git's <c>compute_generation_from_max</c>. Anything the reader can't fully trust — no graph, a
/// generation-1 layer, a damaged file — must come back as no graph at all, never a guess.
/// </summary>
[Trait("tier", "git")]
public sealed class CommitGraphIndexTests(ITestOutputHelper output) : IDisposable
{
    private readonly List<TestRepositoryBuilder> _repos = [];

    public void Dispose()
    {
        foreach (var r in _repos)
            r.Dispose();
    }

    private TestRepositoryBuilder NewRepo(string objectFormat = "sha1")
    {
        var repo = TestRepositoryBuilder.Create();
        _repos.Add(repo);
        if (objectFormat != "sha1")
        {
            var gitDir = Path.Combine(repo.Path, ".git");
            foreach (var f in Directory.EnumerateFiles(gitDir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(gitDir, recursive: true);
            repo.Git("init", "-b", "main", "--object-format=" + objectFormat);
        }
        return repo;
    }

    /// <summary>One commit of a fast-import fixture: its branch, committer time (seconds since the
    /// epoch) and parents, each the index of an earlier commit in the list.</summary>
    internal readonly record struct FixtureCommit(string Branch, long When, params int[] Parents);

    /// <summary>Writes <paramref name="commits"/> with one <c>git fast-import</c> stream and
    /// returns their ids in list order. A commit with no parents starts a fresh line of history
    /// on its branch.</summary>
    internal static string[] Import(TestRepositoryBuilder repo, IReadOnlyList<FixtureCommit> commits)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < commits.Count; i++)
        {
            var c = commits[i];
            var reference = "refs/heads/" + c.Branch;
            if (c.Parents.Length == 0)
                sb.Append("reset ").Append(reference).Append('\n');
            sb.Append("commit ").Append(reference).Append('\n');
            sb.Append("mark :").Append(i + 1).Append('\n');
            sb.Append($"author T <t@example.com> {c.When} +0000\n");
            sb.Append($"committer T <t@example.com> {c.When} +0000\n");
            var msg = $"c{i}\n";
            sb.Append("data ").Append(msg.Length).Append('\n').Append(msg);
            if (c.Parents.Length > 0)
                sb.Append("from :").Append(c.Parents[0] + 1).Append('\n');
            foreach (var p in c.Parents.Skip(1))
                sb.Append("merge :").Append(p + 1).Append('\n');
            var content = $"{i}\n";
            sb.Append("M 100644 inline f.txt\n");
            sb.Append("data ").Append(content.Length).Append('\n').Append(content).Append('\n');
        }
        repo.FastImport(sb.ToString());

        var ids = new string[commits.Count];
        var lines = repo.Git("rev-list", "--all", "--reverse", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < lines.Length; i += 2)
        {
            var sha = lines[i]["commit ".Length..];
            var index = int.Parse(lines[i + 1].AsSpan(1), System.Globalization.CultureInfo.InvariantCulture);
            ids[index] = sha;
        }
        return ids;
    }

    /// <summary>Every commit's corrected commit date, computed from <c>git rev-list</c>.</summary>
    internal static Dictionary<string, long> ExpectedCorrectedDates(TestRepositoryBuilder repo)
    {
        var parents = new Dictionary<string, string[]>();
        var times = new Dictionary<string, long>();
        var lines = repo.Git("rev-list", "--all", "--parents", "--format=%ct").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < lines.Length; i += 2)
        {
            var ids = lines[i]["commit ".Length..].Split(' ');
            parents[ids[0]] = ids[1..];
            times[ids[0]] = long.Parse(lines[i + 1], System.Globalization.CultureInfo.InvariantCulture);
        }

        var cd = new Dictionary<string, long>();
        long Compute(string id)
        {
            if (cd.TryGetValue(id, out var v))
                return v;
            var maxParent = 0L;
            foreach (var p in parents[id])
                maxParent = Math.Max(maxParent, Compute(p));
            return cd[id] = Math.Max(times[id], maxParent + 1);
        }
        foreach (var id in parents.Keys)
            Compute(id);
        return cd;
    }

    private static string ObjectsDir(TestRepositoryBuilder repo) => Path.Combine(repo.Path, ".git", "objects");

    private static GitObjectId[] Ids(IEnumerable<string> shas) => [.. shas.Select(GitObjectId.Parse)];

    private const long Day = 86_400;
    private const long Base = 1_700_000_000;

    /// <summary>Branches, merges, and clock skew (a commit dated before its parent, which only
    /// the corrected date can order).</summary>
    private static readonly FixtureCommit[] Dag =
    [
        new("main", Base),                       // 0
        new("main", Base + Day, 0),              // 1
        new("side", Base + 2 * Day, 0),          // 2
        new("main", Base + 3 * Day, 1, 2),       // 3 merge
        new("skew", Base - 30 * Day, 3),         // 4 dated a month before its parent
        new("skew", Base - 29 * Day, 4),         // 5
        new("main", Base + 4 * Day, 3, 5),       // 6 merge of the skewed line
        new("other", Base + 10 * Day),           // 7 a second root
    ];

    private void AssertDatesMatch(TestRepositoryBuilder repo, int expectedCount)
    {
        var expected = ExpectedCorrectedDates(repo);
        Assert.Equal(expectedCount, expected.Count);
        var lookup = new CommitGraphIndex(ObjectsDir(repo)).Lookup(Ids(expected.Keys));

        Assert.NotNull(lookup);
        Assert.Equal(expected.Count, lookup.Count);
        foreach (var (sha, cd) in expected)
        {
            var id = GitObjectId.Parse(sha);
            Assert.True(lookup.Contains(id), sha);
            Assert.Equal(cd, lookup.CorrectedDate(id));
        }
    }

    [Theory]
    [InlineData("sha1")]
    [InlineData("sha256")]
    public void Single_file_gives_every_commit_its_corrected_date(string objectFormat)
    {
        var repo = NewRepo(objectFormat);
        Import(repo, Dag);
        repo.Git("commit-graph", "write", "--reachable");
        Assert.True(File.Exists(Path.Combine(ObjectsDir(repo), "info", "commit-graph")));

        AssertDatesMatch(repo, Dag.Length);
    }

    [Fact]
    public void Clock_skew_is_ordered_by_the_corrected_date()
    {
        var repo = NewRepo();
        var ids = Import(repo, Dag);
        repo.Git("commit-graph", "write", "--reachable");

        var lookup = new CommitGraphIndex(ObjectsDir(repo)).Lookup(Ids(ids))!;
        var parent = lookup.CorrectedDate(GitObjectId.Parse(ids[3]))!.Value;
        var skewed = lookup.CorrectedDate(GitObjectId.Parse(ids[4]))!.Value;
        Assert.Equal(parent + 1, skewed);
    }

    [Fact]
    public void Split_chain_of_three_layers_answers_from_every_layer()
    {
        var repo = NewRepo();
        Import(repo, Dag[..3]);
        repo.Git("commit-graph", "write", "--reachable", "--split=no-merge");
        repo.Git("commit", "--allow-empty", "-m", "layer 2", "--no-gpg-sign");
        repo.Git("commit-graph", "write", "--reachable", "--split=no-merge");
        repo.Git("commit", "--allow-empty", "-m", "layer 3", "--no-gpg-sign");
        repo.Git("commit-graph", "write", "--reachable", "--split=no-merge");

        var chain = Path.Combine(ObjectsDir(repo), "info", "commit-graphs", "commit-graph-chain");
        Assert.Equal(3, File.ReadAllLines(chain).Length);
        Assert.False(File.Exists(Path.Combine(ObjectsDir(repo), "info", "commit-graph")));

        AssertDatesMatch(repo, 5);
    }

    [Fact]
    public void Offsets_past_31_bits_come_from_the_overflow_chunk()
    {
        var repo = NewRepo();
        var y2100 = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var y1971 = new DateTimeOffset(1971, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var ids = Import(repo, [new("main", y2100), new("main", y1971, 0)]);
        repo.Git("commit-graph", "write", "--reachable");

        var lookup = new CommitGraphIndex(ObjectsDir(repo)).Lookup(Ids(ids))!;
        Assert.Equal(y2100, lookup.CorrectedDate(GitObjectId.Parse(ids[0])));
        Assert.Equal(y2100 + 1, lookup.CorrectedDate(GitObjectId.Parse(ids[1])));
        Assert.True(y2100 + 1 - y1971 > int.MaxValue, "the child's offset must overflow 31 bits");
        AssertDatesMatch(repo, 2);
    }

    [Fact]
    public void Generation_one_graph_is_no_graph()
    {
        var repo = NewRepo();
        var ids = Import(repo, Dag);
        repo.Git("-c", "commitGraph.generationVersion=1", "commit-graph", "write", "--reachable");

        Assert.Null(new CommitGraphIndex(ObjectsDir(repo)).Lookup(Ids(ids)));
    }

    [Fact]
    public void Chain_with_one_generation_one_layer_is_no_graph()
    {
        var repo = NewRepo();
        var ids = Import(repo, Dag[..3]);
        repo.Git("-c", "commitGraph.generationVersion=1", "commit-graph", "write", "--reachable", "--split=no-merge");
        repo.Git("commit", "--allow-empty", "-m", "layer 2", "--no-gpg-sign");
        repo.Git("commit-graph", "write", "--reachable", "--split=no-merge");

        Assert.Null(new CommitGraphIndex(ObjectsDir(repo)).Lookup(Ids(ids)));
    }

    [Fact]
    public void No_graph_is_no_graph()
    {
        var repo = NewRepo();
        var ids = Import(repo, Dag);

        Assert.Null(new CommitGraphIndex(ObjectsDir(repo)).Lookup(Ids(ids)));
    }

    [Fact]
    public void Truncated_file_is_no_graph()
    {
        var repo = NewRepo();
        var ids = Import(repo, Dag);
        repo.Git("commit-graph", "write", "--reachable");
        var file = Path.Combine(ObjectsDir(repo), "info", "commit-graph");
        File.SetAttributes(file, FileAttributes.Normal);
        var bytes = File.ReadAllBytes(file);
        File.WriteAllBytes(file, bytes[..(bytes.Length / 2)]);

        Assert.Null(new CommitGraphIndex(ObjectsDir(repo)).Lookup(Ids(ids)));
    }

    [Fact]
    public void Corrupted_chunk_table_is_no_graph()
    {
        var repo = NewRepo();
        var ids = Import(repo, Dag);
        repo.Git("commit-graph", "write", "--reachable");
        var file = Path.Combine(ObjectsDir(repo), "info", "commit-graph");
        File.SetAttributes(file, FileAttributes.Normal);
        var bytes = File.ReadAllBytes(file);
        // The first chunk's offset (bytes 12..20) pointed past the end of the file.
        WriteInt64BigEndian(bytes.AsSpan(12), bytes.Length + 1000L);
        File.WriteAllBytes(file, bytes);

        Assert.Null(new CommitGraphIndex(ObjectsDir(repo)).Lookup(Ids(ids)));
    }

    private static void WriteInt64BigEndian(Span<byte> span, long value)
    {
        for (var i = 7; i >= 0; i--)
        {
            span[i] = (byte)value;
            value >>= 8;
        }
    }

    [Fact]
    public void Ids_outside_the_graph_are_not_in_it()
    {
        var repo = NewRepo();
        var ids = Import(repo, Dag);
        repo.Git("commit-graph", "write", "--reachable");
        repo.Git("commit", "--allow-empty", "-m", "after the graph was written", "--no-gpg-sign");
        var later = repo.Git("rev-parse", "HEAD").Trim();
        var blob = repo.Git("rev-parse", ids[0] + ":f.txt").Trim();

        var lookup = new CommitGraphIndex(ObjectsDir(repo)).Lookup(Ids([ids[0], later, blob]))!;

        Assert.True(lookup.Contains(GitObjectId.Parse(ids[0])));
        Assert.False(lookup.Contains(GitObjectId.Parse(later)));
        Assert.Null(lookup.CorrectedDate(GitObjectId.Parse(later)));
        Assert.False(lookup.Contains(GitObjectId.Parse(blob)));
        Assert.Null(lookup.CorrectedDate(GitObjectId.Parse(blob)));
        Assert.Equal(1, lookup.Count);
    }

    [Fact]
    public void Ids_of_the_wrong_hash_length_are_no_graph()
    {
        var repo = NewRepo("sha256");
        Import(repo, Dag);
        repo.Git("commit-graph", "write", "--reachable");

        Assert.Null(new CommitGraphIndex(ObjectsDir(repo)).Lookup([GitObjectId.Parse(new string('a', 40))]));
    }

    [Fact]
    public void No_file_stays_open_after_a_lookup()
    {
        var repo = NewRepo();
        var ids = Import(repo, Dag[..3]);
        repo.Git("commit-graph", "write", "--reachable", "--split=no-merge");
        repo.Git("commit", "--allow-empty", "-m", "layer 2", "--no-gpg-sign");
        repo.Git("commit-graph", "write", "--reachable", "--split=no-merge");

        Assert.NotNull(new CommitGraphIndex(ObjectsDir(repo)).Lookup(Ids(ids)));

        // An exclusive open fails while any other handle is open, even a shared one.
        var dir = Path.Combine(ObjectsDir(repo), "info", "commit-graphs");
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            File.SetAttributes(file, FileAttributes.Normal);
            using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Delete(file);
        }
        repo.Git("commit-graph", "write", "--reachable", "--split=no-merge");
        Assert.NotNull(new CommitGraphIndex(ObjectsDir(repo)).Lookup(Ids(ids)));
    }

    [Fact]
    public void Single_file_can_be_replaced_after_a_lookup()
    {
        var repo = NewRepo();
        var ids = Import(repo, Dag);
        repo.Git("commit-graph", "write", "--reachable");

        Assert.NotNull(new CommitGraphIndex(ObjectsDir(repo)).Lookup(Ids(ids)));

        var file = Path.Combine(ObjectsDir(repo), "info", "commit-graph");
        File.SetAttributes(file, FileAttributes.Normal);
        using (File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        repo.Git("commit", "--allow-empty", "-m", "more", "--no-gpg-sign");
        repo.Git("commit-graph", "write", "--reachable");
        Assert.NotNull(new CommitGraphIndex(ObjectsDir(repo)).Lookup(Ids(ids)));
    }

    [Fact]
    public void Empty_id_set_on_a_valid_graph_finds_nothing()
    {
        var repo = NewRepo();
        Import(repo, Dag);
        repo.Git("commit-graph", "write", "--reachable");

        var lookup = new CommitGraphIndex(ObjectsDir(repo)).Lookup([]);
        Assert.NotNull(lookup);
        Assert.Equal(0, lookup.Count);
    }

    [Fact]
    public void Reader_never_memory_maps()
    {
        var source = Path.Combine(SolutionRoot(), "Historia.Core", "Services", "Git", "CommitGraph", "CommitGraphIndex.cs");
        Assert.DoesNotContain("MemoryMappedFile", File.ReadAllText(source), StringComparison.Ordinal);
    }

    private static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "constitution.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("solution root not found");
    }

    /// <summary>Looks up every ref's object in each <c>HISTORIA_PERF_REPOS</c> repository (warm,
    /// best of 3). Read-only.</summary>
    [ColdLoadPerfFact]
    public void Looks_up_every_ref_of_a_real_repository_quickly()
    {
        var repos = (Environment.GetEnvironmentVariable("HISTORIA_PERF_REPOS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists);
        foreach (var path in repos)
        {
            var git = new TestRepositoryBuilderView(path);
            var ids = git.Run("for-each-ref", "--format=%(objectname)")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries).Distinct().Select(GitObjectId.Parse).ToArray();
            var gitDir = git.Run("rev-parse", "--git-common-dir").Trim();
            var objects = Path.Combine(Path.GetFullPath(gitDir, path), "objects");

            var best = double.MaxValue;
            CommitGraphLookup? lookup = null;
            for (var round = 0; round < 3; round++)
            {
                var sw = Stopwatch.StartNew();
                lookup = new CommitGraphIndex(objects).Lookup(ids);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            output.WriteLine($"{path}: {ids.Length} ids, {lookup?.Count ?? -1} in graph, best {best:F0} ms");
        }
    }

    /// <summary>Runs git read-only in an existing repository.</summary>
    private sealed class TestRepositoryBuilderView(string path)
    {
        public string Run(params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = path, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--no-optional-locks");
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output;
        }
    }
}
