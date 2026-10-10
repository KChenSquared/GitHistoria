namespace Historia.Services.Graph;

/// <summary>
/// A SHA -&gt; record-index table keyed on the first 8 bytes of the commit id (a
/// <c>long</c>) rather than its 40-char hex string — at scale a hex-string table is the
/// dominant memory cost (~150-180 MB at 1.5M records on linux), not the record indices
/// themselves. Every lookup still verifies the full id, so a prefix collision (two different
/// commits sharing the same first 8 bytes — astronomically unlikely for real SHA-1s, but not
/// provably impossible) is always resolved correctly via a small side list rather than silently
/// returning the wrong record.
/// </summary>
internal sealed class CompactShaIndex(int capacity = 0)
{
    private readonly Dictionary<long, (GitObjectId Id, int Record)> _byPrefix = new(capacity);
    private List<(GitObjectId Id, int Record)>? _collisions;

    public int Count { get; private set; }

    private static long Prefix(GitObjectId id)
    {
        var span = id.AsSpan();
        return span.Length >= 8 ? BitConverter.ToInt64(span) : 0;
    }

    public void Add(GitObjectId id, int record)
    {
        Count++;
        var prefix = Prefix(id);
        if (_byPrefix.TryAdd(prefix, (id, record)))
            return;
        (_collisions ??= []).Add((id, record));
    }

    public bool TryGetValue(GitObjectId id, out int record)
    {
        var prefix = Prefix(id);
        if (_byPrefix.TryGetValue(prefix, out var primary) && primary.Id.Equals(id))
        {
            record = primary.Record;
            return true;
        }
        if (_collisions is not null)
        {
            foreach (var (cid, crecord) in _collisions)
            {
                if (cid.Equals(id))
                {
                    record = crecord;
                    return true;
                }
            }
        }
        record = 0;
        return false;
    }
}
