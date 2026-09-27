namespace Historia.Services.Graph;

/// <summary>
/// The graph's threshold-stepped silent crawl state: bounds the background crawler
/// to discrete steps as the user scrolls, rather than pre-loading an arbitrarily deep layout
/// all at once. Pure state, unit-tested, bound and ticked by <c>CommitGraphControl</c> on the
/// UI thread only.
/// </summary>
public sealed class GraphCrawlThreshold(int step, int ceiling)
{
    public int Step => step;
    public int Ceiling => ceiling;

    /// <summary>The current row target the background crawler is allowed to load up to.</summary>
    public int Target { get; private set; } = step <= 0 ? ceiling : Math.Min(step, ceiling);

    /// <summary>
    /// Advance the target when the viewport's last visible row <paramref name="lastVisibleRow"/>
    /// reaches or passes <see cref="Target"/>. Returns <c>true</c> only
    /// when <see cref="Target"/> moved; always <c>false</c> when uncapped (<paramref name="step"/> ≤ 0)
    /// or when already at <paramref name="ceiling"/>.
    /// </summary>
    public bool Advance(int lastVisibleRow)
    {
        if (step <= 0 || Target >= ceiling)
            return false;
        if (lastVisibleRow < Target)
            return false;

        var next = Math.Min(ceiling, ((lastVisibleRow / step) + 1) * step);
        if (next <= Target)
            return false;

        Target = next;
        return true;
    }
}
