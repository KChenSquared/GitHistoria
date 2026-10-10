namespace Historia.Services.Graph;

/// <summary>
/// The pure, storage-agnostic half of "reorder
/// instead of re-spill" — given the existing store's SHA -&gt; record-index table and a freshly
/// walked history in the new order, decides which rows can reuse an existing record and which
/// commits genuinely need a new one. Deliberately has no file or git access: the caller
/// (<see cref="GraphLayoutService"/>'s reorder job) is responsible for building
/// <paramref name="existingRecords"/> (<see cref="DiskCommitStore.BuildShaIndex"/>), streaming
/// the new walk, and afterwards appending <c>NewRecords</c> and writing the order — this class
/// only computes what those steps should be, so it can be unit-tested without a live git process
/// or real files, the same way <see cref="GraphMetaStore"/>'s own tests work against synthetic
/// records.
/// </summary>
internal static class GraphMetaReorder
{
    public readonly record struct Plan(int[] Order, IReadOnlyList<RawCommit> NewRecords);

    /// <summary>
    /// One-shot convenience for callers (and tests) that already have the whole new walk
    /// resident — equivalent to a single <see cref="GraphMetaReorderBuilder.MapBatch"/> call.
    /// <see cref="GraphLayoutService.RunHeadlessReorderAsync"/> uses the streaming builder
    /// directly instead, so it never has to materialize the whole new history at
    /// once. <paramref name="existingRecords"/>: every commit sha already durable in the store,
    /// mapped to its record index (<see cref="DiskCommitStore.RecordCount"/> is the count BEFORE
    /// this call — new records are numbered starting there). <paramref name="newWalkCommits"/>:
    /// the new history, in the new walk's real row order. Returns the row -&gt; record order to
    /// write and the commits (in walk order) that need a brand-new record appended — a commit
    /// whose sha already has a record, whether from <paramref name="existingRecords"/> or from
    /// earlier in this very walk (a defensive case; the walk itself should never revisit a sha),
    /// is never duplicated.
    /// </summary>
    public static Plan Build(
        CompactShaIndex existingRecords, int existingRecordCount, IReadOnlyList<RawCommit> newWalkCommits)
    {
        var builder = new GraphMetaReorderBuilder(existingRecords, existingRecordCount);
        var newRecords = builder.MapBatch(newWalkCommits);
        return new Plan(builder.Order, newRecords);
    }
}

/// <summary>
/// The streaming half of "reorder instead of re-spill" — maps successive BATCHES of a
/// freshly walked history to records, so a caller never has to hold the whole new walk resident
/// (holding it all costs ~490 MB of <see cref="RawCommit"/>s on linux plus the SHA table). Deliberately has no file or git access, like the one-shot
/// <see cref="GraphMetaReorder.Build"/> it generalizes — the caller
/// (<see cref="GraphLayoutService.RunHeadlessReorderAsync"/>) is responsible for calling
/// <see cref="MapBatch"/> once per walked batch, durably appending each batch's returned new
/// records (<see cref="GraphMetaWriter.AppendReorderBatch"/>) before asking for the next batch,
/// and finally writing <see cref="Order"/> once the walk is exhausted.
/// </summary>
internal sealed class GraphMetaReorderBuilder
{
    private readonly CompactShaIndex _existingRecords;
    private readonly CompactShaIndex _assignedThisWalk = new();
    private readonly List<int> _order = [];
    private int _nextRecord;

    public GraphMetaReorderBuilder(CompactShaIndex existingRecords, int existingRecordCount)
    {
        _existingRecords = existingRecords;
        _nextRecord = existingRecordCount;
    }

    /// <summary>Total new records assigned across every <see cref="MapBatch"/> call so far.</summary>
    public int NewRecordCount { get; private set; }

    /// <summary>The row -&gt; record order accumulated across every <see cref="MapBatch"/> call so
    /// far, in walk order. Only meaningful once the whole walk is exhausted.</summary>
    public int[] Order => [.. _order];

    /// <summary>Maps one batch of freshly walked commits (in walk order) to records — reusing an
    /// existing record by id where possible (from <paramref name="batch"/>'s own earlier commits
    /// in this same walk, or from the store this reorder started from), otherwise assigning a new
    /// one — and returns just the commits in THIS batch that need a brand-new record (the caller
    /// appends those; an already-known commit needs nothing written).</summary>
    public List<RawCommit> MapBatch(IReadOnlyList<RawCommit> batch)
    {
        var newRecords = new List<RawCommit>();
        foreach (var commit in batch)
        {
            if (_existingRecords.TryGetValue(commit.Id, out var rec) || _assignedThisWalk.TryGetValue(commit.Id, out rec))
            {
                _order.Add(rec);
                continue;
            }
            rec = _nextRecord++;
            _assignedThisWalk.Add(commit.Id, rec);
            _order.Add(rec);
            newRecords.Add(commit);
        }
        NewRecordCount += newRecords.Count;
        return newRecords;
    }
}
