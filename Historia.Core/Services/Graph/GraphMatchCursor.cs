namespace Historia.Services.Graph;

/// <summary>
/// The commit-graph find state: the ordered list of matching rows plus the cursor
/// that Enter / Shift+Enter / F3 steps through. Lifted out of <c>CommitGraphControl</c> so the
/// wrap-at-ends stepping and the "n / m" label are unit-testable (debloat audit T3.2). The
/// control keeps the scroll-into-view and selection side effects.
/// </summary>
public sealed class GraphMatchCursor
{
    private readonly List<int> _rows = [];
    private readonly HashSet<int> _set = [];

    /// <summary>Index into <see cref="Rows"/>, or -1 when there are no matches.</summary>
    public int Cursor { get; private set; } = -1;

    public IReadOnlyList<int> Rows => _rows;

    public int Count => _rows.Count;

    public bool HasMatches => _rows.Count > 0;

    /// <summary>The matched-row set for the render pass, or <c>null</c> when there are no
    /// matches (so the highlight lookup can be skipped entirely).</summary>
    public IReadOnlySet<int>? RowSet => _rows.Count > 0 ? _set : null;

    /// <summary>The row under the cursor, or -1 when there are no matches.</summary>
    public int CurrentRow => Cursor >= 0 && Cursor < _rows.Count ? _rows[Cursor] : -1;

    /// <summary>Replace the match set. The cursor moves to the first match, or -1 when empty.</summary>
    public void Reset(IEnumerable<int> rows)
    {
        _rows.Clear();
        _set.Clear();
        foreach (var r in rows)
        {
            _rows.Add(r);
            _set.Add(r);
        }
        Cursor = _rows.Count > 0 ? 0 : -1;
    }

    /// <summary>Clear every match.</summary>
    public void Clear() => Reset([]);

    /// <summary>Advance the cursor by <paramref name="direction"/> (typically ±1), wrapping at
    /// both ends. Returns the row now under the cursor, or -1 when there are no matches.</summary>
    public int Step(int direction)
    {
        if (_rows.Count == 0)
            return -1;
        Cursor = ((Cursor + direction) % _rows.Count + _rows.Count) % _rows.Count;
        return _rows[Cursor];
    }

    /// <summary>The "n / m" match-count label. <paramref name="hasSearchText"/> separates an
    /// empty search box ("") from a search that matched nothing ("0 / 0").</summary>
    public string Label(bool hasSearchText) =>
        _rows.Count == 0
            ? (hasSearchText ? "0 / 0" : "")
            : $"{Cursor + 1} / {_rows.Count}";
}
