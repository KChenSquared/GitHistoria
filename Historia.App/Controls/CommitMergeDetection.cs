namespace Historia.Controls;

/// <summary>
/// Determines whether a commit is a merge commit (parent count &gt; 1).
/// Reads from the layout row only when the layout query is unfiltered;
/// a path-filtered walk rewrites parent lists so git itself must be consulted.
/// </summary>
internal static class CommitMergeDetection
{
    public static async Task<bool> IsMergeAsync(
        GraphLayoutResult? layout,
        GraphQuery query,
        IGitGateway gateway,
        GitObjectId id,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(query.PathFilter)
            && layout is { } lay
            && lay.Layout.RowByCommitId.TryGetValue(id, out var row)
            && row >= 0 && row < lay.Layout.Commits.Count)
        {
            return lay.Layout.Commits[row].ParentCount > 1;
        }

        try
        {
            var detail = await gateway.GetCommitDetailAsync(id, ct).ConfigureAwait(false);
            return detail.ParentIds.Count > 1;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
