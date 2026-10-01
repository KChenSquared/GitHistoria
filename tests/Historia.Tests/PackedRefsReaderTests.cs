namespace Historia.Tests;

/// <summary>
/// <see cref="PackedRefsReader"/> reads git's <c>packed-refs</c> file directly. Every fixture is
/// packed by <c>git pack-refs --all</c>, and every entry and peel must equal what
/// <c>git show-ref -d</c> reports. A file git would reject reads as no file at all.
/// </summary>
[Trait("tier", "git")]
public sealed class PackedRefsReaderTests(ITestOutputHelper output) : IDisposable
{
    private readonly TestRepositoryBuilder _repo = TestRepositoryBuilder.FromTemplate("packed-refs-shapes", Build);

    public void Dispose() => _repo.Dispose();

    private string GitDir => Path.Combine(_repo.Path, ".git");
    private string PackedFile => Path.Combine(GitDir, "packed-refs");

    /// <summary>A branch, a lightweight tag, annotated tags of a commit, of a tag (a tag of a tag)
    /// and of a tree, then everything packed.</summary>
    private static void Build(TestRepositoryBuilder repo)
    {
        repo.WriteFile("a.txt", "a\n").Stage().Commit("first");
        repo.WriteFile("a.txt", "b\n").Stage().Commit("second");
        repo.Git("branch", "side", "HEAD~1");
        repo.Git("tag", "light");
        repo.Git("tag", "-a", "annotated", "-m", "an annotated tag");
        repo.Git("tag", "-a", "outer", "annotated", "-m", "a tag of a tag");
        repo.Git("tag", "-a", "tree-tag", "HEAD^{tree}", "-m", "a tag of a tree");
        repo.Git("pack-refs", "--all");
    }

    /// <summary><c>git show-ref -d</c>: each ref's id, and for each annotated tag its fully peeled
    /// id on a <c>^{}</c> line.</summary>
    private Dictionary<string, (string Target, string? Peeled)> ShowRef()
    {
        var map = new Dictionary<string, (string, string?)>(StringComparer.Ordinal);
        foreach (var line in _repo.Git("show-ref", "-d").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var (sha, name) = (line[..line.IndexOf(' ')], line[(line.IndexOf(' ') + 1)..]);
            if (name.EndsWith("^{}", StringComparison.Ordinal))
            {
                var bare = name[..^3];
                map[bare] = (map[bare].Item1, sha);
            }
            else
                map[name] = (sha, null);
        }
        return map;
    }

    [Fact]
    public void Every_entry_and_peel_matches_show_ref()
    {
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(GitDir, "refs"), "*", SearchOption.AllDirectories));
        var packed = PackedRefsReader.TryRead(GitDir);
        Assert.NotNull(packed);

        var expected = ShowRef();
        Assert.Equal(expected.Count, packed.Count);
        foreach (var (name, (target, peeled)) in expected)
        {
            Assert.True(packed.TryGet(name, out var t, out var p), name);
            Assert.Equal(target, t.ToString());
            Assert.Equal(peeled, p?.ToString());
        }
        Assert.Equal(
            expected.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (e.Key, e.Value.Target, e.Value.Peeled)),
            packed.Entries.OrderBy(e => e.Name, StringComparer.Ordinal).Select(e => (e.Name, e.Target.ToString(), e.Peeled?.ToString())));
    }

    [Fact]
    public void Tag_of_a_tag_is_peeled_all_the_way()
    {
        var packed = PackedRefsReader.TryRead(GitDir)!;
        var commit = _repo.Git("rev-parse", "HEAD").Trim();
        var tree = _repo.Git("rev-parse", "HEAD^{tree}").Trim();

        Assert.True(packed.TryGet("refs/tags/outer", out _, out var outer));
        Assert.Equal(commit, outer?.ToString());
        Assert.True(packed.TryGet("refs/tags/tree-tag", out _, out var treeTag));
        Assert.Equal(tree, treeTag?.ToString());
        Assert.True(packed.TryGet("refs/tags/light", out _, out var light));
        Assert.Null(light);
        Assert.True(packed.TryGet("refs/heads/side", out _, out var side));
        Assert.Null(side);
    }

    [Fact]
    public void Header_traits_are_read()
    {
        var packed = PackedRefsReader.TryRead(GitDir)!;
        Assert.True(packed.FullyPeeled);
        Assert.True(packed.Sorted);
        Assert.False(packed.TryGet("refs/heads/missing", out _, out _));
    }

    [Fact]
    public void No_header_is_not_fully_peeled()
    {
        var lines = File.ReadAllLines(PackedFile);
        Assert.StartsWith("# pack-refs with:", lines[0], StringComparison.Ordinal);
        WritePacked(lines[1..]);

        var packed = PackedRefsReader.TryRead(GitDir);
        Assert.NotNull(packed);
        Assert.False(packed.FullyPeeled);
        Assert.False(packed.Sorted);
        Assert.Equal(ShowRef().Count, packed.Count);
    }

    [Fact]
    public void Header_without_the_trait_is_not_fully_peeled()
    {
        var lines = File.ReadAllLines(PackedFile);
        lines[0] = "# pack-refs with: peeled sorted ";
        WritePacked(lines);

        var packed = PackedRefsReader.TryRead(GitDir)!;
        Assert.False(packed.FullyPeeled);
        Assert.True(packed.Sorted);
    }

    [Theory]
    [InlineData("not-a-sha refs/heads/bad")]
    [InlineData("1111111111111111111111111111111111111111")]
    [InlineData("^1111111111111111111111111111111111111111")]
    [InlineData("# a stray comment")]
    public void Malformed_line_is_no_file(string bad)
    {
        var lines = File.ReadAllLines(PackedFile).ToList();
        if (bad.StartsWith('^'))
            lines.Insert(1, bad); // a peel line right after the header, with no ref before it
        else
            lines.Add(bad);
        WritePacked([.. lines]);

        Assert.Null(PackedRefsReader.TryRead(GitDir));
    }

    [Fact]
    public void Unterminated_last_line_is_no_file()
    {
        var text = File.ReadAllText(PackedFile).TrimEnd('\n');
        File.WriteAllText(PackedFile, text);

        Assert.Null(PackedRefsReader.TryRead(GitDir));
    }

    [Fact]
    public void Missing_file_is_no_file()
    {
        File.Delete(PackedFile);
        Assert.Null(PackedRefsReader.TryRead(GitDir));
    }

    [Fact]
    public void File_stays_replaceable_after_a_read()
    {
        Assert.NotNull(PackedRefsReader.TryRead(GitDir));
        using (File.Open(PackedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        _repo.Git("tag", "after");
        _repo.Git("pack-refs", "--all");
        Assert.True(PackedRefsReader.TryRead(GitDir)!.TryGet("refs/tags/after", out _, out _));
    }

    [Fact]
    public void Sha256_entries_are_read()
    {
        using var repo = TestRepositoryBuilder.Create();
        var gitDir = Path.Combine(repo.Path, ".git");
        foreach (var f in Directory.EnumerateFiles(gitDir, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(gitDir, recursive: true);
        repo.Git("init", "-b", "main", "--object-format=sha256");
        repo.Git("config", "user.name", "T");
        repo.Git("config", "user.email", "t@example.com");
        repo.WriteFile("a.txt", "a\n").Stage();
        repo.Git("commit", "-m", "first", "--no-gpg-sign");
        repo.Git("tag", "-a", "v1", "-m", "v1");
        repo.Git("pack-refs", "--all");

        var packed = PackedRefsReader.TryRead(gitDir)!;
        var head = repo.Git("rev-parse", "HEAD").Trim();
        Assert.Equal(64, head.Length);
        Assert.True(packed.TryGet("refs/heads/main", out var main, out _));
        Assert.Equal(head, main.ToString());
        Assert.True(packed.TryGet("refs/tags/v1", out _, out var peel));
        Assert.Equal(head, peel?.ToString());
    }

    private void WritePacked(string[] lines) =>
        File.WriteAllText(PackedFile, string.Join('\n', lines) + "\n"); // LF: git rejects a CR in packed-refs

    /// <summary>Parses each <c>HISTORIA_PERF_REPOS</c> repository's <c>packed-refs</c> (warm, best
    /// of 3). Read-only.</summary>
    [ColdLoadPerfFact]
    public void Parses_a_real_repository_quickly()
    {
        var repos = (Environment.GetEnvironmentVariable("HISTORIA_PERF_REPOS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Directory.Exists);
        foreach (var path in repos)
        {
            var gitDir = Path.Combine(path, ".git");
            var best = double.MaxValue;
            PackedRefs? packed = null;
            for (var round = 0; round < 3; round++)
            {
                var sw = Stopwatch.StartNew();
                packed = PackedRefsReader.TryRead(gitDir);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            output.WriteLine($"{path}: {packed?.Count ?? -1} packed refs, fully-peeled={packed?.FullyPeeled}, best {best:F0} ms");
        }
    }
}
