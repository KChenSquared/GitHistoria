namespace Historia.Layout;

/// <summary>
/// Pure column layout maths for the shell's three body panels (sidebar, commit graph, detail pane).
/// </summary>
public static class BodyColumnLayout
{
    /// <summary>
    /// Computes the effective minimum pane width taking the shortcuts bar's natural width into account.
    /// </summary>
    public static double EffectiveMin(double baseMin, double barNaturalWidth) =>
        Math.Max(baseMin, Math.Ceiling(barNaturalWidth));

    /// <summary>
    /// Resolves the width of the Left, Centre, and Right columns given the total content width,
    /// stored user intents, and effective pane minimums. Floors both side panes at their minimums
    /// (left wins over right when even that doesn't fit) and gives way centre-first.
    /// </summary>
    public static (double Left, double Centre, double Right) Resolve(
        double total,
        double leftIntent,
        double rightIntent,
        double leftMin,
        double rightMin)
    {
        if (total <= 0)
            return (0, 0, 0);

        var lIntent = Math.Max(leftMin, leftIntent);
        var rIntent = Math.Max(rightMin, rightIntent);

        if (total >= lIntent + rIntent)
        {
            var left = lIntent;
            var right = rIntent;
            var centre = total - left - right;
            return (left, centre, right);
        }

        // Narrow space: centre collapses first.
        if (total >= leftMin + rightMin)
        {
            // Both sides remain >= their minimums.
            var left = Math.Clamp(total - rightMin, leftMin, lIntent);
            var right = total - left;
            return (left, 0, right);
        }

        // Extremely narrow: total below leftMin + rightMin.
        // Left is kept up to total; right takes whatever space remains.
        {
            var left = Math.Min(leftMin, total);
            var right = Math.Max(0, total - left);
            return (left, 0, right);
        }
    }
}
