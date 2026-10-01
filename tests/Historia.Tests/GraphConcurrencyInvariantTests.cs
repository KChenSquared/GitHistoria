namespace Historia.Tests;

/// <summary>
/// A seeded, randomized interleaving of every concurrent actor of the graph cache and store, with
/// the invariants checked throughout. A failure begins <c>seed=&lt;n&gt;</c>; pin one seed with
/// <c>HISTORIA_CONCURRENCY_SEED</c>. <c>HISTORIA_CONCURRENCY_SOAK_SECONDS</c> turns the soak test
/// on: random seeds, one after another, for that many seconds.
/// </summary>
[Collection("serial")]
[Trait("tier", "git")]
public sealed class GraphConcurrencyInvariantTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Activity = TimeSpan.FromMilliseconds(600);

    private static int? PinnedSeed() =>
        int.TryParse(Environment.GetEnvironmentVariable("HISTORIA_CONCURRENCY_SEED"), out var seed) ? seed : null;

    public static IEnumerable<object[]> FixedSeeds() =>
        PinnedSeed() is { } pinned ? [[pinned]] : [[1], [2], [3], [4], [5], [6]];

    [Theory(Timeout = 120_000)]
    [MemberData(nameof(FixedSeeds))]
    public async Task Fixed_seeds_hold_every_invariant(int seed)
    {
        await using var harness = await GraphConcurrencyHarness.CreateAsync(seed, Activity, output);
        await harness.RunAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task The_oracle_agrees_with_a_clean_single_actor_run()
    {
        await using var harness = await GraphConcurrencyHarness.CreateAsync(0, Activity, output);
        await harness.RunCleanForegroundAsync();
    }

    [Fact]
    public async Task Soak()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("HISTORIA_CONCURRENCY_SOAK_SECONDS"), out var seconds) || seconds <= 0)
        {
            output.WriteLine("skipped: HISTORIA_CONCURRENCY_SOAK_SECONDS is not set");
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var pinned = PinnedSeed();
        var random = new Random();
        var runs = 0;
        while (DateTime.UtcNow < deadline)
        {
            var seed = pinned ?? random.Next();
            await using var harness = await GraphConcurrencyHarness.CreateAsync(seed, Activity, output);
            await harness.RunAsync();
            runs++;
        }
        output.WriteLine($"soak finished: {runs} runs in {seconds} s");
    }
}

/// <summary>The checker has to be able to fail, or a green run proves nothing.</summary>
[Trait("tier", "unit")]
public sealed class GraphConcurrencyHarnessSelfTests
{
    // Ids start at 1: the all-zero id is the stub row.
    private static string Id(int i) => (i + 1).ToString("x40");

    [Fact]
    public void An_injected_duplicate_row_fails_I1()
    {
        var checker = new GraphInvariantChecker();
        checker.AddReference([.. Enumerable.Range(0, 10).Select(Id)]);

        checker.CheckLayout("clean", [Id(0), Id(1), Id(2)]);
        checker.Settle(refChangesInFlight: false);
        Assert.Empty(checker.Violations);

        checker.CheckLayout("injected", [Id(0), Id(1), Id(0)]);
        Assert.Contains(checker.Violations, v => v.StartsWith("I1"));
    }

    [Fact]
    public void A_layout_that_matches_no_history_fails_I1_once_ref_changes_settle()
    {
        var checker = new GraphInvariantChecker();
        checker.AddReference([.. Enumerable.Range(0, 10).Select(Id)]);

        checker.CheckLayout("reordered", [Id(0), Id(2), Id(1)]);
        checker.Settle(refChangesInFlight: true);
        Assert.Empty(checker.Violations);

        checker.AddReference([Id(0), Id(2), Id(1)]);
        checker.Settle(refChangesInFlight: false);
        Assert.Empty(checker.Violations);

        checker.CheckLayout("still wrong", [Id(5), Id(6)]);
        checker.Settle(refChangesInFlight: false);
        Assert.Contains(checker.Violations, v => v.StartsWith("I1"));
    }

    [Fact]
    public void A_stub_row_matches_any_position_but_a_wrong_row_does_not()
    {
        var checker = new GraphInvariantChecker();
        checker.AddReference([.. Enumerable.Range(0, 10).Select(Id)]);

        checker.CheckLayout("stub", [Id(0), GraphInvariantChecker.StubId, Id(2)]);
        checker.Settle(refChangesInFlight: false);
        Assert.Empty(checker.Violations);

        checker.CheckLayout("wrong", [Id(0), Id(7), Id(2)]);
        checker.Settle(refChangesInFlight: false);
        Assert.NotEmpty(checker.Violations);
    }

    [Fact]
    public void An_exhausted_store_must_be_one_whole_history()
    {
        var checker = new GraphInvariantChecker();
        var history = Enumerable.Range(0, 10).Select(Id).ToArray();
        checker.AddReference(history);

        Assert.True(checker.IsWholeReference(history));
        Assert.False(checker.IsWholeReference([.. history, .. history.Take(3)]));
        Assert.False(checker.IsWholeReference(history.Take(9).ToArray()));
    }
}
