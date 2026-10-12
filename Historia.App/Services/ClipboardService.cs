namespace Historia.Services.UI;

/// <summary>
/// The system clipboard, reached through the desktop lifetime's main window. Does nothing when
/// there is no main window or the window has no clipboard (headless runs). Stateless, so a view
/// without a service provider can create its own.
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    public Task SetTextAsync(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Dispatcher.UIThread.CheckAccess()
            ? SetOnUiThreadAsync(text)
            : Dispatcher.UIThread.InvokeAsync(() => SetOnUiThreadAsync(text));
    }

    private static Task SetOnUiThreadAsync(string text)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var clipboard = window is null ? null : TopLevel.GetTopLevel(window)?.Clipboard;
        return clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
    }
}
