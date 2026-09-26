namespace Historia.Services.Git;

/// <summary>
/// One ref as <c>git for-each-ref</c> reports it. <see cref="Target"/> is the object the ref
/// resolves to (for a symbolic ref, the object its target ref points at);
/// <see cref="Peeled"/>/<see cref="PeeledType"/> are set only when <see cref="Target"/> is an
/// annotated tag. The tagger fields and <see cref="Message"/> are set only for annotated tags.
/// </summary>
public sealed record RefEntry(
    string CanonicalName,
    ObjectId? Target,
    string ObjectType,
    ObjectId? Peeled,
    string? PeeledType,
    string? SymrefTarget,
    string? TaggerName,
    DateTimeOffset? TaggerWhen,
    string? Message)
{
    public bool IsAnnotatedTag => ObjectType == "tag";

    /// <summary>The commit this ref reaches — its own target when that is a commit, else the
    /// peeled target of an annotated tag when that is a commit; null for a ref that reaches a
    /// tree or blob (e.g. linux's <c>v2.6.11-tree</c>).</summary>
    public ObjectId? PeeledCommit =>
        ObjectType == "commit" ? Target
        : PeeledType == "commit" ? Peeled
        : null;
}

/// <summary>
/// Every ref of one repository at one moment, read with a single <c>git for-each-ref</c> — the
/// source the commit graph (fingerprint, tip set, decorations) and the tag list read instead of
/// enumerating refs through LibGit2Sharp, which costs ~7× as much on a repo with tens of
/// thousands of tags. Immutable; <see cref="RefSnapshotProvider"/> hands out the same instance
/// until <see cref="Token"/> no longer matches the files on disk.
/// </summary>
public sealed class RefSnapshot
{
    private readonly Dictionary<string, RefEntry> _byName;

    public RefSnapshot(IReadOnlyList<RefEntry> refs, long token, string? headSymref, ObjectId? headTarget)
    {
        Refs = refs;
        Token = token;
        HeadSymref = headSymref;
        HeadTarget = headTarget;
        _byName = new Dictionary<string, RefEntry>(refs.Count, StringComparer.Ordinal);
        foreach (var r in refs)
            _byName[r.CanonicalName] = r;
    }

    /// <summary>Sorted by <see cref="RefEntry.CanonicalName"/> with <see cref="StringComparer.Ordinal"/>.</summary>
    public IReadOnlyList<RefEntry> Refs { get; }

    /// <summary>The <see cref="RefStateToken"/> value read before the scan started — a ref
    /// written during the scan therefore always reads as a change on the next check.</summary>
    public long Token { get; }

    /// <summary>The ref HEAD points at (<c>refs/heads/main</c>); null when HEAD is detached.</summary>
    public string? HeadSymref { get; }

    /// <summary>The object HEAD resolves to; null when HEAD is unborn.</summary>
    public ObjectId? HeadTarget { get; }

    public bool IsHeadDetached => HeadSymref is null && HeadTarget is not null;

    public bool IsHeadUnborn => HeadTarget is null;

    public RefEntry? Find(string canonicalName) =>
        _byName.TryGetValue(canonicalName, out var r) ? r : null;
}
