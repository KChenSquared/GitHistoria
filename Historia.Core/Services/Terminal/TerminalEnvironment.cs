namespace Historia.Services.Terminal;

/// <summary>
/// Builds the child process environment for a terminal session.
/// <para>
/// Historia never sets its internal variables process-globally — <c>GitCliService</c> composes
/// them on each child <c>ProcessStartInfo</c> per invocation, and there is no
/// <c>Environment.SetEnvironmentVariable</c> anywhere in the codebase.
/// So anything found here under a Git-owned name came from <b>the user's own shell</b>, and a
/// blanket name-based scrub would silently break the user's configuration inside their own
/// terminal. This type is therefore <b>defence in depth</b> with value-aware rules, not a patch
/// on a live leak.
/// </para>
/// </summary>
public static class TerminalEnvironment
{
    /// <summary>
    /// Removed unconditionally: Historia-internal, meaningless to a user shell, and in two cases a
    /// secret or a capability token. Public so the test asserts the list by literal name — the
    /// list itself is the guarantee.
    /// </summary>
    public static readonly IReadOnlyList<string> AlwaysRemoved =
    [
        "HISTORIA_ASKPASS",
        "HISTORIA_ASKPASS_USERNAME",
        "HISTORIA_ASKPASS_PASSWORD",
        "HISTORIA_ASKPASS_HOST",
        "HISTORIA_REBASE_DIR",
        "HISTORIA_REBASE_TOKEN",
        "HISTORIA_SSH_KEY",
        "HISTORIA_PERF",
    ];

    /// <summary>
    /// Removed <b>only</b> when the value points at Historia's own executable. A value pointing
    /// anywhere else is the user's own setting and must survive.
    /// </summary>
    public static readonly IReadOnlyList<string> RemovedWhenHistoria =
    [
        "GIT_ASKPASS",
        "SSH_ASKPASS",
        "GIT_SEQUENCE_EDITOR",
        "GIT_EDITOR",
    ];

    public static IReadOnlyDictionary<string, string> Build(
        IReadOnlyDictionary<string, string> inherited,
        string? sshKeyPath,
        string historiaExecutablePath,
        string historiaVersion)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        ArgumentNullException.ThrowIfNull(historiaExecutablePath);

        var caseInsensitive = OperatingSystem.IsWindows();
        var nameComparer = caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        var result = new Dictionary<string, string>(nameComparer);
        var always = new HashSet<string>(AlwaysRemoved, nameComparer);
        var whenHistoria = new HashSet<string>(RemovedWhenHistoria, nameComparer);

        foreach (var (key, value) in inherited)
        {
            if (always.Contains(key))
                continue;

            if (whenHistoria.Contains(key) && PointsAtHistoria(value, historiaExecutablePath, caseInsensitive))
                continue;

            // GIT_TERMINAL_PROMPT and everything else: left exactly as inherited.
            result[key] = value;
        }

        if (!string.IsNullOrWhiteSpace(sshKeyPath))
        {
            // Trap: these are a PAIR. GIT_SSH_COMMAND references $HISTORIA_SSH_KEY, so
            // setting one without the other expands to `ssh -i "" …` and breaks every SSH op.
            result["HISTORIA_SSH_KEY"] = sshKeyPath;
            result["GIT_SSH_COMMAND"] = "ssh -i \"$HISTORIA_SSH_KEY\" -o IdentitiesOnly=yes";
        }

        result["TERM"] = "xterm-256color";
        result["COLORTERM"] = "truecolor";
        result["TERM_PROGRAM"] = "Historia";
        result["TERM_PROGRAM_VERSION"] = historiaVersion ?? "";

        return result;
    }

    /// <summary>
    /// True when <paramref name="value"/> — which may be a bare path or a quoted command line
    /// with arguments — has Historia's own executable as its program.
    /// </summary>
    private static bool PointsAtHistoria(string value, string historiaExecutablePath, bool caseInsensitive)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var cmp = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var trimmed = value.Trim();

        // Unquoted "<Historia path with spaces> --rebase-seq" — the program can't be tokenised on
        // whitespace, so match the value against the known executable path directly.
        if (trimmed.StartsWith(historiaExecutablePath, cmp)
            || trimmed.StartsWith('"' + historiaExecutablePath + '"', cmp))
            return true;

        var program = ExtractProgram(trimmed);
        if (string.Equals(program, historiaExecutablePath, cmp))
            return true;

        try
        {
            var programName = Path.GetFileName(program);
            return !string.IsNullOrEmpty(programName)
                   && string.Equals(programName, Path.GetFileName(historiaExecutablePath), cmp);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string ExtractProgram(string commandLine)
    {
        var s = commandLine.Trim();
        if (s.Length == 0) return s;

        if (s[0] is '"' or '\'')
        {
            var quote = s[0];
            var end = s.IndexOf(quote, 1);
            return end > 0 ? s[1..end] : s[1..];
        }

        var space = s.IndexOf(' ');
        return space > 0 ? s[..space] : s;
    }
}
