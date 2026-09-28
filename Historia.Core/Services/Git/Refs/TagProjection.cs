namespace Historia.Services.Git;

/// <summary>
/// Builds the tag list (<see cref="TagInfo"/>) from a <see cref="RefSnapshot"/>: the peeled commit when
/// the tag reaches one, otherwise the object it points at (a tag of a tree keeps the tree id);
/// tagger and message only for annotated tags, the message byte-for-byte as stored (the scan
/// reads git's output byte-exact, so CRLF and lone CR survive). A tag whose own object, or the
/// object it tags, is missing is not in the snapshot, so it is left out.
/// <para>Order is ordinal by name.</para>
/// </summary>
public static class TagProjection
{
    private const string Prefix = "refs/tags/";

    public static IReadOnlyList<TagInfo> FromSnapshot(RefSnapshot snapshot)
    {
        var result = new List<TagInfo>();
        foreach (var r in snapshot.Refs)
        {
            if (!r.CanonicalName.StartsWith(Prefix, StringComparison.Ordinal))
                continue;
            if ((r.PeeledCommit ?? r.Peeled ?? r.Target) is not { } target)
                continue;

            result.Add(new TagInfo
            {
                Name = r.CanonicalName[Prefix.Length..],
                TargetCommitId = target,
                IsAnnotated = r.IsAnnotatedTag,
                Message = r.Message,
                TaggerName = r.TaggerName,
                TaggerWhen = r.TaggerWhen,
            });
        }
        return result;
    }
}
