namespace Historia.Views;

/// <summary>Toast stack for <see cref="INotificationService"/>. With
/// <see cref="AppSettings.AutoHideNotices"/> on, every notice closes itself
/// <see cref="AutoHideAfter"/> after it is first shown (or after the setting is turned on);
/// with it off, notices stay until dismissed.</summary>
public sealed class NotificationHostView : UserControl, IDisposable
{
    internal static readonly TimeSpan AutoHideAfter = TimeSpan.FromSeconds(10);

    /// <summary>How long a notice stays before closing itself, or null when it stays until dismissed.</summary>
    internal static TimeSpan? AutoHideDelay(AppSettings settings) => settings.AutoHideNotices ? AutoHideAfter : null;

    private readonly INotificationService _notifications;
    private readonly ISettingsService _settings;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private readonly StackPanel _stack = new() { Spacing = 8, Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };

    // One armed auto-hide timer per shown notice, so a re-render never stacks timers and turning
    // the setting off can stop them all.
    private readonly Dictionary<Guid, IDisposable> _autoHideTimers = [];

    public NotificationHostView(INotificationService notifications, ISettingsService settings)
        : this(notifications, settings, ScheduleOnDispatcher)
    {
    }

    /// <summary><paramref name="schedule"/> runs an action once after a delay and returns a handle
    /// that cancels it; tests pass a manual one.</summary>
    internal NotificationHostView(INotificationService notifications, ISettingsService settings, Func<TimeSpan, Action, IDisposable> schedule)
    {
        _notifications = notifications;
        _settings = settings;
        _schedule = schedule;
        IsHitTestVisible = true;
        Content = _stack;
        _notifications.Changed += OnChanged;
        _settings.Changed += OnSettingsChanged;
        Render(_notifications.Current);
    }

    /// <summary>Notices with an armed auto-hide timer.</summary>
    internal int ArmedAutoHideCount => _autoHideTimers.Count;

    private static IDisposable ScheduleOnDispatcher(TimeSpan delay, Action action) => DispatcherTimer.RunOnce(action, delay);

    private void OnChanged(object? sender, NotificationsChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => Render(e.Current));

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.OldSettings.AutoHideNotices != e.NewSettings.AutoHideNotices)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => SyncAutoHide(_notifications.Current));
    }

    private void SyncAutoHide(IReadOnlyList<Notification> items)
    {
        foreach (var id in _autoHideTimers.Keys.Where(id => items.All(n => n.Id != id)).ToList())
        {
            _autoHideTimers[id].Dispose();
            _autoHideTimers.Remove(id);
        }

        if (AutoHideDelay(_settings.Current) is not { } delay)
        {
            foreach (var timer in _autoHideTimers.Values)
                timer.Dispose();
            _autoHideTimers.Clear();
            return;
        }

        foreach (var n in items)
        {
            if (_autoHideTimers.ContainsKey(n.Id))
                continue;
            var id = n.Id;
            _autoHideTimers[id] = _schedule(delay, () =>
            {
                _autoHideTimers.Remove(id);
                _notifications.Dismiss(id);
            });
        }
    }

    private void Render(IReadOnlyList<Notification> items)
    {
        _stack.Children.Clear();
        SyncAutoHide(items);

        foreach (var n in items)
        {
            var key = n.Severity switch
            {
                NotificationSeverity.Error => "ColorDanger",
                NotificationSeverity.Warning => "ColorWarning",
                NotificationSeverity.Success => "ColorSuccess",
                _ => "ColorBorderStrong",
            };

            // Success toasts are informational-only and drop the Copy button.
            var isSuccess = n.Severity == NotificationSeverity.Success;

            var hasDetails = !string.IsNullOrEmpty(n.Details);
            var copyText = hasDetails ? $"{n.Message}\n\n{n.Details}" : n.Message;

            var corner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
            if (!isSuccess)
            {
                // Copy: a bordered word, sized to match the dismiss icon's height, top-right corner.
                var copy = TextButton("Copy", () => _ = CopyAsync(copyText));
                copy.Padding = new Thickness(7, 2);
                copy.MinHeight = 0;
                copy.FontSize = 11;
                copy.VerticalAlignment = VerticalAlignment.Top;
                corner.Children.Add(copy);
            }
            corner.Children.Add(IconButton("close", "Dismiss", () => _notifications.Dismiss(n.Id)));

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(7, 7, 7, 0) };
            var msg = Text(n.Count > 1 ? $"{n.Message}  ×{n.Count}" : n.Message, "ColorText", 13);
            msg.VerticalAlignment = VerticalAlignment.Center;
            // The shared Text() helper ellipsis-trims to one line; a toast must show its whole
            // message, so wrap within the card width instead of cutting off with "…".
            msg.TextWrapping = TextWrapping.Wrap;
            msg.TextTrimming = TextTrimming.None;
            header.Children.Add(msg);
            Grid.SetColumn(corner, 1);
            header.Children.Add(corner);

            var body = new StackPanel { Spacing = 8, MaxWidth = hasDetails ? 920 : 420 };
            body.Children.Add(header);

            if (hasDetails)
            {
                // A fixed, always-visible scrollable pane for the detail text.
                // Full width — 5px from the card border; text is inset past the scrollbar track.
                body.Children.Add(new Border
                {
                    Height = 220,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Child = new ScrollViewer
                    {
                        Padding = new Thickness(8, 8, 20, 8),
                        AllowAutoHide = false,
                        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                        Content = new SelectableTextBlock
                        {
                            Text = n.Details,
                            FontFamily = new FontFamily("Consolas, monospace"),
                            FontSize = 11,
                            TextWrapping = TextWrapping.Wrap,
                        },
                    },
                }.Dyn(Border.BorderBrushProperty, "ColorBorderStrong").Dyn(Border.BackgroundProperty, "ColorChrome"));
            }

            var actionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(7, 0, 7, 5) };
            foreach (var a in n.Actions)
            {
                var act = a;
                actionRow.Children.Add(TextButton(a.Label, () => _ = RunAsync(act)));
            }
            if (actionRow.Children.Count > 0)
                body.Children.Add(actionRow);

            var card = new Border
            {
                Child = body,
                Padding = new Thickness(5),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1, 1, 1, 1),
                BorderBrush = null,
            }.Dyn(Border.BackgroundProperty, "ColorSurface").Dyn(Border.BorderBrushProperty, key);
            card.BoxShadow = BoxShadows.Parse("0 4 16 0 #40000000");
            _stack.Children.Add(card);
        }
    }

    private async Task RunAsync(NotificationAction action)
    {
        try { await action.Invoke(); }
        catch (Exception ex) { _notifications.Publish(NotificationSeverity.Error, ex.NotificationMessage("The action"), ex.NotificationDetails()); }
    }

    private async Task CopyAsync(string text)
    {
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip is not null)
            await clip.SetTextAsync(text);
    }

    public void Dispose()
    {
        _notifications.Changed -= OnChanged;
        _settings.Changed -= OnSettingsChanged;
        foreach (var timer in _autoHideTimers.Values)
            timer.Dispose();
        _autoHideTimers.Clear();
    }
}
