namespace Historia.Services.Ai;

/// <summary>
/// Picks the model to use when the saved Gemini model is gone. Gemini's model list carries no
/// release date, so "newest" is ranked from the version in the id (<c>gemini-2.5-flash</c>).
/// Pure; the ranking is pinned by tests.
/// </summary>
public static class GeminiModelSelector
{
    private static readonly string[] SpecialisedTokens =
        ["tts", "image", "audio", "live", "embedding", "aqa", "robotics"];

    private static readonly Regex Version = new(@"^gemini-(\d+)(?:\.(\d+))?(?=-|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Tiers, first non-empty wins: stable Flash, preview Flash, stable Flash-Lite, preview
    /// Flash-Lite, any stable model, any preview model, then specialised models. Within a tier
    /// the highest version wins; ties go to the plain alias (fewest id segments), then the larger id.
    /// Ids with no parseable version rank below every versioned id in their tier.
    /// <paramref name="excluding"/> is never returned. Null when nothing qualifies.
    /// </summary>
    public static string? PickReplacement(IReadOnlyList<string> ids, string? excluding)
    {
        var excluded = excluding is null ? null : Strip(excluding);
        string? best = null;
        Rank bestRank = default;

        foreach (var raw in ids)
        {
            var id = Strip(raw);
            if (id.Length == 0 || string.Equals(id, excluded, StringComparison.OrdinalIgnoreCase))
                continue;

            var rank = RankOf(id);
            if (best is null || rank.IsBetterThan(bestRank))
            {
                best = id;
                bestRank = rank;
            }
        }
        return best;
    }

    private static string Strip(string id)
    {
        id = id.Trim();
        return id.StartsWith("models/", StringComparison.Ordinal) ? id["models/".Length..] : id;
    }

    private static Rank RankOf(string id)
    {
        var segments = id.Split('-');
        var set = new HashSet<string>(segments, StringComparer.OrdinalIgnoreCase);

        var specialised = SpecialisedTokens.Any(set.Contains)
            || id.Contains("computer-use", StringComparison.OrdinalIgnoreCase);
        var preview = set.Contains("preview") || set.Contains("exp") || set.Contains("experimental");
        var flash = set.Contains("flash");
        var lite = set.Contains("lite");

        int tier;
        if (specialised) tier = 7;
        else if (flash && !lite) tier = preview ? 2 : 1;
        else if (flash) tier = preview ? 4 : 3;
        else tier = preview ? 6 : 5;

        var (major, minor) = (-1, -1);
        var m = Version.Match(id);
        if (m.Success)
        {
            major = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            minor = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        }
        return new Rank(id, tier, major, minor, segments.Length);
    }

    private readonly record struct Rank(string Id, int Tier, int Major, int Minor, int Segments)
    {
        public bool IsBetterThan(Rank other)
        {
            if (Tier != other.Tier) return Tier < other.Tier;
            if (Major != other.Major) return Major > other.Major;
            if (Minor != other.Minor) return Minor > other.Minor;
            if (Segments != other.Segments) return Segments < other.Segments;
            return string.CompareOrdinal(Id, other.Id) > 0;
        }
    }
}
