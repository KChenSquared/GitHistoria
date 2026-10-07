namespace Historia.Services.Ai;

/// <summary>
/// The Extra Headers field: one <c>Name: Value</c> per line. Lines without a colon, or with an
/// empty name or value, are skipped. A header can never replace the authentication header.
/// Adapted from SourceGit (MIT).
/// </summary>
public static class AiExtraHeaders
{
    private static readonly string[] Reserved = ["Authorization", "x-goog-api-key"];

    public static IReadOnlyList<(string Name, string Value)> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var result = new List<(string, string)>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf(':');
            if (idx < 0)
                continue;
            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (name.Length == 0 || value.Length == 0)
                continue;
            if (Reserved.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add((name, value));
        }
        return result;
    }

    public static void Apply(HttpRequestMessage message, string? text)
    {
        foreach (var (name, value) in Parse(text))
            message.Headers.TryAddWithoutValidation(name, value);
    }

    /// <summary>The header values, so error details can have them redacted.</summary>
    public static IEnumerable<string> Values(string? text) => Parse(text).Select(h => h.Value);
}
