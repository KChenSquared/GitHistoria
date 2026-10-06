namespace Historia.Services.UI;

/// <summary>
/// The remedy offered when a git operation failed because a lock file is in the way and the
/// repository holds lock files old enough to be leftovers of a git process that died: a
/// "Remove stale lock files…" action on the failure card, which lists the files in a destructive
/// confirmation before anything is deleted.
/// </summary>
internal static class StaleLockPrompt
{
    private const int ListedFiles = 10;

    /// <summary>The card actions for <paramref name="failure"/> on <paramref name="repo"/>: the
    /// remedy when the failure is a lock conflict and stale locks exist, otherwise none.</summary>
    public static IReadOnlyList<NotificationAction> ActionsFor(
        RepositoryContext repo, Exception failure, IDialogService dialogs, INotificationService notifications)
    {
        if (failure is not HistoriaGitException git || !MayBeLockFailure(git))
            return [];
        if (RepoLockFiles.Find(repo, DateTime.UtcNow).Count == 0)
            return [];
        return [new NotificationAction("Remove stale lock files…", () => OfferRemovalAsync(repo, dialogs, notifications))];
    }

    /// <summary>
    /// True when a lock file could be what stopped <paramref name="failure"/>: git said another
    /// process holds a lock, or a fetch's batched ref update failed with "reference already
    /// exists" — what git prints for a remote-tracking ref whose <c>.lock</c> is already on disk.
    /// That second message has other causes too, so it only counts alongside stale lock files
    /// actually present in the repository.
    /// </summary>
    internal static bool MayBeLockFailure(HistoriaGitException failure) =>
        failure.Kind == GitErrorKind.LockedIndex
        || (failure.Details?.Contains("failed: reference already exists", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Lists the stale lock files, asks for a destructive confirmation, and removes the
    /// ones that are still stale.</summary>
    public static async Task OfferRemovalAsync(RepositoryContext repo, IDialogService dialogs, INotificationService notifications)
    {
        var found = RepoLockFiles.Find(repo, DateTime.UtcNow);
        if (found.Count == 0)
        {
            notifications.Publish(NotificationSeverity.Info, $"No stale lock files left in {repo.DisplayName}.");
            return;
        }

        if (!await dialogs.ConfirmDestructiveAsync("Remove stale lock files — destructive", Effect(repo, found), "Remove"))
        {
            AppLog.Write(AppLogLevel.Info, "declined", $"Stale lock removal in {repo.DisplayName} declined");
            return;
        }

        var result = await RepoLockFiles.RemoveStaleLocksAsync(repo, found, repo.Lifetime);
        if (result.Busy)
        {
            var what = result.BusyWith?.Label ?? "Another git operation";
            notifications.Publish(NotificationSeverity.Info,
                $"{what} on {repo.DisplayName} is running — try again when it finishes.");
            return;
        }

        var removed = $"Removed {Plural(result.Removed, "stale lock file")} from {repo.DisplayName}.";
        notifications.Publish(NotificationSeverity.Success,
            result.Skipped == 0 ? removed : $"{removed} {Plural(result.Skipped, "file")} changed or vanished and {(result.Skipped == 1 ? "was" : "were")} left alone.");
    }

    /// <summary>The confirmation text: how many files, where, how old, which ones, and the one risk
    /// — a lock still held by a git command that is running right now.</summary>
    internal static string Effect(RepositoryContext repo, IReadOnlyList<StaleLockFile> found)
    {
        var oldest = DateTime.UtcNow - found.Min(f => f.LastWriteUtc);
        var listed = string.Join("\n", found.Take(ListedFiles).Select(f => "  " + f.RelativePath));
        var more = found.Count > ListedFiles ? $"\n  … and {found.Count - ListedFiles} more" : "";
        return $"This deletes {Plural(found.Count, "lock file")} from {repo.DisplayName}'s .git folder, "
            + $"the oldest {Age(oldest)} old:\n\n{listed}{more}\n\n"
            + "Git leaves these behind when one of its processes stops in the middle of an update; until they are "
            + "gone, fetches and other operations that need them fail. If a git command is still running on this "
            + "repository — in a terminal, an editor or another tool — deleting its lock can corrupt what it is "
            + "writing. Close those first.";
    }

    private static string Age(TimeSpan age) => age.TotalDays >= 1
        ? Plural((int)age.TotalDays, "day")
        : age.TotalHours >= 1 ? Plural((int)age.TotalHours, "hour") : Plural((int)age.TotalMinutes, "minute");

    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";
}
