using Historia.Diagnostics;
using Historia.Services.Graph;
using Historia.Services.Storage;
using static Historia.Views.ViewKit;

namespace Historia.Views;

/// <summary>
/// Developer-only tools panel (V4). Only routed to when <see cref="Historia.Platform.DevMode"/>
/// is enabled.
/// </summary>
public sealed class DeveloperView : UserControl
{
    public DeveloperView(IServiceProvider sp)
    {
        var root = new StackPanel { Spacing = 18, Margin = new Thickness(32), MaxWidth = 640 };
        root.Children.Add(Text("Developer", "ColorText", 24, FontWeight.SemiBold));
        root.Children.Add(Text("Internal tools. Not shown in release builds.", "ColorTextMuted", 13));

        var throwBtn = TextButton("Throw test exception", () =>
            throw new InvalidOperationException("Developer panel: deliberate test exception."));
        throwBtn.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(throwBtn);

        var commitAlert = TextButton("Show commit alert", () =>
            sp.GetRequiredService<INotificationService>().Publish(
                NotificationSeverity.Success, "Commit created."));
        commitAlert.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(commitAlert);
        root.Children.Add(Text(
            "Fires a sample success toast — no Copy button, auto-dismisses after 2 s.",
            "ColorTextMuted", 12));

        var perf = new CheckBox { Content = "Enable performance logging", IsChecked = PerfLog.Enabled };
        perf.IsCheckedChanged += (_, _) => PerfLog.Enabled = perf.IsChecked == true;
        root.Children.Add(perf);
        root.Children.Add(Text($"Appended to {PerfLog.LogPath}", "ColorTextMuted", 12));

        var wipe = TextButton("Wipe persisted graph cache", () =>
        {
            var removed = sp.GetRequiredService<IGraphLayoutService>().ClearPersistentCache();
            sp.GetRequiredService<INotificationService>().Publish(
                NotificationSeverity.Success,
                removed == 0 ? "No persisted graph cache to remove." : $"Removed {removed} cached graph file(s).");
        });
        wipe.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(wipe);
        root.Children.Add(Text(
            "Deletes the on-disk graph walk cache; the next repo open re-walks from scratch.",
            "ColorTextMuted", 12));

        var wipeRepo = TextButton("Wipe persisted repo cache", () =>
        {
            var removed = sp.GetRequiredService<IRepoStateCache>().WipeAll();
            sp.GetRequiredService<INotificationService>().Publish(
                NotificationSeverity.Success,
                removed == 0 ? "No persisted repo cache to remove." : $"Removed {removed} cached repo file(s).");
        });
        wipeRepo.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(wipeRepo);
        root.Children.Add(Text(
            "Deletes the on-disk repo-state cache (branches, tags, stashes, submodules, worktrees); the next repo open re-enumerates.",
            "ColorTextMuted", 12));

        var wipeAll = TextButton("Wipe all Historia local data", () => _ = WipeAllAsync(sp));
        wipeAll.HorizontalAlignment = HorizontalAlignment.Left;
        wipeAll.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(wipeAll);
        root.Children.Add(Text(
            "Deletes every Historia setting, profile, repo binding, theme, session, cache file and log " +
            "from both state folders. Irreversible; restart Historia afterwards.",
            "ColorTextMuted", 12));

        Content = ClosablePane(new ScrollViewer { Content = root }, sp);
        this.Dyn(BackgroundProperty, "ColorChrome");
    }

    private static async Task WipeAllAsync(IServiceProvider sp)
    {
        var confirmed = await sp.GetRequiredService<IDialogService>().ConfirmAsync(
            "Wipe all Historia local data?",
            "This permanently deletes every Historia setting, profile, repo binding, theme, saved "
            + "session, cached repository/graph file and log from Historia's data and cache folders. "
            + "It cannot be undone. Restart Historia afterwards.",
            destructive: true,
            confirmLabel: "OK",
            defaultToCancel: true); // Cancel is the highlighted button
        if (!confirmed)
            return;

        var removed = AppPaths.WipeAllLocalData();
        sp.GetRequiredService<INotificationService>().Publish(
            NotificationSeverity.Success,
            $"Removed {removed} file(s). Restart Historia for a clean state.");
    }
}
