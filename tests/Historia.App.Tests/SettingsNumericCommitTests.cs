using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Historia.Views;
using Xunit;

namespace HistoriaApp.Tests;

/// <summary>
/// M1.1 (#14): every numeric Settings field commits on <c>LostFocus</c> <b>and</b> on Enter,
/// only when the clamped value actually changed. <see cref="SettingsView"/>'s private
/// <c>WireNumericCommit</c> helper is exercised directly through reflection — standing up the
/// whole view needs seven unrelated services (<c>ISettingsService</c>, <c>IThemeService</c>,
/// <c>ICustomThemeService</c>, <c>INavigationBus</c>, <c>IDialogService</c>,
/// <c>IGraphLayoutService</c>, <c>IRepoStateCache</c>) faked out just to reach this one helper,
/// so this drives the real production wiring against a bare <see cref="NumericUpDown"/> instead.
///
/// Driving it needed one correction to the naive approach: setting <c>NumericUpDown.Value</c>
/// directly does not simulate typed input — <c>NumericUpDown</c>'s own LostFocus/Enter class
/// handlers resync <c>Value</c> <i>from</i> <c>Text</c> on both events (confirmed empirically
/// here: setting <c>Value</c> then raising LostFocus left <c>Value</c> reset to <c>null</c>,
/// because the stale empty <c>Text</c> won the resync). Setting <c>.Text</c> instead — what
/// typing into the templated inner <c>TextBox</c> actually does — is the correct simulation, and
/// is what the Avalonia-source finding in the helper's doc comment (`SettingsView.cs`) describes:
/// by the time our instance handler runs, <c>Value</c> already reflects the typed text.
/// </summary>
public class SettingsNumericCommitTests
{
    private static void WireNumericCommit(NumericUpDown nud, int initial, Func<int, int> clamp, Action<int> commit)
    {
        var method = typeof(SettingsView).GetMethod("WireNumericCommit", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("SettingsView.WireNumericCommit not found — rename tracked here too.");
        method.Invoke(null, [nud, initial, clamp, commit]);
    }

    private static void RaiseEnter(NumericUpDown nud) =>
        nud.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

    private static void RaiseLostFocus(NumericUpDown nud) =>
        nud.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));

    [AvaloniaFact]
    public void Enter_commits_a_changed_typed_value_once()
    {
        var nud = new NumericUpDown { Value = 3 };
        var commits = new List<int>();
        WireNumericCommit(nud, initial: 3, clamp: v => v, commit: commits.Add);

        nud.Text = "7"; // what typing "7" into the inner TextBox leaves behind
        RaiseEnter(nud);
        Assert.Equal([7], commits);

        // Enter again with no further change commits nothing more.
        RaiseEnter(nud);
        Assert.Equal([7], commits);
    }

    [AvaloniaFact]
    public void LostFocus_commits_a_changed_typed_value_through_the_clamp()
    {
        var nud = new NumericUpDown { Value = 3 };
        var commits = new List<int>();
        WireNumericCommit(nud, initial: 3, clamp: v => Math.Clamp(v, 0, 5), commit: commits.Add);

        nud.Text = "9";
        RaiseLostFocus(nud);

        Assert.Equal([5], commits); // clamped, not the raw 9
    }

    [AvaloniaFact]
    public void LostFocus_with_unchanged_text_does_not_commit()
    {
        var nud = new NumericUpDown { Value = 4 };
        var commits = new List<int>();
        WireNumericCommit(nud, initial: 4, clamp: v => v, commit: commits.Add);

        nud.Text = "4";
        RaiseLostFocus(nud);

        Assert.Empty(commits);
    }
}
