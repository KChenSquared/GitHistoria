using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Historia.Platform;
using Historia.Services.UI;
using Xunit;
using Path = Avalonia.Controls.Shapes.Path;

namespace HistoriaApp.Tests;

/// <summary>
/// Constitution Rule 00, half 2: the hand-rolled dialog builder must give <b>Cancel</b> the
/// primary (blue) class and the Enter default while keeping it the rightmost button, and a
/// <see cref="DialogIcon.Warning"/> dialog must actually carry the red <c>warning</c>.
/// The call-site half (every destructive action routes through the one entry point) is guarded
/// by <c>Historia.Tests.DestructiveConfirmationTests</c>.
/// </summary>
public class DestructiveConfirmDialogTests
{
    [AvaloniaFact]
    public void Destructive_confirm_row_makes_Cancel_primary_default_and_rightmost()
    {
        var (row, confirm, cancel) = SimpleDialog.ConfirmButtonRow(
            confirmLabel: "Delete", cancelLabel: "Cancel", defaultToCancel: true, primaryConfirm: false);

        Assert.NotNull(cancel);
        Assert.Same(cancel, row.Children[^1]);            // rightmost
        Assert.Contains("primary", cancel!.Classes);      // blue
        Assert.True(cancel.IsDefault);                    // Enter lands on Cancel
        Assert.DoesNotContain("primary", confirm.Classes); // the confirm button is never blue here
        Assert.False(confirm.IsDefault);
    }

    [AvaloniaFact]
    public void Non_destructive_confirm_row_keeps_the_confirm_button_as_default()
    {
        var (row, confirm, cancel) = SimpleDialog.ConfirmButtonRow(
            confirmLabel: "OK", cancelLabel: "Cancel", defaultToCancel: false, primaryConfirm: true);

        Assert.Same(cancel, row.Children[^1]);            // Cancel still rightmost
        Assert.Contains("primary", confirm.Classes);
        Assert.True(confirm.IsDefault);
        Assert.DoesNotContain("primary", cancel!.Classes);
    }

    [AvaloniaFact]
    public void Warning_icon_puts_a_warning_glyph_beside_the_message()
    {
        var warned = SimpleDialog.MessageBlock("Delete 3 files?", maxWidth: 380, DialogIcon.Warning);
        Assert.IsType<StackPanel>(warned);
        Assert.Contains(warned.GetLogicalDescendants(), d => d is Path);

        var plain = SimpleDialog.MessageBlock("Just a message.", maxWidth: 380, DialogIcon.None);
        Assert.IsType<TextBlock>(plain);
    }
}
