namespace Historia.Services.Git;

/// <summary>
/// Turns a git invocation's stderr into something a person can read. With <c>--progress</c>
/// git interleaves <c>\r</c>-overwritten progress meters (its own and the server's, prefixed
/// <c>remote: </c>) with the real diagnostics, so the first line is usually
/// <c>Enumerating objects: 244, done.</c> and the actual error sits near the end.
/// </summary>
public static class GitStderr
{
    private const string RemotePrefix = "remote: ";

    /// <summary>Progress titles git and its server-side counterparts print (pack-objects,
    /// index-pack, unpack-objects, send-pack).</summary>
    private static readonly string[] ProgressPrefixes =
    [
        "Enumerating objects",
        "Counting objects",
        "Compressing objects",
        "Writing objects",
        "Receiving objects",
        "Unpacking objects",
        "Resolving deltas",
        "Total ",
        "Delta compression",
        "Path-based delta compression",
    ];

    /// <summary>True for a line of git progress output, with or without the server's
    /// <c>remote: </c> prefix, including a line still holding <c>\r</c>-overwritten fragments
    /// when every fragment is progress.</summary>
    public static bool IsProgressLine(string line)
    {
        if (line.Contains('\r'))
        {
            var fragments = line.Split('\r', StringSplitOptions.RemoveEmptyEntries);
            return fragments.Length > 0 && fragments.All(f => string.IsNullOrWhiteSpace(f) || IsProgressLine(f));
        }

        var text = line.TrimStart();
        if (text.StartsWith(RemotePrefix, StringComparison.Ordinal))
            text = text[RemotePrefix.Length..].TrimStart();

        foreach (var prefix in ProgressPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>The stderr with progress and blank lines removed, one line per <c>\n</c>.</summary>
    public static string Clean(string? stderr) =>
        string.Join('\n', Lines(stderr).Where(l => !IsProgressLine(l)).Select(l => l.TrimEnd()));

    /// <summary>The server's own <c>remote: </c> messages, prefix stripped and progress dropped —
    /// <c>remote: error:</c> lines first, the rest in their original order.</summary>
    public static IReadOnlyList<string> RemoteMessages(string? stderr)
    {
        var messages = Lines(stderr)
            .Where(l => l.StartsWith(RemotePrefix, StringComparison.Ordinal) && !IsProgressLine(l))
            .Select(l => l[RemotePrefix.Length..].TrimEnd())
            .Where(l => l.Length > 0)
            .ToList();
        return
        [
            .. messages.Where(IsErrorLine),
            .. messages.Where(m => !IsErrorLine(m)),
        ];
    }

    /// <summary>The single most diagnostic line: the first <c>remote: error:</c> line, else the
    /// last <c>fatal:</c> / <c>error:</c> / <c> ! [</c> line, else the first non-progress line,
    /// else empty.</summary>
    public static string SummaryLine(string? stderr)
    {
        var lines = Lines(stderr).Where(l => !IsProgressLine(l)).ToList();

        var remoteError = lines.FirstOrDefault(l =>
            l.StartsWith(RemotePrefix, StringComparison.Ordinal) && IsErrorLine(l[RemotePrefix.Length..]));
        if (remoteError is not null)
            return remoteError.TrimEnd();

        var diagnostic = lines.LastOrDefault(l =>
            l.StartsWith("fatal:", StringComparison.Ordinal)
            || l.StartsWith("error:", StringComparison.Ordinal)
            || l.StartsWith(" ! [", StringComparison.Ordinal));
        if (diagnostic is not null)
            return diagnostic.TrimEnd();

        return lines.FirstOrDefault()?.TrimEnd() ?? "";
    }

    private static bool IsErrorLine(string message) =>
        message.TrimStart().StartsWith("error:", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Lines(string? stderr) =>
        string.IsNullOrEmpty(stderr)
            ? []
            : stderr.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.Trim() != "remote:");
}
