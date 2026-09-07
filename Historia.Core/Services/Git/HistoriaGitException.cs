
namespace Historia.Services.Git;

/// <summary>
/// A Git operation failure already classified into a <see cref="GitErrorKind"/> (§6.5.3), with
/// an optional verbatim detail string for a "Details" expander. Higher layers map
/// <see cref="Kind"/> to an actionable message; raw stderr is never surfaced directly.
/// </summary>
public sealed class HistoriaGitException(GitErrorKind kind, string message, string? details = null)
    : Exception(message)
{
    public GitErrorKind Kind { get; } = kind;
    public string? Details { get; } = details;

    /// <summary>
    /// §13.1: set when a user-initiated fetch/push/pull failed on every transport while a
    /// non-<i>Local config</i> profile was the active selection. The app layer offers to switch
    /// the repository to <i>Local config</i> and retry once.
    /// </summary>
    public bool FallbackToLocalConfigAvailable { get; init; }

    public static HistoriaGitException FromCli(Models.GitCliResult result, string operation) =>
        new(result.ErrorKind,
            $"{operation} failed ({result.ErrorKind}).",
            string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
}

/// <summary>Shared by every error-notification call site so none of them has to remember the
/// HistoriaGitException special case itself.</summary>
public static class ExceptionNotificationExtensions
{
    /// <summary>The right "Details" text for an error toast: an <see cref="HistoriaGitException"/>'s
    /// verbatim git stderr when it has one, else the .NET stack trace as a last resort for
    /// anything unexpected. Using <c>ex.ToString()</c> directly here is the recurring bug — it
    /// silently throws away the actual git error message (e.g. "refusing to merge unrelated
    /// histories") behind an opaque stack trace.</summary>
    public static string? NotificationDetails(this Exception ex) =>
        ex is HistoriaGitException git ? git.Details : ex.ToString();
}
