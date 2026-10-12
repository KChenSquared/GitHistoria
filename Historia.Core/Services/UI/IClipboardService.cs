namespace Historia.Services.UI;

/// <summary>
/// Puts text on the system clipboard. View models take this instead of reaching the clipboard
/// through a window, so they stay free of any UI framework and tests can record what was copied.
/// </summary>
public interface IClipboardService
{
    /// <summary>Replaces the clipboard text. Does nothing when no clipboard is available.</summary>
    Task SetTextAsync(string text);
}

/// <summary>A clipboard that discards what it is given. The default where none is supplied.</summary>
public sealed class NullClipboardService : IClipboardService
{
    public static NullClipboardService Instance { get; } = new();

    private NullClipboardService()
    {
    }

    public Task SetTextAsync(string text) => Task.CompletedTask;
}
