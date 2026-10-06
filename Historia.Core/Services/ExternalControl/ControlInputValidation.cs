namespace Historia.Services.ExternalControl;

/// <summary>Validation of everything an external caller supplies. Pure functions, used on both
/// ends of the pipe so a hostile value is refused before it can reach a git argument list.</summary>
public static class ControlInputValidation
{
    public const int MaxRevisionLength = 256;
    public const int MaxCommitMessageBytes = 16 * 1024;

    /// <summary>A revision is passed to git as a single argument after <c>--end-of-options</c>;
    /// it must still never look like an option, span lines, or be unreasonably long.</summary>
    public static bool TryValidateRevision(string? rev, out string? error)
    {
        if (string.IsNullOrEmpty(rev))
        {
            error = "A revision is required.";
            return false;
        }

        if (rev.Length > MaxRevisionLength)
        {
            error = $"A revision is at most {MaxRevisionLength} characters.";
            return false;
        }

        if (rev[0] == '-')
        {
            error = "A revision cannot start with '-'.";
            return false;
        }

        foreach (var c in rev)
        {
            if (char.IsControl(c))
            {
                error = "A revision cannot contain control characters.";
                return false;
            }
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Resolves <paramref name="path"/> (against <paramref name="cwd"/> unless rooted) to a path
    /// relative to <paramref name="repoRoot"/>, with forward slashes. Rejects empty paths, NUL, and
    /// anything that escapes the root. The file need not exist, so deleted files resolve.
    /// </summary>
    public static bool TryResolveRepoRelativePath(string repoRoot, string cwd, string? path, out string relative)
    {
        relative = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || string.IsNullOrEmpty(repoRoot))
            return false;

        string full;
        string root;
        try
        {
            root = System.IO.Path.GetFullPath(repoRoot);
            full = System.IO.Path.GetFullPath(path, string.IsNullOrEmpty(cwd) ? root : System.IO.Path.GetFullPath(cwd));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var rootWithSeparator = System.IO.Path.TrimEndingDirectorySeparator(root) + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, comparison) || full.Length == rootWithSeparator.Length)
            return false;

        var candidate = full[rootWithSeparator.Length..]
            .Replace(System.IO.Path.DirectorySeparatorChar, '/')
            .Replace(System.IO.Path.AltDirectorySeparatorChar, '/');
        if (candidate.Length == 0)
            return false;

        relative = candidate;
        return true;
    }

    /// <summary>
    /// Normalizes a proposed commit message: drops one leading BOM, converts CRLF to LF, trims
    /// trailing whitespace on every line and at the end. Fails when the result is empty, holds a
    /// control character other than newline or tab, or exceeds <see cref="MaxCommitMessageBytes"/>.
    /// </summary>
    public static bool NormalizeCommitMessage(string? text, out string normalized, out string? error)
    {
        normalized = string.Empty;
        if (text is null)
        {
            error = "A commit message is required.";
            return false;
        }

        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd();

        var result = string.Join('\n', lines).TrimEnd();
        if (result.Length == 0)
        {
            error = "The commit message is empty.";
            return false;
        }

        foreach (var c in result)
        {
            if (char.IsControl(c) && c is not ('\n' or '\t'))
            {
                error = "The commit message contains a control character.";
                return false;
            }
        }

        if (Encoding.UTF8.GetByteCount(result) > MaxCommitMessageBytes)
        {
            error = $"The commit message is longer than {MaxCommitMessageBytes / 1024} KiB.";
            return false;
        }

        normalized = result;
        error = null;
        return true;
    }
}
