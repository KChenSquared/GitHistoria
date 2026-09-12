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

    /// <summary>Non-destructive confirmation (§6.9.1, §6.15.1) — detached checkout, merge,
    /// "switch to Local config", and the like. For anything that destroys uncommitted work or
    /// rewrites history, use <see cref="ConfirmDestructiveAsync"/> instead (constitution Rule
    /// 00). <paramref name="defaultToCancel"/> makes <c>Cancel</c> the highlighted (primary /
    /// Enter) button and leaves the confirm button plain. <paramref name="icon"/> puts a
    /// leading glyph (e.g. a red <c>triangle-alert</c>) beside the message.</summary>
    Task<bool> ConfirmAsync(string title, string message, bool destructive = false,
        string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None);

    /// <summary>Constitution Rule 00 — the one entry point for a destructive-action
    /// confirmation. Always shows the large red <c>triangle-alert</c>, makes <c>Cancel</c> the
    /// primary (blue) button and the Enter default while keeping it rightmost, and styles the
    /// confirm button plain/danger — never primary. <paramref name="effect"/> must name the
    /// exact thing being destroyed (the files, the branch, the commit count), never a bare
    /// "Are you sure?". <paramref name="confirmLabel"/> is the verb ("Delete", "Discard",
    /// "Force push"). Returns <c>true</c> only on an explicit confirm click.</summary>
    Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel);

    /// <summary>
    /// Modal with one button per entry in <paramref name="options"/> plus an implicit Cancel.
    /// The option at <paramref name="defaultIndex"/> is the highlighted (primary / default)
    /// button; the option at <paramref name="destructiveIndex"/> (if any) gets the red
    /// outlined "danger" styling instead. Option labels stay on one line — the window widens
    /// to fit rather than trimming. Returns the 0-based index of the chosen option, or -1 if
    /// cancelled / closed.
    /// <para>When <paramref name="destructiveIndex"/> is set, pass
    /// <paramref name="icon"/> = <see cref="DialogIcon.Warning"/> (constitution Rule 00): the
    /// dialog then shows the red <c>triangle-alert</c>, <c>Cancel</c> becomes the primary /
    /// Enter button, and no option carries the primary styling. <b>Exemption:</b> when the
    /// destructive option itself opens <see cref="ConfirmDestructiveAsync"/> before acting (the
    /// chooser only offers the destructive path, it doesn't take it), the chooser may keep its
    /// ordinary default and omit the icon — the red treatment then lands on the dialog that
    /// actually performs the destructive step.</para>
    /// </summary>
    Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options,
        int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None);

    Task ShowMessageAsync(string title, string message);

    /// <summary>Single-line text prompt (branch name, tag name, clone URL, …).
    /// <paramref name="confirmLabel"/> names the accept button; <paramref name="primaryConfirm"/>
    /// gives it the accent (primary) styling.</summary>
    Task<string?> PromptAsync(string title, string message, string? defaultValue = null,
        string confirmLabel = "OK", bool primaryConfirm = false);
}
