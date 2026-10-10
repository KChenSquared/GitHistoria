namespace Historia.Services.Graph;

public sealed partial class GraphLayoutService
{
    /// <summary>Reuses the per-path decoration cache while the ref fingerprint is unchanged;
    /// otherwise rebuilds it with <see cref="BuildDecorations"/>.</summary>
    private static (Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> Decorations, bool Fresh) ResolveDecorations(
        GraphQuery query, RefSnapshot snapshot, DecoCache? decoCache, long fingerprint,
        IReadOnlyList<string> remoteNames)
    {
        if (decoCache is not null && fingerprint != 0 && decoCache.Fingerprint == fingerprint)
            return (decoCache.Decos, false);

        Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> decorations;
        using (Diagnostics.PerfLog.Scope("BuildDecorations"))
            decorations = BuildDecorations(query, snapshot, remoteNames);
        return (decorations, true);
    }

    private enum Family { Branch, Tag }

    private sealed class BadgeBuilder
    {
        public required Family Family;
        public required string Label;
        public bool IsLocal;
        public bool IsHead;
        public readonly List<string> Remotes = [];
        public int Order; // 0 local, 1 remote-only, 2 tag
    }

    /// <summary>
    /// Builds the per-commit decoration list, folding a branch that exists both locally and on
    /// one or more remotes into a single badge. Works straight off
    /// <paramref name="snapshot"/> — branch tips are the ref's resolved target, tags their
    /// peeled commit, with no object load; only the remote names come from
    /// CLI readers. The remote prefix is stripped by matching the
    /// configured remote names (longest first), falling back to the first path segment only for
    /// an orphan remote-tracking ref whose remote no longer exists.
    /// </summary>
    internal static Dictionary<GitObjectId, IReadOnlyList<GraphDecorationInput>> BuildDecorations(
        GraphQuery query, RefSnapshot snapshot, IReadOnlyList<string> remoteNames)
    {
        // commit id → ((family, shortName) → builder)
        var map = new Dictionary<GitObjectId, Dictionary<(Family, string), BadgeBuilder>>();

        BadgeBuilder Get(GitObjectId id, Family family, string label)
        {
            if (!map.TryGetValue(id, out var byKey))
                map[id] = byKey = [];
            var key = (family, label);
            if (!byKey.TryGetValue(key, out var b))
                byKey[key] = b = new BadgeBuilder { Family = family, Label = label };
            return b;
        }

        var sortedRemoteNames = remoteNames.OrderByDescending(n => n.Length).ToArray();

        foreach (var r in snapshot.Refs)
        {
            var name = r.CanonicalName;

            if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                if (r.Target is { } tip)
                    Get(tip, Family.Branch, name["refs/heads/".Length..]).IsLocal = true;
            }
            else if (name.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                if (!query.IncludeRemoteBranches)
                    continue;
                var rest = name["refs/remotes/".Length..];
                var remote = Array.Find(sortedRemoteNames, rn => rest.StartsWith(rn + "/", StringComparison.Ordinal));
                string shortName;
                if (remote is not null)
                {
                    shortName = rest[(remote.Length + 1)..];
                }
                else
                {
                    var slash = rest.IndexOf('/');
                    if (slash < 0)
                        continue;
                    remote = rest[..slash];
                    shortName = rest[(slash + 1)..];
                }
                // refs/remotes/<remote>/HEAD is a symbolic pointer to the remote's default
                // branch, not a branch of its own — never a badge.
                if (string.Equals(shortName, "HEAD", StringComparison.Ordinal))
                    continue;
                if (r.Target is not { } tip)
                    continue;
                var b = Get(tip, Family.Branch, shortName);
                if (!b.Remotes.Contains(remote))
                    b.Remotes.Add(remote);
            }
            else if (name.StartsWith("refs/tags/", StringComparison.Ordinal))
            {
                if (r.PeeledCommit is { } id)
                    Get(id, Family.Tag, name["refs/tags/".Length..]);
            }
        }

        // HEAD folds into its branch badge; a standalone HEAD badge only when detached.
        if (snapshot.HeadTarget is { } headTip)
        {
            if (snapshot.IsHeadDetached)
                Get(headTip, Family.Branch, "HEAD").IsHead = true;
            else
            {
                var t = snapshot.HeadSymref;
                var shortName = t is not null && t.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? t["refs/heads/".Length..]
                    : t ?? "HEAD";
                Get(headTip, Family.Branch, shortName).IsHead = true;
            }
        }

        var defaultBranch = ResolveDefaultBranchShortName(snapshot);
        return map.ToDictionary(
            kv => kv.Key,
            kv => BuildInputs(kv.Value.Values, defaultBranch));
    }

    /// <summary>
    /// ordering source: the branch whose badge sorts first when several sit on one commit.
    /// Precedence: the currently checked-out branch, then <c>origin/HEAD</c>'s target, then a
    /// branch literally named <c>main</c>, then <c>master</c>, else none.
    /// </summary>
    private static string? ResolveDefaultBranchShortName(RefSnapshot snapshot)
    {
        if (!snapshot.IsHeadUnborn && !snapshot.IsHeadDetached)
        {
            var t = snapshot.HeadSymref;
            return t is not null && t.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? t["refs/heads/".Length..]
                : t;
        }

        if (snapshot.Find("refs/remotes/origin/HEAD")?.SymrefTarget is { } target)
        {
            const string prefix = "refs/remotes/";
            var s = target.StartsWith(prefix, StringComparison.Ordinal) ? target[prefix.Length..] : target;
            var slash = s.IndexOf('/');
            return slash >= 0 ? s[(slash + 1)..] : s;
        }

        if (snapshot.Find("refs/heads/main") is not null) return "main";
        if (snapshot.Find("refs/heads/master") is not null) return "master";
        return null;
    }

    /// <summary>
    /// as soon as a commit carries at least one branch, that branch's badge stands in for
    /// every ref on the commit — any further branches (default‑first, then alphabetical) and
    /// then any tags ride along in <c>Siblings</c>, shown in the badge's interactive hover list
    /// and folded into its <c>+N</c> counter. A detached HEAD keeps its own badge;
    /// a commit with no branch at all leaves tags as their own badges.
    /// </summary>
    private static bool IsDetachedHead(BadgeBuilder b) =>
        b.Family == Family.Branch && b.IsHead && !b.IsLocal && b.Remotes.Count == 0;

    private static IReadOnlyList<GraphDecorationInput> BuildInputs(IEnumerable<BadgeBuilder> badges, string? defaultBranch)
    {
        var all = badges.ToList();
        // A standalone detached-HEAD badge is never part of a collapsible branch group.
        var branches = all.Where(b => b.Family == Family.Branch && !IsDetachedHead(b)).ToList();

        // No branch to fold into → tags keep their own badges.
        if (branches.Count == 0)
            return Order(all).Select(b => ToInput(b)).ToList();

        // A detached HEAD is still drawn as its own badge.
        var separate = all.Where(IsDetachedHead)
            .OrderBy(b => b.Label, StringComparer.Ordinal)
            .Select(b => ToInput(b));

        branches.Sort((x, y) =>
        {
            var xd = string.Equals(x.Label, defaultBranch, StringComparison.Ordinal) ? 0 : 1;
            var yd = string.Equals(y.Label, defaultBranch, StringComparison.Ordinal) ? 0 : 1;
            return xd != yd ? xd - yd : string.CompareOrdinal(x.Label, y.Label);
        });

        // Siblings: the further branches (default-first / alphabetical), then the tags. Empty
        // when there is a single branch and no tags — then the head badge folds nothing.
        var tags = all.Where(b => b.Family == Family.Tag)
            .OrderBy(b => b.Label, StringComparer.Ordinal);
        var siblings = branches.Skip(1).Concat(tags).Select(b => ToInput(b)).ToArray();

        var head = ToInput(branches[0], siblings);
        return new[] { head }.Concat(separate).ToList();
    }

    private static IEnumerable<BadgeBuilder> Order(IEnumerable<BadgeBuilder> badges)
    {
        foreach (var b in badges)
            b.Order = b.Family switch
            {
                Family.Tag => 2,
                _ => b.IsLocal ? 0 : 1,
            };
        return badges.OrderBy(b => b.Order).ThenBy(b => b.Label, StringComparer.Ordinal);
    }

    private static GraphDecorationInput ToInput(BadgeBuilder b, GraphDecorationInput[]? siblings = null)
    {
        var type = b.Family switch
        {
            Family.Tag => DecorationType.Tag,
            _ when b.IsHead && !b.IsLocal && b.Remotes.Count == 0 => DecorationType.Head, // detached HEAD
            _ => b.IsLocal ? DecorationType.LocalBranch : DecorationType.RemoteBranch,
        };
        return new GraphDecorationInput(
            type,
            b.Label,
            IsLocal: b.IsLocal,
            Remotes: b.Remotes.Count > 0 ? b.Remotes.ToArray() : null,
            IsHead: b.IsHead,
            Siblings: siblings is { Length: > 0 } ? siblings : null);
    }
}
