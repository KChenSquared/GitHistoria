namespace Historia.Services.Text;

/// <summary>
/// A ranked item produced by fuzzy search scoring.
/// </summary>
public readonly record struct RankedItem<T>(T Item, int Score, IReadOnlyList<TextSpan> Matches);

/// <summary>
/// Pure and allocation-bounded ref ranking service using the VS Code fuzzy scorer.
/// </summary>
public static class RefSearch
{
    /// <summary>
    /// Scores and ranks items against a prepared query, dropping zero scores and ordering by
    /// score descending, then match span compactness, then label length, then ordinal label.
    /// </summary>
    public static IReadOnlyList<RankedItem<T>> Rank<T>(
        IEnumerable<T> items,
        Func<T, string> label,
        PreparedQuery query,
        int limit = int.MaxValue)
    {
        if (items == null || string.IsNullOrWhiteSpace(query.Normalized) || limit <= 0)
            return Array.Empty<RankedItem<T>>();

        var scored = new List<(T Item, string Label, int Score, IReadOnlyList<TextSpan> Matches)>();

        foreach (var item in items)
        {
            string itemLabel = label(item) ?? string.Empty;
            var itemScore = FuzzyScorer.ScoreItem(itemLabel, query);
            if (itemScore.Score > 0)
            {
                scored.Add((item, itemLabel, itemScore.Score, itemScore.Matches));
            }
        }

        if (scored.Count == 0)
            return Array.Empty<RankedItem<T>>();

        // Ported comparer from VS Code fuzzyScorer.ts:
        // score desc -> more compact match span -> shorter label -> ordinal label
        // Historia pins final tiebreak to StringComparer.Ordinal (compareAnything is not ported).
        scored.Sort((a, b) =>
        {
            // 1. Score descending
            if (a.Score != b.Score)
                return b.Score.CompareTo(a.Score);

            // 2. More compact match span (shorter match length)
            int spanA = MatchSpanLength(a.Matches);
            int spanB = MatchSpanLength(b.Matches);
            if (spanA != spanB)
                return spanA.CompareTo(spanB);

            // 3. Shorter label
            if (a.Label.Length != b.Label.Length)
                return a.Label.Length.CompareTo(b.Label.Length);

            // 4. Ordinal label tiebreak
            return string.Compare(a.Label, b.Label, StringComparison.Ordinal);
        });

        int count = Math.Min(scored.Count, limit);
        var result = new RankedItem<T>[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = new RankedItem<T>(scored[i].Item, scored[i].Score, scored[i].Matches);
        }

        return result;
    }

    private static int MatchSpanLength(IReadOnlyList<TextSpan> matches)
    {
        if (matches == null || matches.Count == 0)
            return int.MaxValue;
        return matches[^1].End - matches[0].Start;
    }
}
