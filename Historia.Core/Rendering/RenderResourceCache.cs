namespace Historia.Rendering;

/// <summary>
/// A bounded LRU cache of render resources with <b>miss and hit counters</b> (constitution
/// rule 24). Control-agnostic on purpose: it knows about keys, values, capacity, and
/// bookkeeping, and nothing about terminals or graphs. The App layer instantiates it with an
/// Avalonia factory (<c>RenderResourceCache&lt;Rgb, IBrush&gt;</c>, <c>&lt;PenKey, IPen&gt;</c>,
/// <c>&lt;int, GlyphInfo&gt;</c>); <see cref="Historia.Core"/> owns the counters so a test can
/// assert on them without referencing the App assembly (V5 §5.3).
/// </summary>
public sealed class RenderResourceCache<TKey, TValue>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly Func<TKey, TValue> _factory;
    private readonly Action<TValue>? _onEvict;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _lru = new();

    public RenderResourceCache(
        int capacity,
        Func<TKey, TValue> factory,
        Action<TValue>? onEvict = null,
        IEqualityComparer<TKey>? comparer = null)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be >= 1");

        _capacity = capacity;
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _onEvict = onEvict;
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
    }

    /// <summary>Resources created because the key was absent. Non-zero on a second identical
    /// frame is a rule-24 regression.</summary>
    public long Misses { get; private set; }

    /// <summary>Resources served from the cache.</summary>
    public long Hits { get; private set; }

    /// <summary>Entries evicted to stay within <see cref="Capacity"/>.</summary>
    public long Evictions { get; private set; }

    public int Capacity => _capacity;
    public int Count => _map.Count;

    public TValue Get(TKey key)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
            Hits++;
            return node.Value.Value;
        }

        Misses++;
        var value = _factory(key);
        var entry = new Entry(key, value);
        var added = _lru.AddFirst(entry);
        _map[key] = added;

        if (_map.Count > _capacity)
            EvictOldest();

        return value;
    }

    private void EvictOldest()
    {
        var last = _lru.Last;
        if (last is null)
            return;
        _lru.RemoveLast();
        _map.Remove(last.Value.Key);
        _onEvict?.Invoke(last.Value.Value);
        Evictions++;
    }

    /// <summary>Drops every entry (e.g. on a theme change) without touching the counters.</summary>
    public void Clear()
    {
        if (_onEvict is not null)
            foreach (var node in _lru)
                _onEvict(node.Value);
        _lru.Clear();
        _map.Clear();
    }

    /// <summary>Zeroes <see cref="Hits"/> / <see cref="Misses"/> / <see cref="Evictions"/> — the
    /// per-frame boundary the allocation tests measure across.</summary>
    public void ResetCounters()
    {
        Hits = 0;
        Misses = 0;
        Evictions = 0;
    }

    private readonly record struct Entry(TKey Key, TValue Value);
}
