namespace Historia.Services.UI;

/// <summary>
/// Prompts the user when attempting to close or navigate away with unsaved changes.
/// Provides list-based resolution for registered <see cref="IUnsavedWork"/> items and a
/// standalone chooser overload for custom unsaved-work workflows.
/// </summary>
public static class UnsavedWorkPrompt
{
    public static async Task<UnsavedWorkChoice> ResolveAsync(
        IDialogService dialogs,
        string title,
        string message,
        string saveLabel,
        string discardLabel = "Discard changes")
    {
        // Constitution Rule 00 multi-remedy chooser: save (default), discard (destructive), "Cancel" (implicit, rightmost)
        var choice = await dialogs.ChooseAsync(
            title: title,
            message: message,
            options: [saveLabel, discardLabel],
            defaultIndex: 0,
            destructiveIndex: 1,
            icon: DialogIcon.Warning).ConfigureAwait(true);

        return choice switch
        {
            0 => UnsavedWorkChoice.Save,
            1 => UnsavedWorkChoice.Discard,
            _ => UnsavedWorkChoice.Cancel,
        };
    }

    public static async Task<bool> ResolveAsync(IReadOnlyList<IUnsavedWork> work, IDialogService dialogs)
    {
        if (work.Count == 0)
            return true;

        var message = work.Count == 1
            ? $"Save changes to {work[0].Description} before closing?"
            : $"The following items have unsaved changes:\n\n• {string.Join("\n• ", work.Select(w => w.Description))}\n\nDo you want to save changes before closing?";

        var choice = await ResolveAsync(dialogs, "Unsaved changes", message, "Save", "Discard changes").ConfigureAwait(true);

        switch (choice)
        {
            case UnsavedWorkChoice.Save:
                foreach (var item in work)
                {
                    var saved = await item.SaveAsync().ConfigureAwait(true);
                    if (!saved)
                        return false;
                }
                return true;

            case UnsavedWorkChoice.Discard:
                foreach (var item in work)
                {
                    item.Discard();
                }
                return true;

            default: // Cancel
                return false;
        }
    }
}

public enum UnsavedWorkChoice
{
    Save,
    Discard,
    Cancel,
}
