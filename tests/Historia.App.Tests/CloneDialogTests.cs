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

    private static Historia.Models.GitProfile Profile(string name, bool global = false) => new()
    {
        Id = Guid.NewGuid(), DisplayName = name, GitUserName = name, GitUserEmail = $"{name}@example.com", IsGlobal = global,
    };

    // the clone dialog pre-selects the default profile (picker index = profile index + 1).
    [AvaloniaFact]
    public void InitialProfileIndex_selects_a_non_global_default()
    {
        var global = Profile("global", global: true);
        var work = Profile("work");
        Assert.Equal(2, CloneDialog.InitialProfileIndex([global, work], work.Id));
    }

    [AvaloniaFact]
    public void InitialProfileIndex_falls_back_to_the_global_profile_when_unset_or_missing()
    {
        var work = Profile("work");
        var global = Profile("global", global: true);
        Assert.Equal(2, CloneDialog.InitialProfileIndex([work, global], null));
        Assert.Equal(2, CloneDialog.InitialProfileIndex([work, global], Guid.NewGuid()));
    }

    [AvaloniaFact]
    public void InitialProfileIndex_is_local_config_with_no_profiles()
    {
        Assert.Equal(0, CloneDialog.InitialProfileIndex([], null));
    }
}
