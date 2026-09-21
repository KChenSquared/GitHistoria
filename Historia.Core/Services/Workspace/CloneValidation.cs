namespace Historia.Services.Workspace;

/// <summary>
/// Non-throwing, offline-only checks for the Clone dialog (F1, F3, F4a) — shared by the dialog
/// (live feedback as the user types) and <see cref="WorkspaceService.CloneAsync"/> (the
/// authoritative gate before <c>git clone</c> runs), so the two never disagree. This is a UX
/// nicety, not a security boundary: it never makes a network call (Rule 0.2), and the
/// leading-'-'/remote-helper/scheme checks it reuses from <see cref="WorkspaceService"/> remain
/// the real defence against option injection and arbitrary remote helpers.
/// </summary>
public static class CloneValidation
{
    // Schemes Historia will pass to `git clone`. Deliberately excludes any custom transport —
    // notably "ext::", a Git remote-helper syntax that runs an arbitrary shell command — and any
    // scheme this list doesn't recognise.
    internal static readonly HashSet<string> AllowedCloneSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "ssh", "git", "file" };

    // scp-like syntax: [user@]host:path (no scheme, a ':' after a host of >=2 chars, not "://").
    // The host must be at least 2 characters and contain none of `/ \ @ :`, so Windows drive paths
    // such as `C:\repo` and `C:foo` are never taken as scp (git treats a one-letter "host" as a drive on Windows).
    private static readonly Regex ScpLikeUrl =
        new(@"^(?:[^\s@/\\]+@)?[^\s@:/\\]{2,}:.+$", RegexOptions.Compiled);

    /// <summary>Rejects a pasted clone URL that would let Git parse it as something other than a
    /// repository address (a leading '-', a "&lt;transport&gt;::" remote-helper prefix, or an
    /// unsupported scheme), or that simply isn't shaped like a repository address at all.
    /// Returns <c>null</c> when the URL is acceptable, otherwise a short human-readable reason.</summary>
    public static string? DescribeUrlProblem(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "A clone URL is required.";

        var trimmed = url.Trim();

        if (trimmed.StartsWith('-'))
            return "The clone URL cannot start with '-'.";

        if (trimmed.Contains("::", StringComparison.Ordinal))
            return "This URL uses a Git remote-helper transport, which Historia does not allow for Clone.";

        var schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var scheme = trimmed[..schemeEnd];
            if (!AllowedCloneSchemes.Contains(scheme))
                return $"Unsupported clone URL scheme '{scheme}'.";
            var host = trimmed[(schemeEnd + 3)..].TrimStart('/');
            if (host.Length == 0 || host[0] is '/' or '\\')
                return "This doesn't look like a repository URL.";
            return null;
        }

        if (ScpLikeUrl.IsMatch(trimmed))
            return null;

        if (Directory.Exists(trimmed))
            return null;

        return "This doesn't look like a repository URL or an existing local path.";
    }

    /// <summary>Rejects a Root/Folder pair that would fail once combined and handed to
    /// <c>git clone</c> — an empty segment, a segment containing an invalid path character
    /// (verified live on this .NET runtime: <see cref="Path.GetInvalidPathChars"/> does NOT
    /// include '"' — only C0 control characters — but <see cref="Path.GetInvalidFileNameChars"/>
    /// does, and a live <c>Directory.CreateDirectory</c> call throws <see cref="IOException"/>
    /// for a '"' segment, confirming <see cref="HasInvalidSegmentChar"/> below is the check that
    /// actually matches what Windows rejects; this supersedes the plan's original
    /// <c>GetInvalidPathChars</c> assumption), or a destination that already exists and is not
    /// empty. Returns <c>null</c> when the pair is acceptable, otherwise a short human-readable
    /// reason.</summary>
    public static string? DescribeDestinationProblem(string root, string folder)
    {
        var trimmedRoot = (root ?? "").Trim();
        var trimmedFolder = (folder ?? "").Trim();

        if (trimmedRoot.Length == 0)
            return "A destination folder is required.";
        if (trimmedFolder.Length == 0)
            return "A folder name is required.";

        if (HasInvalidSegmentChar(trimmedRoot) || HasInvalidSegmentChar(trimmedFolder))
            return "The destination path contains a character that isn't allowed in a file path.";

        string target;
        try
        {
            target = Path.Combine(trimmedRoot, trimmedFolder);
        }
        catch (ArgumentException)
        {
            return "The destination path contains a character that isn't allowed in a file path.";
        }

        try
        {
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
                return "That destination already exists and is not empty.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return $"That destination can't be read: {ex.Message}";
        }

        return null;
    }

    /// <summary>True when any '\'/'/'-delimited segment of <paramref name="path"/> contains a
    /// character Windows rejects in a file/directory name (<see cref="Path.GetInvalidFileNameChars"/>
    /// — '"', '&lt;', '&gt;', '|', ':', '*', '?', plus the separators themselves, which is why
    /// this checks per-segment rather than the whole string). A two-character drive segment like
    /// "C:" is exempted since its ':' is a legitimate drive marker, not a name character.</summary>
    private static bool HasInvalidSegmentChar(string path)
    {
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var segment in path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Length == 2 && segment[1] == ':' && char.IsLetter(segment[0]))
                continue;
            if (segment.IndexOfAny(invalid) >= 0)
                return true;
        }
        return false;
    }
}
