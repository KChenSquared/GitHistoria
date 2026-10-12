namespace Historia.Services.UI;

/// <summary>
/// The remedy offered when a fetch refused to move local tags the remote now points elsewhere:
/// a "Replace local …" action on the failure card that confirms as destructive, then fetches only
/// those tags with forced refspecs.
/// </summary>
public static class TagConflictPrompt
{
    /// <summary>"tag v1 points to a different commit", or the plural for several tags.</summary>
    public static string Subject(HistoriaGitException failure) =>
        GitErrorClassifier.ClobberedTags(failure.Details) switch
        {
            [var one] => $"tag {one} points to a different commit",
            [] => "a local tag points to a different commit",
            var many => $"tags {string.Join(", ", many)} point to different commits",
        };

    /// <summary>The card actions for <paramref name="failure"/>: the replacement when it is a tag
    /// conflict naming at least one tag, otherwise none.</summary>
    public static IReadOnlyList<NotificationAction> ActionsFor(
        RepositoryContext repo, string remote, Exception failure,
        IRepoNetworkActionRunner runner, IDialogService dialogs, INotificationService notifications)
    {
        if (failure is not HistoriaGitException { Kind: GitErrorKind.TagConflict } git)
            return [];
        var tags = GitErrorClassifier.ClobberedTags(git.Details);
        if (tags.Count == 0)
            return [];

        var label = tags.Count == 1 ? $"Replace local {tags[0]}" : "Replace local tags";
        return [new NotificationAction(label, () => ReplaceAsync(repo, remote, tags, runner, dialogs, notifications))];
    }

    private static async Task ReplaceAsync(
        RepositoryContext repo, string remote, IReadOnlyList<string> tags,
        IRepoNetworkActionRunner runner, IDialogService dialogs, INotificationService notifications)
    {
        if (!await dialogs.ConfirmDestructiveAsync("Replace local tag — destructive", Effect(remote, tags), "Replace"))
        {
            AppLog.Write(AppLogLevel.Info, "declined", $"Replace of tags {string.Join(", ", tags)} from {remote} declined");
            return;
        }

        try
        {
            await runner.RunExclusiveAsync(repo, "Fetch", async (progress, ct) =>
            {
                await repo.Gateway.FetchAsync(remote, new GitFetchOptions { ReplaceTags = tags }, progress, ct);
                notifications.Publish(NotificationSeverity.Success, tags.Count == 1
                    ? $"Replaced {tags[0]} with {remote}'s."
                    : $"Replaced {tags.Count} tags with {remote}'s.");
            });
        }
        finally
        {
            await repo.State.RefreshAllAsync(repo.Lifetime);
        }
    }

    /// <summary>The confirmation text: names the tags and the remote, and what happens to the
    /// commits the local tags point to now.</summary>
    internal static string Effect(string remote, IReadOnlyList<string> tags)
    {
        var one = tags.Count == 1;
        var names = string.Join(", ", tags.Select(t => $"'{t}'"));
        return $"This moves your local {(one ? "tag" : "tags")} {names} to the {(one ? "commit" : "commits")} "
            + $"{remote} has for {(one ? "it" : "them")}.\n\n"
            + "The commits they point to now are not deleted, but one that no branch or tag still reaches "
            + "drops out of the graph and can be lost when git cleans up.";
    }
}
