namespace Historia.Tests;

/// <summary>
/// <see cref="GraphWalkCache"/> records which walk wrote a saved file in its <c>WalkEngine</c>
/// field — always <see cref="GraphWalkCache.CliWalkMarker"/> today. A file carrying any other
/// value (0 is what the retired libgit2 revwalk wrote) may order the same history differently on
/// equal-timestamp ties, so a NON-exhausted one (which a later call would resume with
/// <c>skip: have</c>) must be a miss — resuming it under the wrong row order would silently
/// duplicate or drop rows. An EXHAUSTED one carries the whole history already, so there is
/// nothing left to resume and it stays reusable.
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

    /// <summary>Rewrites the saved file's <c>WalkEngine</c> field, as a file written by a retired
    /// walk engine would carry it.</summary>
    private static void SetWalkMarker(int marker)
    {
        var path = GraphCachePaths.File(Repo, ".json.gz");
        System.Text.Json.Nodes.JsonObject node;
        using (var fs = File.OpenRead(path))
        using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress))
            node = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(gz)!;

        Assert.True(node.ContainsKey("WalkEngine"), "the saved file keeps its WalkEngine field");
        node["WalkEngine"] = marker;

        using (var fs = File.Create(path))
        using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
        using (var writer = new System.Text.Json.Utf8JsonWriter(gz))
            node.WriteTo(writer);
    }

    [Fact]
    public void A_non_exhausted_save_from_a_retired_walk_engine_is_a_miss()
    {
        new GraphWalkCache().Save(Repo, Query, fingerprint: 42, exhausted: false, Commits(5), NoDecos);
        Assert.NotNull(new GraphWalkCache().TryLoad(Repo, Query, wantCount: 5, fingerprint: 42));

        SetWalkMarker(0);

        Assert.Null(new GraphWalkCache().TryLoad(Repo, Query, wantCount: 5, fingerprint: 42));
    }

    [Fact]
    public void An_exhausted_save_from_a_retired_walk_engine_is_still_reusable()
    {
        // Once the whole history is on disk there is nothing left to resume with skip: have, so a
        // completed save is safe to hand back as-is whichever walk produced it.
        new GraphWalkCache().Save(Repo, Query, fingerprint: 99, exhausted: true, Commits(5), NoDecos);
        SetWalkMarker(0);

        var hit = new GraphWalkCache().TryLoad(Repo, Query, wantCount: 5, fingerprint: 99);
        Assert.NotNull(hit);
        Assert.Equal(5, hit!.Commits.Count);
        Assert.True(hit.Exhausted);
    }

    [Fact]
    public void Fingerprint_mismatch_still_misses()
    {
        var cache = new GraphWalkCache();
        cache.Save(Repo, Query, fingerprint: 7, exhausted: true, Commits(3), NoDecos);

        Assert.Null(cache.TryLoad(Repo, Query, wantCount: 3, fingerprint: 8));
    }
}
