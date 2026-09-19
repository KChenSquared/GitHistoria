namespace Historia.Tests;

/// <summary>
/// §3 linux cold-prime plan: <see cref="GraphWalkCache"/> now records which
/// <see cref="WalkEngine"/> wrote a saved walk (version bump 2 -> 3). The CLI streaming walk and
/// the libgit2 revwalk can order the same ref set's history differently, so a NON-exhausted save
/// (one that a later call would resume with <c>skip: have</c>) must be a miss for the other
/// engine — resuming it under the wrong row order would silently duplicate or drop rows. An
/// EXHAUSTED save carries the whole history already, so there is nothing left to resume and it
/// stays reusable regardless of which engine produced it.
/// </summary>
[Trait("tier", "unit")]
public sealed class GraphWalkCacheEngineMismatchTests : IDisposable
{
    private readonly StateDir _state = new();
    private const string Repo = @"C:\repos\walkcache-demo";
    private static readonly GraphQuery Query = new() { RefScope = GraphRefScope.AllRefs };
    private static readonly Dictionary<ObjectId, IReadOnlyList<GraphDecorationInput>> NoDecos = [];

    public void Dispose() => _state.Dispose();

    private static ObjectId Oid(int i) => new(i.ToString("x40"));

    private static RawCommit Commit(int i) => new(
        Oid(i),
        i == 0 ? Array.Empty<ObjectId>() : new[] { Oid(i - 1) },
        new CommitMetadata
        {
            Id = Oid(i),
            MessageShort = $"c{i}",
            AuthorName = "Dev",
            AuthorEmail = "dev@example.com",
            AuthorWhen = DateTimeOffset.UnixEpoch.AddMinutes(i),
            CommitWhen = DateTimeOffset.UnixEpoch.AddMinutes(i),
            ParentCount = i == 0 ? 0 : 1,
        });

    private static List<RawCommit> Commits(int n) => Enumerable.Range(0, n).Select(Commit).ToList();

    [Fact]
    public void A_non_exhausted_save_is_a_miss_for_the_other_engine()
    {
        var cache = new GraphWalkCache();
        cache.Save(Repo, Query, fingerprint: 42, exhausted: false, Commits(5), NoDecos, WalkEngine.Cli);

        Assert.NotNull(cache.TryLoad(Repo, Query, wantCount: 5, fingerprint: 42, WalkEngine.Cli));
        Assert.Null(cache.TryLoad(Repo, Query, wantCount: 5, fingerprint: 42, WalkEngine.LibGit2));
    }

    [Fact]
    public void An_exhausted_save_is_reusable_regardless_of_engine()
    {
        // Once the whole history is on disk there is nothing left to resume with skip: have, so a
        // completed save from either engine is safe to hand back as-is.
        var cache = new GraphWalkCache();
        cache.Save(Repo, Query, fingerprint: 99, exhausted: true, Commits(5), NoDecos, WalkEngine.Cli);

        var hit = cache.TryLoad(Repo, Query, wantCount: 5, fingerprint: 99, WalkEngine.LibGit2);
        Assert.NotNull(hit);
        Assert.Equal(5, hit!.Commits.Count);
        Assert.True(hit.Exhausted);
    }

    [Fact]
    public void Fingerprint_mismatch_still_misses_independent_of_engine()
    {
        var cache = new GraphWalkCache();
        cache.Save(Repo, Query, fingerprint: 7, exhausted: true, Commits(3), NoDecos, WalkEngine.Cli);

        Assert.Null(cache.TryLoad(Repo, Query, wantCount: 3, fingerprint: 8, WalkEngine.Cli));
    }
}
