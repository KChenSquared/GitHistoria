namespace Historia.Services.CommandLine;

/// <summary>
/// A proposed change to the user PATH environment variable.
/// </summary>
/// <param name="Original">The raw PATH value the change was planned from, or <c>null</c> when it was missing.</param>
/// <param name="NewValue">The rewritten PATH value.</param>
/// <param name="Added">The folder added, or <c>null</c> if our folder was already in place.</param>
/// <param name="Removed">The raw text of every removed entry.</param>
/// <param name="IsNoOp">True when the proposed rewrite does not alter effective entries.</param>
/// <param name="TooLong">True when <see cref="NewValue"/> exceeds 32,767 characters.</param>
public sealed record PathChange(
    string? Original,
    string NewValue,
    string? Added,
    IReadOnlyList<string> Removed,
    bool IsNoOp,
    bool TooLong = false);

/// <summary>
/// Pure operations for parsing, comparing and rewriting PATH segments.
/// </summary>
public static class UserPathEditor
{
    private const int MaxPathLength = 32767;

    /// <summary>
    /// Splits raw PATH string into entries in order, keeping each entry's raw text intact.
    /// Empty segments from repeated semicolons or trailing semicolons are excluded.
    /// </summary>
    public static IReadOnlyList<string> Split(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return [];

        var list = new List<string>();
        var parts = raw.Split(';');
        foreach (var part in parts)
        {
            if (part.Length > 0)
                list.Add(part);
        }
        return list;
    }

    /// <summary>
    /// Compares two folder paths for equivalence by expanding environment variables,
    /// trimming whitespace and surrounding quotes, obtaining full paths, trimming trailing slashes,
    /// and comparing case-insensitively. Entries that fail normalisation never compare equal.
    /// </summary>
    public static bool SameFolder(string a, string b)
    {
        var normA = NormalizeFolder(a);
        var normB = NormalizeFolder(b);
        if (normA is null || normB is null)
            return false;

        return string.Equals(normA, normB, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Plans a replacement of cmdr entries on PATH: the first entry holding cmdr or pointing to our folder
    /// is replaced with <paramref name="ourFolder"/> (preserving its raw text if it is already our folder);
    /// subsequent cmdr entries and duplicates of our folder are removed; when no cmdr entry exists,
    /// <paramref name="ourFolder"/> is appended. Unrelated entries are preserved verbatim.
    /// </summary>
    public static PathChange PlanReplace(string? raw, string ourFolder, Func<string, bool> holdsCmdr)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ourFolder);
        ArgumentNullException.ThrowIfNull(holdsCmdr);

        var entries = Split(raw);
        var newEntries = new List<string>(entries.Count + 1);
        var removed = new List<string>();
        string? added = null;
        bool foundFirst = false;

        foreach (var entry in entries)
        {
            bool isOur = SameFolder(entry, ourFolder);
            bool isCmdr = isOur || SafeHoldsCmdr(entry, holdsCmdr);

            if (isCmdr)
            {
                if (!foundFirst)
                {
                    foundFirst = true;
                    if (!isOur)
                    {
                        added = ourFolder;
                        removed.Add(entry);
                    }
                    newEntries.Add(isOur ? entry : ourFolder);
                }
                else
                {
                    removed.Add(entry);
                }
            }
            else
            {
                newEntries.Add(entry);
            }
        }

        if (!foundFirst)
        {
            added = ourFolder;
            newEntries.Add(ourFolder);
        }

        var newValue = string.Join(';', newEntries);
        bool isNoOp = added is null && removed.Count == 0;
        bool tooLong = newValue.Length > MaxPathLength;

        return new PathChange(raw, newValue, added, removed, isNoOp, tooLong);
    }

    /// <summary>
    /// Expands environment variables, trims whitespace, strips one pair of surrounding quotes,
    /// and ensures the resulting path is fully qualified. Returns <c>null</c> if the result is empty,
    /// relative, or on any exception.
    /// </summary>
    public static string? ExpandEntry(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return null;

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(entry).Trim();
            if (expanded.StartsWith('"') && expanded.EndsWith('"') && expanded.Length >= 2)
                expanded = expanded[1..^1].Trim();

            if (expanded.Length == 0 || !Path.IsPathFullyQualified(expanded))
                return null;

            return expanded;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeFolder(string? path)
    {
        var expanded = ExpandEntry(path);
        if (expanded is null)
            return null;

        try
        {
            var full = Path.GetFullPath(expanded);
            return full.TrimEnd('\\', '/');
        }
        catch
        {
            return null;
        }
    }

    private static bool SafeHoldsCmdr(string entry, Func<string, bool> holdsCmdr)
    {
        var expanded = ExpandEntry(entry);
        if (expanded is null)
            return false;

        try
        {
            return holdsCmdr(expanded);
        }
        catch
        {
            return false;
        }
    }
}
