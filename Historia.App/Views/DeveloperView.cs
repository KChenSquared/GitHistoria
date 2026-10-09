namespace Historia.Views;

/// <summary>
/// Developer-only tools panel. Only routed to when <see cref="Historia.Platform.DevMode"/>
/// is enabled.
/// </summary>
public sealed class DeveloperView : UserControl
{
    public DeveloperView(IServiceProvider sp)
    {
        var root = new StackPanel { Spacing = 18, Margin = new Thickness(32), MaxWidth = 640 };
        root.Children.Add(WithHint(Text("Developer", "ColorText", 24, FontWeight.SemiBold),
            "Shown when developer mode is on in Settings → Advanced, or an enableDevMode file is next to Historia.exe."));

        root.Children.Add(CustomThemeRow(sp));

        root.Children.Add(WithHint(TextButton("Throw test exception", () =>
            throw new InvalidOperationException("Developer panel: deliberate test exception.")),
            "Throws an unhandled error to test crash handling."));

        root.Children.Add(WithHint(TextButton("Show sample toast", () =>
            sp.GetRequiredService<INotificationService>().Publish(
                NotificationSeverity.Success, "Commit created.")),
            "Shows a success toast. It closes after 2 s and has no Copy button."));

        root.Children.Add(WithHint(TextButton("Show sample alert", () =>
            _ = sp.GetRequiredService<IDialogService>().ConfirmDestructiveAsync(
                "Delete branch", "Delete 'sample-branch'?", "Delete")),
            "Opens the red confirm dialog shown before deleting a branch. Neither button changes anything."));

        var openLogs = WithHint(TextButton("Open logs folder", () =>
            sp.GetRequiredService<IExternalLauncher>().OpenFolder(AppPaths.LogsDirectory)),
            $"Opens {AppPaths.LogsDirectory}.");
        openLogs.HorizontalAlignment = HorizontalAlignment.Left;
        root.Children.Add(openLogs);

        var afViz = new CheckBox
        {
            Content = "Show auto-fetch status on tabs",
            IsChecked = Historia.Platform.AutoFetchDebug.Enabled,
        };
        afViz.IsCheckedChanged += (_, _) => Historia.Platform.AutoFetchDebug.Enabled = afViz.IsChecked == true;
        root.Children.Add(WithHint(afViz,
            "Adds a colour bar to each tab: red = auto-fetch not running, yellow = not fetched yet this round, green = fetched this round."));

        root.Children.Add(WithHint(TextButton("Clear graph cache", () =>
        {
            var removed = sp.GetRequiredService<IGraphLayoutService>().ClearPersistentCache();
            sp.GetRequiredService<INotificationService>().Publish(
                NotificationSeverity.Success,
                removed == 0 ? "No persisted graph cache to remove." : $"Removed {removed} cached graph file(s).");
        }), "Deletes saved graph layouts. Each repository rebuilds its graph next time it opens."));

        root.Children.Add(WithHint(TextButton("Clear sidebar cache", () =>
        {
            var removed = sp.GetRequiredService<IRepoStateCache>().WipeAll();
            sp.GetRequiredService<INotificationService>().Publish(
                NotificationSeverity.Success,
                removed == 0 ? "No persisted repo cache to remove." : $"Removed {removed} cached repo file(s).");
        }), "Deletes saved sidebar data (branches, tags, stashes, submodules, worktrees). It's rebuilt from Git next time."));

        var deleteAll = WithHint(TextButton("Delete all Historia data", () => _ = DeleteAllAsync(sp)),
            "Deletes all settings, profiles, themes, sessions, caches and logs. Can't be undone. Restart Historia afterwards.");
        deleteAll.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(deleteAll);

        Content = ClosablePane(new ScrollViewer { Content = root }, sp);
        this.Dyn(BackgroundProperty, "ColorChrome");
    }

    /// <summary>
    /// The custom-theme override: <c>[Use custom theme] [saved themes ▾] [Manage themes]</c>.
    /// The dropdown shows only when a theme is saved and stores the selection whether or not the
    /// box is ticked; ticking with no saved themes also opens the Themes panel. The panel is
    /// rebuilt on every navigation, so the row reads the settings once and needs no
    /// subscription.
    /// </summary>
    private static Control CustomThemeRow(IServiceProvider sp)
    {
        var settings = sp.GetRequiredService<ISettingsService>();
        var theme = sp.GetRequiredService<IThemeService>();
        var nav = sp.GetRequiredService<INavigationBus>();
        var saved = sp.GetRequiredService<ICustomThemeService>().List();

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var use = new CheckBox
        {
            Content = "Use custom theme",
            IsChecked = settings.Current.UseCustomTheme,
            VerticalAlignment = VerticalAlignment.Center,
        };
        use.IsCheckedChanged += (_, _) =>
        {
            var on = use.IsChecked == true;
            _ = theme.SetCustomThemeEnabledAsync(on);
            if (on && saved.Count == 0)
                nav.RequestNavigate("themes");
        };
        row.Children.Add(use);

        if (saved.Count > 0)
        {
            var combo = new ComboBox { Width = 220, ItemsSource = saved.Select(t => t.Name).ToList() };
            var stored = saved.ToList().FindIndex(t => t.Id == settings.Current.CustomThemeId);
            combo.SelectedIndex = stored >= 0 ? stored : 0;
            combo.SelectionChanged += (_, _) =>
            {
                var i = combo.SelectedIndex;
                if (i >= 0 && i < saved.Count && saved[i].Id != settings.Current.CustomThemeId)
                    _ = theme.SelectCustomThemeAsync(saved[i].Id);
            };
            row.Children.Add(combo);
        }

        row.Children.Add(TextButton("Manage themes", () => nav.RequestNavigate("themes")));

        return WithHint(row,
            "Replaces the Light / Dark / System choice while a theme is selected. High Contrast still wins. Turned off when Historia starts without developer mode.");
    }

    private static async Task DeleteAllAsync(IServiceProvider sp)
    {
        var confirmed = await sp.GetRequiredService<IDialogService>().ConfirmDestructiveAsync(
            "Delete all Historia data?",
            "This permanently deletes every Historia setting, profile, repo binding, theme, saved "
            + "session, cached repository/graph file and log from Historia's data and cache folders. "
            + "It cannot be undone. Restart Historia afterwards.",
            "Delete");
        if (!confirmed)
            return;

        var removed = AppPaths.WipeAllLocalData();
        sp.GetRequiredService<INotificationService>().Publish(
            NotificationSeverity.Success,
            $"Removed {removed} file(s). Restart Historia for a clean state.");
    }
}
