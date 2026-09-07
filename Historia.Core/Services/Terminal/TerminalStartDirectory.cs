namespace Historia.Services.Terminal;

/// <summary>
/// Resolves a session's start directory with its fallback ladder (V5 §4.2). Pure and
/// unit-tested.
/// <para>
/// Paths crossing into the pty are <b>native</b> paths. The forward-slash rule (comprehensive
/// doc §16) governs repo-relative paths in Historia's own models; a <c>chdir</c> target is a
/// filesystem boundary, which is exactly where that rule says conversion happens.
/// </para>
/// </summary>
public static class TerminalStartDirectory
{
    /// <summary>
    /// Ladder: <paramref name="preferred"/> → <paramref name="homeDirectory"/> →
    /// <paramref name="currentDirectory"/> → <c>null</c> (let the shell choose). Never throws;
    /// a missing directory is reported in the session's first line, not as a modal.
    /// </summary>
    public static string? Resolve(
        string? preferred,
        string homeDirectory,
        string currentDirectory,
        Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);

        if (Usable(preferred, directoryExists)) return preferred;
        if (Usable(homeDirectory, directoryExists)) return homeDirectory;
        if (Usable(currentDirectory, directoryExists)) return currentDirectory;
        return null;
    }

    /// <summary>
    /// Repository working directory for a terminal origin: the working directory for a normal
    /// repo or a linked worktree, and the repository path itself for a <b>bare</b> repo (which
    /// has none).
    /// </summary>
    public static string ForRepository(string? workingDirectory, string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        return string.IsNullOrWhiteSpace(workingDirectory)
            ? repositoryPath
            : workingDirectory;
    }

    private static bool Usable(string? path, Func<string, bool> directoryExists)
        => !string.IsNullOrWhiteSpace(path) && directoryExists(path);
}
