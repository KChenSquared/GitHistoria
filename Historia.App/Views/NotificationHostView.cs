using Avalonia.Threading;
using Historia.Services.Git;
using static Historia.Views.ViewKit;

namespace Historia.Views;

/// <summary>Toast stack for <see cref="INotificationService"/> (V4 port of NotificationHost.razor).</summary>
public sealed class NotificationHostView : UserControl, IDisposable
{
    /// <summary>Success toasts (commit / push / pull / fetch) clear themselves after this long.</summary>
    private static readonly TimeSpan SuccessAutoDismiss = TimeSpan.FromSeconds(2);

    private readonly INotificationService _notifications;
    private readonly StackPanel _stack = new() { Spacing = 8, Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };

    // Ids we've already armed an auto-dismiss timer for, so a re-render doesn't stack timers.
    private readonly HashSet<Guid> _autoDismissArmed = [];

    public NotificationHostView(INotificationService notifications)
    {
        _notifications = notifications;
        IsHitTestVisible = true;
        Content = _stack;
        _notifications.Changed += OnChanged;
        Render(_notifications.Current);
    }

    private void OnChanged(object? sender, NotificationsChangedEventArgs e)
        => Avalonia.Threading.Dispatcher.UIThread.Post(() => Render(e.Current));

    private void Render(IReadOnlyList<Notification> items)
    {
        _stack.Children.Clear();
        _autoDismissArmed.RemoveWhere(id => items.All(n => n.Id != id));

        foreach (var n in items)
        {
            var key = n.Severity switch
            {
                NotificationSeverity.Error => "ColorDanger",
                NotificationSeverity.Warning => "ColorWarning",
                NotificationSeverity.Success => "ColorSuccess",
                _ => "ColorBorderStrong",
            };

            // Success toasts are informational-only: they drop the Copy button and time out on
            // their own after 7 s. Errors/warnings keep Copy and stay until dismissed.
            var isSuccess = n.Severity == NotificationSeverity.Success;
            if (isSuccess && _autoDismissArmed.Add(n.Id))
                DispatcherTimer.RunOnce(() => _notifications.Dismiss(n.Id), SuccessAutoDismiss);

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
            header.Children.Add(msg);
            Grid.SetColumn(corner, 1);
            header.Children.Add(corner);

            var body = new StackPanel { Spacing = 8, MaxWidth = hasDetails ? 920 : 420 };
            body.Children.Add(header);

            if (hasDetails)
            {
                // A fixed, always-visible scrollable pane for the detail text (was an Expander).
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
        catch (Exception ex) { _notifications.Publish(NotificationSeverity.Error, $"The action failed: {ex.Message}", ex.NotificationDetails()); }
    }

    private async Task CopyAsync(string text)
    {
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip is not null)
            await clip.SetTextAsync(text);
    }

    public void Dispose() => _notifications.Changed -= OnChanged;
}
