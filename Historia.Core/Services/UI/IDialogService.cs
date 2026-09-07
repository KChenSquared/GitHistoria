namespace Historia.Services.UI;

/// <summary>Optional leading glyph for a confirmation dialog.</summary>
public enum DialogIcon
{
    None,

    /// <summary>A large red <c>triangle-alert</c> — for a machine-wide or otherwise weighty change.</summary>
    Warning,
}

/// <summary>
/// Native WPF dialogs invoked from Blazor components (§6.2.1 — cross-boundary popups must be
/// WPF, not HTML overlays). Registered as a singleton; safe to call from any thread.
/// </summary>
public interface IDialogService
{
    Task<string?> PickFolderAsync(string title, string? initialDirectory = null);
    Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null);

    /// <summary>Destructive-action confirmation naming the exact effect (§6.9.1, §6.15.1).
    /// <paramref name="defaultToCancel"/> makes <c>Cancel</c> the highlighted (primary /
    /// Enter) button and leaves the confirm button plain — for an irreversible wipe where the
    /// safe choice should be the obvious one. <paramref name="icon"/> puts a leading glyph
    /// (e.g. a red <c>triangle-alert</c>) beside the message.</summary>
    Task<bool> ConfirmAsync(string title, string message, bool destructive = false,
        string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None);

    /// <summary>
    /// Modal with one button per entry in <paramref name="options"/> plus an implicit Cancel.
    /// The option at <paramref name="defaultIndex"/> is the highlighted (primary / default)
    /// button; the option at <paramref name="destructiveIndex"/> (if any) gets the red
    /// outlined "danger" styling instead. Option labels stay on one line — the window widens
    /// to fit rather than trimming. Returns the 0-based index of the chosen option, or -1 if
    /// cancelled / closed.
    /// </summary>
    Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options,
        int defaultIndex = 0, int destructiveIndex = -1);

    Task ShowMessageAsync(string title, string message);

    /// <summary>Single-line text prompt (branch name, tag name, clone URL, …).
    /// <paramref name="confirmLabel"/> names the accept button; <paramref name="primaryConfirm"/>
    /// gives it the accent (primary) styling.</summary>
    Task<string?> PromptAsync(string title, string message, string? defaultValue = null,
        string confirmLabel = "OK", bool primaryConfirm = false);
}
