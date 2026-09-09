namespace Historia.Services.UI;

/// <summary>
/// v8.4: "Stash all" behaviour behind the shortcuts-bar stash button (the <c>save-check</c>
/// icon, <see cref="Historia.Controls.ShortcutsBarControl"/>). Stashes the whole working tree
/// (tracked edits + untracked files). The stash message is <c>Stash of &lt;branch&gt;</c> unless
/// <see cref="AppSettings.UseCustomStashNames"/> is on, in which case the user is prompted for
/// one (cancelling the prompt cancels the stash).
/// </summary>
internal static class StashActions
{
    public static async Task<bool> StashAllAsync(
        RepositoryContext ctx,
        IStashService stash,
        ISettingsService settings,
        IDialogService dialogs,
        Historia.Services.Notifications.INotificationService notifications)
    {
        var branch = ctx.State.CurrentBranch?.Name ?? "HEAD";
        var message = $"Stash of {branch}";

        if (settings.Current.UseCustomStashNames)
        {
            var entered = await dialogs.PromptAsync("Stash all", "Stash message:", message,
                confirmLabel: "Stash", primaryConfirm: true);
            if (string.IsNullOrWhiteSpace(entered))
                return false; // cancelled
            message = entered.Trim();
        }

        try
        {
            var stashed = await stash.SaveAsync(
                ctx,
                new StashSaveOptions { Message = message, IncludeUntracked = true },
                ctx.Lifetime);

            await ctx.State.RefreshAllAsync(ctx.Lifetime);
            notifications.Publish(
                stashed ? Historia.Services.Notifications.NotificationSeverity.Success
                        : Historia.Services.Notifications.NotificationSeverity.Info,
                stashed ? $"Stashed all changes — “{message}”." : "Nothing to stash.");
            return stashed;
        }
        catch (Exception ex)
        {
            notifications.Publish(Historia.Services.Notifications.NotificationSeverity.Error,
                $"Stash all failed: {ex.Message}", ex.NotificationDetails());
            return false;
        }
    }
}
