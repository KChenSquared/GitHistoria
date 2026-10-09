namespace HistoriaApp.Tests;

/// <summary>
/// Every note on the Developer panel is a <c>?</c> hint on the control it explains, with its
/// whole text; the developer-log hint's path paragraph follows the checkbox; and the delete-all
/// confirm carries the action's verb.
/// </summary>
[Trait("tier", "ui")]
public sealed class DeveloperViewHintTests
{
    private static Dictionary<string, string> ExpectedHints() => new()
    {
        ["Developer"] = "Shown when developer mode is on in Settings → Advanced, or an enableDevMode file is next to Historia.exe.",
        ["Use custom theme"] = "Replaces the Light / Dark / System choice while a theme is selected. High Contrast still wins. Turned off when Historia starts without developer mode.",
        ["Throw test exception"] = "Throws an unhandled error to test crash handling.",
        ["Show sample toast"] = "Shows a success toast. It closes after 2 s and has no Copy button.",
        ["Show sample alert"] = "Opens the red confirm dialog shown before deleting a branch. Neither button changes anything.",
        ["Open logs folder"] = $"Opens {AppPaths.LogsDirectory}.",
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
