using System.ComponentModel;

namespace Historia.Services.Terminal;

/// <summary>Start-up / runtime failure classes for the terminal subsystem (V5 §9.5). Mirrors
/// <see cref="Historia.Services.Git.GitErrorKind"/> in spirit: there is no
/// <c>WebViewRuntimeMissing</c> — that failure class does not exist in this design.</summary>
public enum TerminalErrorKind
{
    None,
    ShellNotFound,
    PtyUnavailable,
    WorkingDirectoryMissing,
    PermissionDenied,
    Unknown,
}

/// <summary>
/// Thrown when a terminal session cannot start (V5 §9.5). <see cref="Kind"/> drives the
/// actionable message; <see cref="Exception.Message"/> carries the offending detail (e.g. the
/// configured shell path for <see cref="TerminalErrorKind.ShellNotFound"/>).
/// </summary>
public sealed class TerminalStartException(TerminalErrorKind kind, string detail)
    : Exception(detail)
{
    public TerminalErrorKind Kind { get; } = kind;
}

/// <summary>
/// Maps a start-up failure to an actionable message (V5 §9.5). Mirrors
/// <c>GitErrorClassifier</c>: <see cref="TerminalErrorKind.Unknown"/> surfaces the underlying
/// text behind a Details expander — never a silent fallback (constitution rule 11).
/// </summary>
public static class TerminalErrorClassifier
{
    public static (TerminalErrorKind Kind, string Message, string? Details) Classify(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        switch (ex)
        {
            case TerminalStartException tse:
                return (tse.Kind, MessageFor(tse.Kind, tse.Message), tse.Kind == TerminalErrorKind.Unknown ? tse.ToString() : null);

            case UnauthorizedAccessException:
                return (TerminalErrorKind.PermissionDenied,
                    "Historia is not allowed to start a shell here. Check the folder's permissions and try again.",
                    ex.ToString());

            case DirectoryNotFoundException:
                return (TerminalErrorKind.WorkingDirectoryMissing,
                    "The terminal's start directory no longer exists. Historia started the shell in your home directory instead.",
                    ex.ToString());

            case FileNotFoundException:
                return (TerminalErrorKind.ShellNotFound,
                    "The shell could not be found. Set a shell path in Settings → Terminal.",
                    ex.ToString());

            // ConPTY needs Windows 10 1809+ — the "supported but not baseline" case (rule 0.5).
            case Win32Exception w32:
                return (TerminalErrorKind.PtyUnavailable,
                    "This system cannot host an embedded terminal (a pseudoterminal could not be created).",
                    w32.ToString());

            case PlatformNotSupportedException:
                return (TerminalErrorKind.PtyUnavailable,
                    "This platform does not support an embedded terminal.",
                    ex.ToString());

            default:
                return (TerminalErrorKind.Unknown, "The terminal failed to start.", ex.ToString());
        }
    }

    private static string MessageFor(TerminalErrorKind kind, string detail) => kind switch
    {
        TerminalErrorKind.ShellNotFound =>
            $"The configured shell was not found: {detail}. Fix the path in Settings → Terminal, or clear it to use the platform default.",
        TerminalErrorKind.PtyUnavailable =>
            "This system cannot host an embedded terminal (a pseudoterminal could not be created).",
        TerminalErrorKind.WorkingDirectoryMissing =>
            $"The terminal's start directory could not be used: {detail}.",
        TerminalErrorKind.PermissionDenied =>
            $"Permission denied starting the shell: {detail}.",
        _ => string.IsNullOrWhiteSpace(detail) ? "The terminal failed to start." : detail,
    };
}
