namespace Historia.Models;

/// <summary>The six first-class regions of the graph panel (§V2-6.1).</summary>
public enum GraphColumnKind { Refs, Graph, Message, Author, Date, Sha }

/// <summary>
/// One column of the graph panel. Persisted in <see cref="AppSettings"/> as an ordered list —
/// the list order <i>is</i> the column order (§V2-9.1).
/// </summary>
public sealed record GraphColumn
{
    public required GraphColumnKind Kind { get; init; }

    /// <summary>Width in device-independent units. Ignored while <see cref="IsFlex"/>.</summary>
    public required double Width { get; init; }

    public required bool Visible { get; init; }

    /// <summary>Exactly one visible column absorbs leftover width. Default: Message.</summary>
    public bool IsFlex { get; init; }
}

public static class GraphColumnDefaults
{
    /// <summary>
    /// Pre-measurement floor for the Refs column, safe to use at settings-load time before any
    /// window, typeface or DPI exists (§V3-8.1 Trap). <c>GraphColumnLayout.Resolve</c> raises it
    /// to the measured <c>refsBaseWidth</c> once a control is available.
    /// </summary>
    public const double RefsStaticFloor = 140;

    public static readonly IReadOnlyList<GraphColumn> All =
    [
        new() { Kind = GraphColumnKind.Refs,    Width = RefsStaticFloor, Visible = true  },
        new() { Kind = GraphColumnKind.Graph,   Width = 220,             Visible = true  },
        new() { Kind = GraphColumnKind.Message, Width = 400,             Visible = true, IsFlex = true },
        new() { Kind = GraphColumnKind.Author,  Width = 150,             Visible = false },
        new() { Kind = GraphColumnKind.Date,    Width = 210,             Visible = false },
        new() { Kind = GraphColumnKind.Sha,     Width =  80,             Visible = false },
    ];

    /// <summary>
    /// §V3-8.2 / D2. Keeps its one-argument signature so <c>Normalize</c>, the auto-fit gesture
    /// and the resize gesture all keep compiling. "Minimum 150 for each other column", with SHA
    /// held at 80 — a seven-character hash in a 150 DIU column is dead space.
    /// </summary>
    public static double MinWidthFor(GraphColumnKind kind) => kind switch
    {
        GraphColumnKind.Refs => RefsStaticFloor,
        GraphColumnKind.Sha  => 80,
        // v8.4: the Date cell now carries a full "MM/DD/YYYY - HH:MM AM/PM (ago)" string.
        GraphColumnKind.Date => 200,
        _                    => 150,
    };

    public const double MaxWidth = 2000;

    /// <summary>
    /// §V3-9.4 / §V3-B: at every launch the column <i>order</i> is reset to the canonical order
    /// and every <i>width</i> to its default; only <see cref="GraphColumn.Visible"/> and
    /// <see cref="GraphColumn.IsFlex"/> survive. The caller clears its own graph-width-pin flag.
    /// </summary>
    public static IReadOnlyList<GraphColumn> ResetLayout(IReadOnlyList<GraphColumn>? columns)
    {
        var byKind = (columns ?? All).ToDictionary(c => c.Kind);
        var reset = All.Select(def =>
            byKind.TryGetValue(def.Kind, out var cur)
                ? def with { Visible = cur.Visible }
                : def).ToList();

        // Visibility guard mirrors Normalize: never zero visible or a non-unique flex column.
        if (reset.Count(c => c.Visible) == 0 || reset.Count(c => c.Visible && c.IsFlex) != 1)
            reset = All.ToList();

        return reset;
    }

    /// <summary>
    /// Makes a hand-edited or migrated-forward column list safe to hand to the panel (§V2-11.1):
    /// drop unknown kinds, append missing kinds hidden in default order, keep the first of any
    /// duplicate, clamp widths, and fall back to <see cref="All"/> when nothing is visible or
    /// the flex column is not unique.
    /// </summary>
    public static IReadOnlyList<GraphColumn> Normalize(IReadOnlyList<GraphColumn>? input)
    {
        if (input is null || input.Count == 0)
            return All;

        var known = Enum.GetValues<GraphColumnKind>().ToHashSet();
        var seen = new HashSet<GraphColumnKind>();
        var result = new List<GraphColumn>(known.Count);

        foreach (var col in input)
        {
            if (!known.Contains(col.Kind) || !seen.Add(col.Kind))
                continue;
            result.Add(col with
            {
                Width = Math.Clamp(col.Width, MinWidthFor(col.Kind), MaxWidth),
            });
        }

        // Append any kind the file never mentioned, hidden, in the canonical order.
        foreach (var def in All)
            if (seen.Add(def.Kind))
                result.Add(def with { Visible = false });

        var visible = result.Count(c => c.Visible);
        var flex = result.Count(c => c.Visible && c.IsFlex);
        if (visible == 0 || flex != 1)
            return All;

        return result;
    }
}
