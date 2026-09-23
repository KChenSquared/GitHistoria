namespace Historia.Services.UI;

/// <summary>
/// Prompts the user when attempting to close a tab or exit Historia with unsaved changes.
/// </summary>
public static class UnsavedWorkPrompt
{
    public static async Task<bool> ResolveAsync(IReadOnlyList<IUnsavedWork> work, IDialogService dialogs)
    {
        if (work.Count == 0)
            return true;

        var message = work.Count == 1
            ? $"Save changes to {work[0].Description} before closing?"
            : $"The following items have unsaved changes:\n\n• {string.Join("\n• ", work.Select(w => w.Description))}\n\nDo you want to save changes before closing?";

        // Constitution Rule 00 multi-remedy chooser: "Save" (default), "Discard changes" (destructive), "Cancel" (implicit, rightmost)
        var choice = await dialogs.ChooseAsync(
            title: "Unsaved changes",
            message: message,
            options: ["Save", "Discard changes"],
            defaultIndex: 0,
            destructiveIndex: 1,
            icon: DialogIcon.Warning).ConfigureAwait(true);

        switch (choice)
        {
            case 0: // Save
                foreach (var item in work)
                {
                    var saved = await item.SaveAsync().ConfigureAwait(true);
                    if (!saved)
                        return false;
                }
                return true;

            case 1: // Discard changes
                foreach (var item in work)
                {
                    item.Discard();
                }
                return true;

            default: // Cancel (-1)
                return false;
        }
    }
}
