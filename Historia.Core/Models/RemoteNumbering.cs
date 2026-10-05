namespace Historia.Models;

/// <summary>
/// Each configured remote's 1-based position in alphabetical order, shown on the remote's cloud
/// icon in the sidebar and on graph badges. Numbers are only handed out when two or more remotes
/// are configured; with one remote every lookup returns 0 and the UI shows a plain cloud. Tooltip
/// strings are built once here so render paths never format them.
/// </summary>
public sealed class RemoteNumbering
{
    private sealed class NameComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            var c = StringComparer.OrdinalIgnoreCase.Compare(x, y);
            return c != 0 ? c : StringComparer.Ordinal.Compare(x, y);
        }
    }

    /// <summary>Case-insensitive alphabetical order, ties (names differing only in case) broken
    /// ordinally so the order is total and stable. The sidebar sorts remote rows with this too, so
    /// a remote's number matches its row position.</summary>
    public static IComparer<string> Comparer { get; } = new NameComparer();

    public static RemoteNumbering Empty { get; } = new(new Dictionary<string, (int, string)>(StringComparer.Ordinal), 0);

    private readonly Dictionary<string, (int Number, string Tooltip)> _byName;

    private RemoteNumbering(Dictionary<string, (int Number, string Tooltip)> byName, int count)
    {
        _byName = byName;
        Count = count;
    }

    /// <summary>How many distinct remote names were numbered.</summary>
    public int Count { get; }

    /// <summary>True when two or more remotes are configured, so numbers are shown.</summary>
    public bool IsActive => Count >= 2;

    public static RemoteNumbering From(IEnumerable<string> configuredNames)
    {
        var names = configuredNames.Distinct(StringComparer.Ordinal).ToList();
        names.Sort(Comparer);
        var active = names.Count >= 2;
        var byName = new Dictionary<string, (int, string)>(names.Count, StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
            byName[names[i]] = active ? (i + 1, $"{i + 1} · {names[i]}") : (0, names[i]);
        return new RemoteNumbering(byName, names.Count);
    }

    /// <summary>The remote's number, or 0 when it is not configured or numbering is inactive.</summary>
    public int NumberOf(string name) => _byName.TryGetValue(name, out var e) ? e.Number : 0;

    /// <summary>"<c>n · name</c>" for a numbered remote, otherwise the name itself.</summary>
    public string TooltipOf(string name) => _byName.TryGetValue(name, out var e) ? e.Tooltip : name;
}
