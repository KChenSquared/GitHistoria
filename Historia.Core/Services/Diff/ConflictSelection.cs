namespace Historia.Services.Diff;

/// <summary>Check state of one side's lines within a conflict, derived from its rows.</summary>
public enum ConflictHunkState { Unchecked, Partial, Checked }

/// <summary>
/// Pure selection state of one managed conflict region: which ours and theirs rows are kept,
/// and the order the two sides appear in. Knows nothing about the document the rows live in.
/// <para>
/// Order follows clicks: a side moves only when it becomes checked, to directly after the other
/// side if that one is checked, else to the front. Unchecking never moves anything, so rows stay
/// put while the user toggles lines off.
/// </para>
/// </summary>
public sealed class ConflictSelection
{
    private readonly bool[] _ours;
    private readonly bool[] _theirs;
    private readonly ConflictSide[] _sideLayout = [ConflictSide.Ours, ConflictSide.Theirs];
    private List<(ConflictSide Side, int Index)>? _layout;

    public IReadOnlyList<string> OursLines { get; }
    public IReadOnlyList<string> TheirsLines { get; }

    public ConflictSelection(IReadOnlyList<string> oursLines, IReadOnlyList<string> theirsLines)
    {
        OursLines = oursLines;
        TheirsLines = theirsLines;
        _ours = new bool[oursLines.Count];
        _theirs = new bool[theirsLines.Count];
    }

    private ConflictSelection(ConflictSelection source)
    {
        OursLines = source.OursLines;
        TheirsLines = source.TheirsLines;
        _ours = (bool[])source._ours.Clone();
        _theirs = (bool[])source._theirs.Clone();
        _sideLayout[0] = source._sideLayout[0];
        _sideLayout[1] = source._sideLayout[1];
    }

    /// <summary>The two sides in display order.</summary>
    public IReadOnlyList<ConflictSide> SideLayout => _sideLayout;

    /// <summary>Every row of <c>SideLayout[0]</c>, then every row of <c>SideLayout[1]</c>, each in source order.</summary>
    public IReadOnlyList<(ConflictSide Side, int Index)> Layout
    {
        get
        {
            if (_layout == null)
            {
                var list = new List<(ConflictSide, int)>(_ours.Length + _theirs.Length);
                foreach (var side in _sideLayout)
                {
                    int count = Rows(side).Length;
                    for (int i = 0; i < count; i++)
                    {
                        list.Add((side, i));
                    }
                }
                _layout = list;
            }
            return _layout;
        }
    }

    public int RowCount(ConflictSide side) => Rows(side).Length;

    public bool HasRows(ConflictSide side) => Rows(side).Length > 0;

    public bool IsIncluded(ConflictSide side, int index) => Rows(side)[index];

    /// <summary>At least one row of the side is kept.</summary>
    public bool IsChecked(ConflictSide side) => Array.IndexOf(Rows(side), true) >= 0;

    /// <summary>True when no row of either side is kept — the conflict resolves to no lines.</summary>
    public bool IsEmpty => !IsChecked(ConflictSide.Ours) && !IsChecked(ConflictSide.Theirs);

    public ConflictHunkState HunkState(ConflictSide side)
    {
        var rows = Rows(side);
        if (rows.Length == 0)
        {
            return ConflictHunkState.Unchecked;
        }

        int included = 0;
        foreach (var r in rows)
        {
            if (r)
            {
                included++;
            }
        }

        return included == 0 ? ConflictHunkState.Unchecked
            : included == rows.Length ? ConflictHunkState.Checked
            : ConflictHunkState.Partial;
    }

    /// <summary>Keeps (or drops) every row of the side. A side with no rows is left alone.</summary>
    public void SetHunk(ConflictSide side, bool include)
    {
        var rows = Rows(side);
        if (rows.Length == 0)
        {
            return;
        }

        bool wasChecked = IsChecked(side);
        Array.Fill(rows, include);
        if (include && !wasChecked)
        {
            MoveAfterCheckedSide(side);
        }
    }

    public void ToggleRow(ConflictSide side, int index)
    {
        var rows = Rows(side);
        bool wasChecked = IsChecked(side);
        rows[index] = !rows[index];
        if (rows[index] && !wasChecked)
        {
            MoveAfterCheckedSide(side);
        }
    }

    /// <summary>Puts <paramref name="first"/>'s rows before the other side's.</summary>
    public void SetOrder(ConflictSide first)
    {
        if (_sideLayout[0] != first)
        {
            _sideLayout[0] = first;
            _sideLayout[1] = Other(first);
            _layout = null;
        }
    }

    public ConflictSelection Clone() => new(this);

    /// <summary>Same rows kept and same side order.</summary>
    public bool SelectionEquals(ConflictSelection other)
        => _sideLayout[0] == other._sideLayout[0]
           && _ours.AsSpan().SequenceEqual(other._ours)
           && _theirs.AsSpan().SequenceEqual(other._theirs);

    private void MoveAfterCheckedSide(ConflictSide side)
    {
        var other = Other(side);
        SetOrder(IsChecked(other) ? other : side);
    }

    private bool[] Rows(ConflictSide side) => side switch
    {
        ConflictSide.Ours => _ours,
        ConflictSide.Theirs => _theirs,
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, "Only ours and theirs rows can be selected."),
    };

    private static ConflictSide Other(ConflictSide side) => side == ConflictSide.Ours ? ConflictSide.Theirs : ConflictSide.Ours;
}

/// <summary>Where a document line of a managed region came from.</summary>
public enum ConflictRowOrigin { Ours, Theirs, Added }

/// <summary>
/// One document line of a managed region: an ours or theirs row (with its index and original
/// text on that side), or a line the user typed inside the region.
/// </summary>
public readonly record struct ConflictLineOrigin(ConflictRowOrigin Origin, int SourceIndex, string SourceText);

/// <summary>A managed region's document lines, starting at <paramref name="StartLine"/> (0-based).</summary>
public sealed record ConflictManagedSpan(int StartLine, IReadOnlyList<ConflictLineOrigin> Lines)
{
    public int EndLine => StartLine + Lines.Count - 1;
}
