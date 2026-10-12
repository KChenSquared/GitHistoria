namespace HistoriaApp.Tests;

/// <summary>
/// The toast stack's markup: each notification renders as a card whose border carries its severity
/// colour from the palette, Copy is absent on success, details and remedy buttons appear only when
/// the notification has them, and the buttons drive the item commands.
/// </summary>
[Trait("tier", "ui")]
public sealed class NotificationHostViewTests
{
    private sealed class Rig : IDisposable
    {
        private readonly Window _window;

        public Rig(AppSettings? settings = null)
        {
            Notifications = new NotificationService();
            View = new NotificationHostView(Notifications, new FixedSettings(settings ?? new AppSettings { AutoHideNotices = false }), (_, _) => new NoopHandle());
            _window = new Window { Width = 900, Height = 700, Content = View };
            _window.Show();
            Pump();
        }

        public NotificationService Notifications { get; }
        public NotificationHostView View { get; }

        public IReadOnlyList<Border> Cards()
            => View.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("card")).ToList();

        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            _window.UpdateLayout();
        }

        public void Dispose()
        {
            View.Dispose();
            _window.Close();
        }
    }

    private sealed class FixedSettings(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; } = current;

#pragma warning disable CS0067
        public event EventHandler<SettingsChangedEventArgs>? Changed;
#pragma warning restore CS0067

        public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default) => Task.CompletedTask;

        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopHandle : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private static Avalonia.Media.Color PaletteColor(string token) => HistoriaPalette.Get(EffectiveTheme.Dark, token).ToAvalonia();

    private static Avalonia.Media.Color SolidColor(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static IEnumerable<Button> Buttons(Control root) => root.GetVisualDescendants().OfType<Button>();

    private static StackPanel ToastBody(Control card)
        => card.GetVisualDescendants().OfType<StackPanel>().First(p => p.Classes.Contains("toast-body"));

    private static Button ButtonLabelled(Control root, string label)
        => Buttons(root).Single(b => b.Content as string == label);

    [AvaloniaFact]
    public void The_view_carries_its_view_model_as_data_context()
    {
        using var rig = new Rig();

        Assert.Same(rig.View.ViewModel, rig.View.DataContext);
        Assert.Empty(rig.Cards());
    }

    [AvaloniaTheory]
    [InlineData(NotificationSeverity.Error, "danger")]
    [InlineData(NotificationSeverity.Warning, "warning")]
    [InlineData(NotificationSeverity.Success, "success")]
    [InlineData(NotificationSeverity.Info, "border-strong")]
    public void A_card_border_takes_its_severity_colour_from_the_palette(NotificationSeverity severity, string token)
    {
        using var rig = new Rig();

        rig.Notifications.Publish(severity, "message");
        rig.Pump();

        var card = Assert.Single(rig.Cards());
        Assert.Equal(PaletteColor(token), SolidColor(card.BorderBrush));
    }

    [AvaloniaFact]
    public void Cards_follow_the_notifications_in_order_and_show_the_repeat_count()
    {
        using var rig = new Rig();

        rig.Notifications.Publish(NotificationSeverity.Info, "first");
        rig.Notifications.Publish(NotificationSeverity.Error, "second");
        rig.Notifications.Publish(NotificationSeverity.Error, "second");
        rig.Pump();

        var texts = rig.Cards()
            .Select(c => c.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("text")).Text)
            .ToList();
        Assert.Equal(["first", "second  ×2"], texts);
    }

    [AvaloniaFact]
    public void Success_has_no_Copy_button_and_the_other_severities_do()
    {
        using var rig = new Rig();

        rig.Notifications.Publish(NotificationSeverity.Success, "done");
        rig.Notifications.Publish(NotificationSeverity.Info, "fyi");
        rig.Pump();

        var cards = rig.Cards();
        Assert.False(ButtonLabelled(cards[0], "Copy").IsVisible);
        Assert.True(ButtonLabelled(cards[1], "Copy").IsVisible);
    }

    [AvaloniaFact]
    public void Details_show_in_a_pane_and_remedy_buttons_in_a_row_only_when_present()
    {
        using var rig = new Rig();

        rig.Notifications.Publish(NotificationSeverity.Error, "plain");
        rig.Notifications.Publish(NotificationSeverity.Error, "rich", "stderr text", [new NotificationAction("Retry", () => Task.CompletedTask)]);
        rig.Pump();

        var plain = rig.Cards()[0];
        var rich = rig.Cards()[1];
        Assert.DoesNotContain(plain.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.IsEffectivelyVisible);
        Assert.DoesNotContain(Buttons(plain), b => b.Content as string == "Retry" && b.IsEffectivelyVisible);
        Assert.Equal(420, ToastBody(plain).MaxWidth);

        var details = Assert.Single(rich.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.IsEffectivelyVisible);
        Assert.Equal("stderr text", details.Text);
        Assert.True(ButtonLabelled(rich, "Retry").IsEffectivelyVisible);
        Assert.Equal(920, ToastBody(rich).MaxWidth);
    }

    [AvaloniaFact]
    public void Pressing_dismiss_removes_the_card_and_the_notification()
    {
        using var rig = new Rig();
        rig.Notifications.Publish(NotificationSeverity.Info, "bye");
        rig.Pump();
        var dismiss = Buttons(rig.Cards()[0]).Single(b => ToolTip.GetTip(b) as string == "Dismiss");

        dismiss.Command!.Execute(dismiss.CommandParameter);
        rig.Pump();

        Assert.Empty(rig.Notifications.Current);
        Assert.Empty(rig.Cards());
    }

    [AvaloniaFact]
    public void Pressing_a_remedy_button_runs_its_action()
    {
        using var rig = new Rig();
        var runs = 0;
        rig.Notifications.Publish(NotificationSeverity.Error, "rejected", actions: [new NotificationAction("Pull", () => { runs++; return Task.CompletedTask; })]);
        rig.Pump();
        var pull = ButtonLabelled(rig.Cards()[0], "Pull");

        pull.Command!.Execute(pull.CommandParameter);

        Assert.Equal(1, runs);
    }

    private sealed class RecordingClipboard : IClipboardService
    {
        public List<string> Texts { get; } = [];

        public Task SetTextAsync(string text)
        {
            Texts.Add(text);
            return Task.CompletedTask;
        }
    }

    [AvaloniaFact]
    public async Task Copy_goes_to_the_clipboard_the_service_provider_registers()
    {
        var notifications = new NotificationService();
        var clipboard = new RecordingClipboard();
        await using var sp = new ServiceCollection()
            .AddSingleton<INotificationService>(notifications)
            .AddSingleton<ISettingsService>(new FixedSettings(new AppSettings { AutoHideNotices = false }))
            .AddSingleton<IClipboardService>(clipboard)
            .BuildServiceProvider();
        using var view = new NotificationHostView(sp);

        notifications.Publish(NotificationSeverity.Error, "boom", "stderr text");
        Dispatcher.UIThread.RunJobs();
        await Assert.Single(view.ViewModel.Items).CopyDetailsCommand.ExecuteAsync(null);

        Assert.Contains("boom", Assert.Single(clipboard.Texts));
    }

    [AvaloniaFact]
    public void Disposing_the_view_stops_it_following_the_service()
    {
        var rig = new Rig();
        rig.Notifications.Publish(NotificationSeverity.Info, "before");
        rig.Pump();
        rig.View.Dispose();

        rig.Notifications.Publish(NotificationSeverity.Info, "after");
        rig.Pump();

        Assert.Single(rig.Cards());
        Assert.True(rig.View.ViewModel.IsDisposed);
        rig.Dispose();
    }
}
