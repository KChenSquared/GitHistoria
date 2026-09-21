using Avalonia.Headless.XUnit;
using Historia.Platform;
using Xunit;

namespace HistoriaApp.Tests;

/// <summary>
/// F2: the Clone dialog's folder-name box must keep auto-filling from the URL box across
/// multiple pastes — the regression was the auto-fill's own programmatic write to
/// <c>folder.Text</c> being mistaken for a user edit, which silently stopped every later update.
/// Exercises <see cref="CloneDialog.ShouldAutoFillFolder"/> directly rather than driving the
/// whole modal, mirroring <c>DestructiveConfirmDialogTests</c>'s pattern of calling small static
/// helpers.
/// </summary>
[Trait("tier", "ui")]
public class CloneDialogTests
{
    [AvaloniaFact]
    public void ShouldAutoFillFolder_stays_true_after_its_own_programmatic_write()
    {
        // Before any URL: the empty box should auto-fill.
        Assert.True(CloneDialog.ShouldAutoFillFolder("", lastAutoFilled: null));

        // URL #1 pasted -> auto-fill writes "repo-one" into folder.Text, which itself raises
        // folder.TextChanged. That write must not be mistaken for a user edit.
        Assert.True(CloneDialog.ShouldAutoFillFolder(currentFolderText: "repo-one", lastAutoFilled: "repo-one"));

        // URL #2 pasted -> the folder box still holds URL #1's guess, still auto-fill-owned.
        Assert.True(CloneDialog.ShouldAutoFillFolder(currentFolderText: "repo-one", lastAutoFilled: "repo-one"));
    }

    [AvaloniaFact]
    public void ShouldAutoFillFolder_becomes_false_once_the_user_types_something_else()
    {
        var lastAutoFilled = "repo-one";

        Assert.False(CloneDialog.ShouldAutoFillFolder("my-custom-folder", lastAutoFilled));
    }
}
