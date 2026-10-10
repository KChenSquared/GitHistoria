namespace Historia.Services.Graph;

/// <summary>
/// One lock object per repo cache stem, shared by
/// every <see cref="GraphMetaWriter"/> for that repo (the active tab's own spill, an inactive
/// tab's headless spool/reorder). Every <c>.meta.*</c> mutation — <see cref="GraphMetaWriter.Reset"/>,
/// an append + <see cref="GraphMetaWriter.Commit"/>, <see cref="GraphMetaWriter.WriteOrder"/>,
/// <see cref="GraphMetaWriter.WriteTips"/>, <see cref="GraphMetaWriter.CommitReorder"/> — takes
/// this lock and re-checks the on-disk header before writing, so two writers racing for the same
/// repo's store can never interleave (see <see cref="GraphMetaWriter.AppendSpillBatch"/> and
/// <see cref="GraphMetaWriter.TryCommitReorder"/>). Keyed by stem, not repo path, since the stem
/// is already the case-folded, slash-normalised identity the files themselves are named after.
/// </summary>
internal static class GraphCacheLocks
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Locks =
        new(StringComparer.Ordinal);

    public static object For(string repoPath) =>
        Locks.GetOrAdd(GraphCachePaths.Stem(repoPath), static _ => new object());
}
