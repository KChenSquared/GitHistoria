namespace HistoriaApp.Tests;

/// <summary>
/// Every note on the Developer panel is a <c>?</c> hint on the control it explains, with its
/// whole text; the developer-log hint's path paragraph follows the checkbox; and the delete-all
/// confirm carries the action's verb.
/// </summary>
[Trait("tier", "ui")]
public sealed class DeveloperViewHintTests
{
    private const string DevLogExplanation =
        "Records errors, every Git command with its result, and repository open/close. Terminal input/output isn't logged. One file per session; the newest 10 are kept. Also on when HISTORIA_LOG is set.";

    private static string DevLogPathLine() => Historia.Diagnostics.AppLog.Enabled
        ? $"Writing to {Historia.Diagnostics.AppLog.CurrentLogPath}"
        : $"Saved under {AppPaths.LogsDirectory} when enabled";

    private static Dictionary<string, string> ExpectedHints() => new()
    {
        ["Developer"] = "Shown only when an enableDevMode file is next to Historia.exe.",
        ["Use custom theme"] = "Replaces the Light / Dark / System choice while a theme is selected. High Contrast still wins. Turned off when Historia starts without developer mode.",
        ["Throw test exception"] = "Throws an unhandled error to test crash handling.",
        ["Show sample toast"] = "Shows a success toast. It closes after 2 s and has no Copy button.",
        ["Show sample alert"] = "Opens the red confirm dialog shown before deleting a branch. Neither button changes anything.",
        ["Performance log"] = $"Records startup and repository-open timings to {Historia.Diagnostics.PerfLog.LogPath}. Stays on across launches; also on when HISTORIA_PERF is set.",
        ["Developer log"] = DevLogExplanation + "\n\n" + DevLogPathLine(),
        ["Show auto-fetch status on tabs"] = "Adds a colour bar to each tab: red = auto-fetch not running, yellow = not fetched yet this round, green = fetched this round.",
        ["Clear graph cache"] = "Deletes saved graph layouts. Each repository rebuilds its graph next time it opens.",
        ["Clear sidebar cache"] = "Deletes saved sidebar data (branches, tags, stashes, submodules, worktrees). It's rebuilt from Git next time.",
        ["Delete all Historia data"] = "Deletes all settings, profiles, themes, sessions, caches and logs. Can't be undone. Restart Historia afterwards.",
    };

    private static (DeveloperView View, FakeDialogService Dialogs) CreateView()
    {
        var dialogs = new FakeDialogService();
        var settings = new InMemorySettingsService();
        var customThemes = new InMemoryCustomThemeService();
        var theme = new ThemeService(settings, new FixedThemeProbe(), customThemes);
        var sp = DeveloperViewServices.Build(settings, theme, customThemes, new NavigationBus(), dialogs);
        var view = new DeveloperView(sp);
        view.Measure(new Size(800, 600));
        view.Arrange(new Rect(0, 0, 800, 600));
        return (view, dialogs);
    }

    [AvaloniaFact]
    public void Every_developer_panel_hint_has_its_full_text()
    {
        var (view, _) = CreateView();

        Assert.Equal(ExpectedHints().OrderBy(p => p.Key, StringComparer.Ordinal),
                     HelpHintMap.Of(view).OrderBy(p => p.Key, StringComparer.Ordinal));

        var root = view.GetLogicalDescendants().OfType<ScrollViewer>().First().Content as StackPanel
            ?? throw new InvalidOperationException("Developer panel root StackPanel not found.");
        Assert.DoesNotContain(root.Children, c => c is TextBlock);
    }

    [AvaloniaFact]
    public void Developer_log_hint_updates_when_toggled()
    {
        var wasEnabled = Historia.Diagnostics.AppLog.Enabled;
        try
        {
            Historia.Diagnostics.AppLog.Enabled = false;
            var (view, _) = CreateView();
            var box = view.GetLogicalDescendants().OfType<CheckBox>().Single(c => (c.Content as string) == "Developer log");
            string Hint() => HelpHintMap.Of(view)["Developer log"];

            Assert.Equal(DevLogExplanation + $"\n\nSaved under {AppPaths.LogsDirectory} when enabled", Hint());

            box.IsChecked = true;
            Assert.True(Historia.Diagnostics.AppLog.Enabled);
            Assert.StartsWith(DevLogExplanation + "\n\nWriting to ", Hint());
            Assert.Equal(DevLogExplanation + $"\n\nWriting to {Historia.Diagnostics.AppLog.CurrentLogPath}", Hint());

            var badge = HelpHintMap.Badges(view).Single(b => HelpHintMap.AnchorLabel(b) == "Developer log");
            Assert.Equal(Hint(), Avalonia.Automation.AutomationProperties.GetHelpText(badge));

            box.IsChecked = false;
            Assert.Equal(DevLogExplanation + $"\n\nSaved under {AppPaths.LogsDirectory} when enabled", Hint());
        }
        finally
        {
            Historia.Diagnostics.AppLog.Enabled = wasEnabled;
        }
    }

    [AvaloniaFact]
    public void Delete_all_confirm_uses_the_delete_verb()
    {
        var (view, dialogs) = CreateView();
        var button = view.GetLogicalDescendants().OfType<Button>()
            .Single(b => (b.Content as string) == "Delete all Historia data");

        // The fake declines, so nothing is deleted.
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var call = Assert.Single(dialogs.DestructiveCalls);
        Assert.Equal("Delete all Historia data?", call.Title);
        Assert.Equal("Delete", call.ConfirmLabel);
        Assert.Equal(
            "This permanently deletes every Historia setting, profile, repo binding, theme, saved "
            + "session, cached repository/graph file and log from Historia's data and cache folders. "
            + "It cannot be undone. Restart Historia afterwards.",
            call.Effect);
    }

    [AvaloniaFact]
    public void Sample_alert_opens_the_delete_branch_confirm()
    {
        var (view, dialogs) = CreateView();
        var button = view.GetLogicalDescendants().OfType<Button>()
            .Single(b => (b.Content as string) == "Show sample alert");

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var call = Assert.Single(dialogs.DestructiveCalls);
        Assert.Equal(("Delete branch", "Delete 'sample-branch'?", "Delete"), call);
    }

    private sealed class FakeDialogService : IDialogService
    {
        public List<(string Title, string Effect, string ConfirmLabel)> DestructiveCalls { get; } = [];

        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
        {
            DestructiveCalls.Add((title, effect, confirmLabel));
            return Task.FromResult(false);
        }

        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false, string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None) =>
            throw new InvalidOperationException("Unexpected plain confirm.");

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None) =>
            throw new InvalidOperationException("Unexpected chooser.");

        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null, string confirmLabel = "OK", bool primaryConfirm = false) => Task.FromResult<string?>(null);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
