namespace Historia.Services.Terminal;

/// <summary>Which OS family a shell chain is being resolved for.</summary>
public enum OSPlatformKind
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>A resolved shell: an absolute executable path plus its argument vector.</summary>
public sealed record ResolvedShell(string Path, IReadOnlyList<string> Arguments);

/// <summary>
/// Resolves which shell to launch (V5 §4.1). Pure and unit-tested: the filesystem and
/// environment are injected so every platform chain is testable from any platform.
/// <para>
/// Order, first hit wins:
/// <list type="number">
///   <item><c>configuredPath</c> if non-empty <b>and</b> the file exists. A configured-but-missing
///   shell throws <see cref="TerminalStartException"/>(<see cref="TerminalErrorKind.ShellNotFound"/>) —
///   it never falls through to the platform chain (constitution rule 11: no invented fallback
///   for a failure).</item>
///   <item>the platform default chain.</item>
/// </list>
/// Login-shell flags (<c>-l</c>) are deliberately <b>not</b> defaulted on — they re-run the
/// user's full login profile inside a child of a GUI app and duplicate <c>PATH</c> entries.
/// </para>
/// </summary>
public static class ShellResolver
{
    public static ResolvedShell Resolve(
        string? configuredPath,
        IReadOnlyList<string> configuredArgs,
        OSPlatformKind platform,
        Func<string, bool> fileExists,
        Func<string, string?> getEnvironmentVariable,
        Func<string, string?> findOnPath,
        Func<string?> getPasswdShell)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(findOnPath);
        ArgumentNullException.ThrowIfNull(getPasswdShell);

        var args = configuredArgs ?? [];

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (fileExists(configuredPath))
                return new ResolvedShell(configuredPath, [.. args]);

            throw new TerminalStartException(TerminalErrorKind.ShellNotFound, configuredPath);
        }

        var resolved = platform switch
        {
            OSPlatformKind.Windows => ResolveWindows(fileExists, getEnvironmentVariable, findOnPath),
            OSPlatformKind.MacOS => ResolveMac(fileExists, getEnvironmentVariable),
            OSPlatformKind.Linux => ResolveLinux(fileExists, getEnvironmentVariable, getPasswdShell),
            _ => null,
        };

        if (resolved is null)
            throw new TerminalStartException(TerminalErrorKind.ShellNotFound, "no shell found on the platform default chain");

        return new ResolvedShell(resolved, [.. args]);
    }

    private static string? ResolveWindows(
        Func<string, bool> fileExists,
        Func<string, string?> getEnv,
        Func<string, string?> findOnPath)
    {
        var pwsh = findOnPath("pwsh.exe");
        if (!string.IsNullOrWhiteSpace(pwsh) && fileExists(pwsh))
            return pwsh;

        var systemRoot = getEnv("SystemRoot");
        if (!string.IsNullOrWhiteSpace(systemRoot))
        {
            var windowsPowerShell = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            if (fileExists(windowsPowerShell))
                return windowsPowerShell;
        }

        var comSpec = getEnv("ComSpec");
        if (!string.IsNullOrWhiteSpace(comSpec) && fileExists(comSpec))
            return comSpec;

        return fileExists(@"C:\Windows\System32\cmd.exe") ? @"C:\Windows\System32\cmd.exe" : null;
    }

    private static string? ResolveMac(Func<string, bool> fileExists, Func<string, string?> getEnv)
        => FirstExisting(fileExists, getEnv("SHELL"), "/bin/zsh", "/bin/sh");

    private static string? ResolveLinux(
        Func<string, bool> fileExists,
        Func<string, string?> getEnv,
        Func<string?> getPasswdShell)
        => FirstExisting(fileExists, getEnv("SHELL"), getPasswdShell(), "/bin/bash", "/bin/sh");

    private static string? FirstExisting(Func<string, bool> fileExists, params string?[] candidates)
    {
        foreach (var c in candidates)
            if (!string.IsNullOrWhiteSpace(c) && fileExists(c))
                return c;
        return null;
    }
}
