namespace Historia.Services.Git;

/// <summary>
/// Parses <c>git --progress</c> stderr lines into <see cref="GitProgress"/> (§6.6.2), e.g.
/// <c>Receiving objects:  47% (4700/10000), 1.20 MiB | 800.00 KiB/s</c>.
/// </summary>
public static partial class GitProgressParser
{
    public static bool TryParse(string line, out GitProgress progress)
    {
        progress = null!;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var match = ProgressLineRegex().Match(line);
        if (!match.Success)
            return false;

        var phase = match.Groups["phase"].Value.Trim();
        double? percent = match.Groups["pct"].Success
            ? double.Parse(match.Groups["pct"].Value, CultureInfo.InvariantCulture)
            : null;
        long? done = match.Groups["done"].Success ? long.Parse(match.Groups["done"].Value, CultureInfo.InvariantCulture) : null;
        long? total = match.Groups["total"].Success ? long.Parse(match.Groups["total"].Value, CultureInfo.InvariantCulture) : null;
        double? bps = match.Groups["rate"].Success
            ? ParseRate(match.Groups["rate"].Value, match.Groups["rateUnit"].Value)
            : null;

        progress = new GitProgress
        {
            Phase = phase,
            Percent = percent,
            ObjectsDone = done,
            ObjectsTotal = total,
            BytesPerSecond = bps,
            RawLine = line,
        };
        return true;
    }

    private static double ParseRate(string value, string unit)
    {
        var n = double.Parse(value, CultureInfo.InvariantCulture);
        return unit.ToLowerInvariant() switch
        {
            "gib" => n * 1024 * 1024 * 1024,
            "mib" => n * 1024 * 1024,
            "kib" => n * 1024,
            _ => n,
        };
    }

    [GeneratedRegex(
        @"^(?<phase>[A-Za-z ]+):\s+(?<pct>\d+)%\s+\((?<done>\d+)/(?<total>\d+)\)(?:.*?\|\s*(?<rate>[\d.]+)\s*(?<rateUnit>[KMG]iB|B)/s)?",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProgressLineRegex();
}
