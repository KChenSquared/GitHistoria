namespace Historia.Tests;
[Trait("tier", "git")]

public sealed class TestRepositoryBuilderTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void FromTemplate_returns_an_independent_copy()
    {
        var key = "test-independent-" + Guid.NewGuid().ToString("N");
        using (var copy1 = TestRepositoryBuilder.FromTemplate(key, b => b.LinearHistory(3)))
        {
            copy1.WriteFile("mutated.txt", "hello").Stage().Commit("mutation");
            Assert.True(File.Exists(Path.Combine(copy1.Path, "mutated.txt")));
        }

        using (var copy2 = TestRepositoryBuilder.FromTemplate(key, b => b.LinearHistory(3)))
        {
            Assert.False(File.Exists(Path.Combine(copy2.Path, "mutated.txt")));
            var log = copy2.Git("log", "--oneline").Trim();
            Assert.DoesNotContain("mutation", log);
        }
    }

    [Fact]
    public void FromTemplate_builds_the_template_once()
    {
        var buildCount = 0;
        var key = "test-build-once-" + Guid.NewGuid().ToString("N");

        for (var i = 0; i < 3; i++)
        {
            using var repo = TestRepositoryBuilder.FromTemplate(key, b =>
            {
                Interlocked.Increment(ref buildCount);
                b.LinearHistory(2);
            });
            Assert.NotNull(repo.Path);
        }

        Assert.Equal(1, buildCount);
    }

    [Fact]
    public void LinearHistory_matches_a_CLI_built_history()
    {
        const int count = 5;
        var baseDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        using var cliRepo = TestRepositoryBuilder.Create();
        for (var i = 0; i < count; i++)
        {
            var when = baseDate.AddMinutes(i);
            cliRepo.WriteFile("f.txt", $"{i}\n").Stage().CommitAt($"commit {i:D2}", when);
        }

        using var fiRepo = TestRepositoryBuilder.Create();
        fiRepo.LinearHistory(count, baseDate: baseDate);

        var cliLog = cliRepo.Git("log", "--format=%H|%s|%P", "--reverse").Trim();
        var fiLog = fiRepo.Git("log", "--format=%H|%s|%P", "--reverse").Trim();

        Assert.Equal(cliLog, fiLog);
    }

    [Fact]
    public void Commit_returns_the_same_sha_as_rev_parse()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("a.txt", "1").Stage();
        var sha1 = repo.Commit("first");
        var expectedSha1 = repo.Git("rev-parse", "HEAD").Trim();
        Assert.Equal(expectedSha1, sha1);

        repo.WriteFile("b.txt", "2").Stage();
        var sha2 = repo.CommitAt("second", DateTimeOffset.UtcNow);
        var expectedSha2 = repo.Git("rev-parse", "HEAD").Trim();
        Assert.Equal(expectedSha2, sha2);
    }

    [Fact]
    public void FromTemplate_copy_and_cli_creation_timings_are_recorded()
    {
        const int count = 20;
        // Warm up template
        using (TestRepositoryBuilder.FromTemplate("bench-20", b => b.LinearHistory(count))) { }

        // Measure FromTemplate copy
        int templateCommitCount;
        var sw = Stopwatch.StartNew();
        using (var copy = TestRepositoryBuilder.FromTemplate("bench-20", b => b.LinearHistory(count)))
        {
            templateCommitCount = int.TryParse(copy.Git("rev-list", "--count", "HEAD").Trim(), out var n) ? n : 0;
        }
        sw.Stop();
        var templateMs = sw.ElapsedMilliseconds;

        // Measure CLI creation
        int cliCommitCount;
        sw.Restart();
        using (var cli = TestRepositoryBuilder.Create())
        {
            for (var i = 0; i < count; i++)
                cli.WriteFile("f.txt", $"{i}\n").Stage().Commit($"commit {i:D2}");
            cliCommitCount = int.TryParse(cli.Git("rev-list", "--count", "HEAD").Trim(), out var n) ? n : 0;
        }
        sw.Stop();
        var cliMs = sw.ElapsedMilliseconds;

        // Performance ratio is recorded for visibility rather than gating builds (Constitution Rule 25).
        _output.WriteLine($"Template copy: {templateMs} ms, CLI creation: {cliMs} ms (ratio: {(double)cliMs / Math.Max(1, templateMs):F1}x)");

        Assert.Equal(count, templateCommitCount);
        Assert.Equal(count, cliCommitCount);
    }
}
