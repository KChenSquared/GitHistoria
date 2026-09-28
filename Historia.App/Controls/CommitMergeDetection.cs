namespace Historia.Controls;

/// <summary>
/// Determines whether a commit is a merge commit (parent count &gt; 1).
/// Reads from the layout row only when the query the layout was built with is known and
/// unfiltered; a path-filtered walk rewrites parent lists so git itself must be consulted.
/// </summary>
internal static class CommitMergeDetection
{
    public static async Task<bool> IsMergeAsync(
        GraphLayoutResult? layout,
        GraphQuery? layoutQuery,
        IGitGateway gateway,
        GitObjectId id,
        CancellationToken ct)
    {
        if (layoutQuery is not null && string.IsNullOrEmpty(layoutQuery.PathFilter)
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
