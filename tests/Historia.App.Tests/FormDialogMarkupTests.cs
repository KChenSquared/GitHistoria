namespace HistoriaApp.Tests;

/// <summary>
/// The About, Add remote and Edit remote windows are markup bound to their view models: each opens,
/// shows its fields, and Cancel closes it with no result.
/// </summary>
[Trait("tier", "ui")]
public sealed class FormDialogMarkupTests
{
    private sealed class StubThemeService : IThemeService
    {
        public EffectiveTheme Effective => EffectiveTheme.Dark;
        public ThemeMode CurrentMode => ThemeMode.Dark;
        public bool CustomThemeActive => false;
        public bool HighContrastEnabled => false;
        public string EffectiveCssThemeName => "dark";
        public IReadOnlyDictionary<string, Rgb> Palette => HistoriaPalette.For(Effective);
        public IReadOnlyList<Rgb> LaneColors => [];

#pragma warning disable CS0067
        public event EventHandler<ThemeChangedEventArgs>? ThemeChanged;
#pragma warning restore CS0067

        public Task ApplyModeAsync(ThemeMode mode, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetCustomThemeEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public Task SelectCustomThemeAsync(string themeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetHighContrastAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static RemoteInfo Remote(string url, string? pushUrl = null, string name = "origin") =>
        new() { Name = name, Url = url, PushUrl = pushUrl ?? url };

    private static void Open(Window window)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    private static Button ButtonWithContent(Window window, string content) =>
        window.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, content));

    private static void Click(Button button) => button.Command!.Execute(button.CommandParameter);

    private static IEnumerable<string> Texts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");

    [AvaloniaFact]
    public void About_dialog_shows_the_product_version_and_credits_and_OK_closes_it()
    {
        var dialog = new AboutDialog(new StubThemeService(), "1.0.0+dac352a1e8b7c9d0");
        Open(dialog);

        var texts = Texts(dialog).ToList();
        Assert.Contains("Historia", texts);
        Assert.Contains("Version 1.0.0 (dac352a)", texts);
        foreach (var credit in AboutDialog.Credits)
            Assert.Contains(credit, texts);
        Assert.NotNull(dialog.GetVisualDescendants().OfType<Image>().Single().Source);

        var closed = false;
        dialog.Closed += (_, _) => closed = true;
        ButtonWithContent(dialog, "OK").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(closed);
        Assert.True(dialog.ViewModel.IsDisposed);
    }

    [AvaloniaFact]
    public async Task Add_remote_dialog_opens_with_Add_disabled_and_Cancel_returns_null()
    {
        var dialog = new AddRemoteDialog([]);
        Open(dialog);

        var boxes = dialog.GetVisualDescendants().OfType<TextBox>().ToList();
        Assert.Equal("origin", boxes[0].Text);
        Assert.Equal("", boxes[1].Text);
        Assert.False(ButtonWithContent(dialog, "Add").IsEffectivelyEnabled);
        Assert.True(ButtonWithContent(dialog, "Add").IsDefault);
        Assert.True(ButtonWithContent(dialog, "Cancel").IsCancel);

        Click(ButtonWithContent(dialog, "Cancel"));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(await dialog.Completion);
    }

    [AvaloniaFact]
    public async Task Add_remote_dialog_typing_a_url_enables_Add_and_returns_the_values()
    {
        var dialog = new AddRemoteDialog([]);
        Open(dialog);

        dialog.GetVisualDescendants().OfType<TextBox>().Last().Text = "git@host:o/r.git";
        Dispatcher.UIThread.RunJobs();
        var add = ButtonWithContent(dialog, "Add");
        Assert.True(add.IsEffectivelyEnabled);

        Click(add);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new AddRemoteResult("origin", "git@host:o/r.git", true), await dialog.Completion);
    }

    [AvaloniaFact]
    public void Add_remote_dialog_shows_the_problem_for_a_taken_name()
    {
        var dialog = new AddRemoteDialog([Remote("git@host:o/r.git")]);
        Open(dialog);

        dialog.GetVisualDescendants().OfType<TextBox>().First().Text = "origin";
        Dispatcher.UIThread.RunJobs();

        var problem = dialog.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("problem"));
        Assert.True(problem.IsVisible);
        Assert.Equal("A remote named 'origin' already exists.", problem.Text);
        Assert.False(ButtonWithContent(dialog, "Add").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task Edit_remote_dialog_opens_with_the_remote_and_Cancel_returns_null()
    {
        var dialog = new EditRemoteDialog(Remote("git@host:o/r.git"), [], new Dictionary<string, string>());
        Open(dialog);

        var boxes = dialog.GetVisualDescendants().OfType<TextBox>().ToList();
        Assert.Equal("origin", boxes[0].Text);
        Assert.Equal("git@host:o/r.git", boxes[1].Text);
        Assert.True(ButtonWithContent(dialog, "Save").IsEffectivelyEnabled);
        Assert.False(dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsVisible);

        Click(ButtonWithContent(dialog, "Cancel"));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(await dialog.Completion);
    }

    [AvaloniaFact]
    public async Task Edit_remote_dialog_offers_the_push_url_reset_only_when_a_separate_one_exists()
    {
        var dialog = new EditRemoteDialog(
            Remote("git@host:o/r.git", pushUrl: "git@host:o/push.git"), [], new Dictionary<string, string>());
        Open(dialog);

        var reset = dialog.GetVisualDescendants().OfType<CheckBox>().Single();
        Assert.True(reset.IsVisible);
        Assert.Equal("Reset push URL (currently git@host:o/push.git)", reset.Content);

        reset.IsChecked = true;
        Click(ButtonWithContent(dialog, "Save"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new EditRemoteResult("origin", "git@host:o/r.git", true), await dialog.Completion);
    }

    [AvaloniaFact]
    public async Task Closing_a_form_dialog_without_choosing_returns_null()
    {
        var dialog = new AddRemoteDialog([]);
        Open(dialog);

        dialog.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Null(await dialog.Completion);
    }
}
