namespace Historia.Services.Ai;

/// <summary>
/// Whether the AI Integrations window is showing in the centre pane. App-level state, not
/// repository state. Events are raised synchronously on the caller's thread; the UI code that
/// calls these is on the UI thread. Singleton.
/// </summary>
public interface IAiIntegrationsPanel
{
    bool IsOpen { get; }

    /// <summary>The profile the window should select when it opens, if any.</summary>
    string? FocusProfileId { get; }

    /// <summary>Shows the window, selecting <paramref name="focusProfileId"/> when given.
    /// Raises <see cref="OpenChanged"/> even when already open, so the window can re-focus.</summary>
    void Open(string? focusProfileId = null);

    /// <summary>Hides the window. A no-op when it is closed.</summary>
    void Close();

    /// <summary>Raised after <see cref="IsOpen"/> or <see cref="FocusProfileId"/> changes.</summary>
    event EventHandler? OpenChanged;
}
