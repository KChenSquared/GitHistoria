
namespace Historia.Services.Graph;

/// <summary>
/// One node of the sidebar ref tree. <see cref="Leaf"/> is non-null iff this node
/// is a branch; a node can be both a folder and a branch (Git allows <c>feature</c> and
/// <c>feature/x</c> to coexist), in which case it is materialised as a folder whose first child
/// is the branch itself.
/// </summary>
public sealed record RefTreeNode(
    string Segment,
    string FullPath,
    IReadOnlyList<RefTreeNode> Children,
    BranchInfo? Leaf)
{
    public bool IsFolder => Leaf is null;
}

/// <summary>Pure, repository-free builder for the sidebar ref tree.</summary>
public static class RefTree
{
    private sealed class Builder(string segment, string fullPath)
    {
        public string Segment { get; } = segment;
        public string FullPath { get; } = fullPath;
        public BranchInfo? Leaf { get; set; }
        public Dictionary<string, Builder> Children { get; } = new(StringComparer.Ordinal);
    }

    /// <param name="path">Extracts the <c>/</c>-separated path a branch nests under — the full
    /// name for a local branch, the name minus the remote prefix for a remote branch.</param>
    public static IReadOnlyList<RefTreeNode> Build(IEnumerable<BranchInfo> branches, Func<BranchInfo, string> path)
    {
        var root = new Builder("", "");
        foreach (var b in branches)
        {
            var segments = path(b).Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                continue;

            var node = root;
            var acc = "";
            foreach (var seg in segments)
            {
                acc = acc.Length == 0 ? seg : acc + "/" + seg;
                if (!node.Children.TryGetValue(seg, out var child))
                    node.Children[seg] = child = new Builder(seg, acc);
                node = child;
            }
            node.Leaf = b;
        }

        return Materialize(root);
    }

    private static IReadOnlyList<RefTreeNode> Materialize(Builder parent)
    {
        var result = new List<RefTreeNode>(parent.Children.Count);
        foreach (var child in parent.Children.Values)
        {
            var kids = Materialize(child);
            if (child.Leaf is { } leaf && kids.Count > 0)
            {
                // Both a folder and a branch: folder, with the branch itself as the first child.
                var withSelf = new List<RefTreeNode>(kids.Count + 1)
                {
                    new(child.Segment, child.FullPath, [], leaf),
                };
                withSelf.AddRange(kids);
                result.Add(new RefTreeNode(child.Segment, child.FullPath, withSelf, null));
            }
            else if (kids.Count > 0)
            {
                result.Add(new RefTreeNode(child.Segment, child.FullPath, kids, null));
            }
            else
            {
                result.Add(new RefTreeNode(child.Segment, child.FullPath, [], child.Leaf));
            }
        }

        // one flat alphabetical order — folders are not hoisted above leaves.
        // Case-insensitive first (the natural "alphabetical"), ordinal as a stable tie-break.
        result.Sort((x, y) =>
        {
            var c = string.Compare(x.Segment, y.Segment, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(x.Segment, y.Segment);
        });
        return result;
    }
}
