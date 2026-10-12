namespace HistoriaApp.Tests;

/// <summary>
/// Every numeric Settings field commits on <c>LostFocus</c> <b>and</b> on Enter, only when the
/// clamped value actually changed. The fields are driven in a real <see cref="SettingsView"/>
/// hosted in a window, against in-memory settings and a scripted dialog service.
///
/// Setting <c>NumericUpDown.Value</c> directly does not simulate typed input —
/// <c>NumericUpDown</c>'s own LostFocus/Enter class handlers resync <c>Value</c> <i>from</i>
/// <c>Text</c> on both events. Setting <c>.Text</c> instead — what typing into the templated inner
/// <c>TextBox</c> actually does — is the correct simulation: by the time the view's handler runs,
/// <c>Value</c> already reflects the typed text (checked against Avalonia 11.3.20's
/// <c>NumericUpDown.OnKeyDown</c> source, whose Enter case calls <c>CommitInput()</c>).
/// </summary>
[Trait("tier", "ui")]
public class SettingsNumericCommitTests
{
    private static void RaiseEnter(NumericUpDown nud) =>
        nud.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });

    private static void RaiseLostFocus(NumericUpDown nud) =>
        nud.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));

    /// <summary>Lets the field's pre-dialog wait and the dialog flow finish.</summary>
    private static async Task SettleAsync(int milliseconds = 300)
    {
        await Task.Delay(milliseconds);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    [AvaloniaFact]
    public void Enter_commits_a_changed_typed_value_once()
    {
        using var rig = new SettingsViewRig(new AppSettings { DiffContextLines = 3 }, new ScriptedDialogs());
        var nud = rig.Field(rig.View.ViewModel.DiffContextLines);
        var saves = 0;
        rig.Settings.Changed += (_, _) => saves++;

        nud.Text = "7"; // what typing "7" into the inner TextBox leaves behind
        RaiseEnter(nud);
        Assert.Equal(7, rig.Settings.Current.DiffContextLines);
        Assert.Equal(1, saves);

        // Enter again with no further change commits nothing more.
        RaiseEnter(nud);
        Assert.Equal(1, saves);
    }

    [AvaloniaFact]
    public void LostFocus_commits_a_changed_typed_value()
    {
        using var rig = new SettingsViewRig(new AppSettings { TerminalScrollbackLines = 5_000 }, new ScriptedDialogs());
        var nud = rig.Field(rig.View.ViewModel.TerminalScrollbackLines);

        nud.Text = "8000";
        RaiseLostFocus(nud);

        Assert.Equal(8_000, rig.Settings.Current.TerminalScrollbackLines);
    }

    [AvaloniaFact]
    public void LostFocus_with_unchanged_text_does_not_commit()
    {
        using var rig = new SettingsViewRig(new AppSettings { DiffContextLines = 4 }, new ScriptedDialogs());
        var nud = rig.Field(rig.View.ViewModel.DiffContextLines);
        var saves = 0;
        rig.Settings.Changed += (_, _) => saves++;

        nud.Text = "4";
        RaiseLostFocus(nud);

        Assert.Equal(0, saves);
    }

    [AvaloniaFact]
    public void A_value_change_without_Enter_or_LostFocus_does_not_commit()
    {
        using var rig = new SettingsViewRig(new AppSettings { DiffContextLines = 10 }, new ScriptedDialogs());
        var nud = rig.Field(rig.View.ViewModel.DiffContextLines);

        // A bare Value change (a spin-button click, or Value syncing live from typed Text)
        // must not auto-commit on its own — only Enter/LostFocus should.
        nud.Value = 20;
        nud.Text = "20";
        Assert.Equal(10, rig.Settings.Current.DiffContextLines);

        RaiseLostFocus(nud);
        Assert.Equal(20, rig.Settings.Current.DiffContextLines);
    }

    [AvaloniaFact]
    public void Typing_text_does_not_fire_ValueChanged_until_Enter_or_LostFocus()
    {
        var nud = new NumericUpDown { Value = 4 };
        var valueChangedCount = 0;
        nud.ValueChanged += (_, _) => valueChangedCount++;

        nud.Text = "7";
        Assert.Equal(0, valueChangedCount);
        Assert.Equal(4, nud.Value);

        nud.Text = "75";
        Assert.Equal(0, valueChangedCount);
        Assert.Equal(4, nud.Value);

        nud.Text = "750";
        Assert.Equal(0, valueChangedCount);
        Assert.Equal(4, nud.Value);

        RaiseEnter(nud);
        Assert.Equal(1, valueChangedCount);
        Assert.Equal(750, nud.Value);
    }

    [AvaloniaFact]
    public async Task ResidentRowCap_increasing_prompts_destructive_confirmation_and_commits_on_confirm()
    {
        using var rig = new SettingsViewRig(new AppSettings { ResidentRowCap = 2_000_000 }, new ScriptedDialogs { ResultToReturn = true });
        var nud = rig.Field(rig.View.ViewModel.ResidentRowCap);

        nud.Text = "2500000";
        RaiseEnter(nud);

        // The commit waits out a real-time delay (guards the Enter-key/dialog race) before
        // touching the dialog service.
        await SettleAsync();

        Assert.Single(rig.Dialog.DestructiveCalls);
        Assert.Equal("Change the resident row cap?", rig.Dialog.DestructiveCalls[0].Title);
        Assert.Equal("Continue", rig.Dialog.DestructiveCalls[0].ConfirmLabel);
        Assert.Equal(2_500_000, rig.Settings.Current.ResidentRowCap);
        Assert.Equal(2_500_000, nud.Value);
    }

    [AvaloniaFact]
    public async Task ResidentRowCap_decreasing_prompts_destructive_confirmation_and_commits_on_confirm()
    {
        using var rig = new SettingsViewRig(new AppSettings { ResidentRowCap = 2_000_000 }, new ScriptedDialogs { ResultToReturn = true });
        var nud = rig.Field(rig.View.ViewModel.ResidentRowCap);

        nud.Text = "1500000";
        RaiseEnter(nud);
        await SettleAsync();

        Assert.Single(rig.Dialog.DestructiveCalls);
        Assert.Equal("Change the resident row cap?", rig.Dialog.DestructiveCalls[0].Title);
        Assert.Equal(1_500_000, rig.Settings.Current.ResidentRowCap);
        Assert.Equal(1_500_000, nud.Value);
    }

    [AvaloniaFact]
    public async Task ResidentRowCap_cancelling_reverts_value_and_does_not_commit()
    {
        using var rig = new SettingsViewRig(new AppSettings { ResidentRowCap = 2_000_000 }, new ScriptedDialogs { ResultToReturn = false });
        var nud = rig.Field(rig.View.ViewModel.ResidentRowCap);

        nud.Text = "1500000";
        RaiseEnter(nud);
        await SettleAsync();

        Assert.Single(rig.Dialog.DestructiveCalls);
        Assert.Equal(2_000_000, rig.Settings.Current.ResidentRowCap);
        Assert.Equal(2_000_000, nud.Value);
    }

    [AvaloniaFact]
    public async Task ResidentRowCap_unchanged_value_does_not_prompt_or_commit()
    {
        using var rig = new SettingsViewRig(new AppSettings { ResidentRowCap = 2_000_000 }, new ScriptedDialogs { ResultToReturn = true });
        var nud = rig.Field(rig.View.ViewModel.ResidentRowCap);

        nud.Text = "2000000";
        RaiseEnter(nud);
        await SettleAsync();

        Assert.Empty(rig.Dialog.DestructiveCalls);
        Assert.Equal(2_000_000, rig.Settings.Current.ResidentRowCap);
    }

    [AvaloniaFact]
    public async Task ResidentRowCap_spin_value_change_does_not_commit_until_Enter()
    {
        using var rig = new SettingsViewRig(new AppSettings { ResidentRowCap = 2_000_000 }, new ScriptedDialogs { ResultToReturn = true });
        var nud = rig.Field(rig.View.ViewModel.ResidentRowCap);

        // A spin-button click sets Value directly (no Text/Enter/LostFocus involved) — this
        // must not, on its own, schedule a commit or pop the confirmation dialog.
        nud.Value = 2_100_000;
        nud.Text = "2100000";
        await SettleAsync();
        Assert.Empty(rig.Dialog.DestructiveCalls);
        Assert.Equal(2_000_000, rig.Settings.Current.ResidentRowCap);

        RaiseEnter(nud);
        await SettleAsync();

        Assert.Single(rig.Dialog.DestructiveCalls);
        Assert.Equal(2_100_000, rig.Settings.Current.ResidentRowCap);
    }

    [AvaloniaFact]
    public async Task ResidentRowCap_above_warn_threshold_asks_twice_and_commits_on_both_confirms()
    {
        using var rig = new SettingsViewRig(new AppSettings { ResidentRowCap = 2_500_000 }, new ScriptedDialogs { ResultToReturn = true });
        var nud = rig.Field(rig.View.ViewModel.ResidentRowCap);

        nud.Text = "20000000"; // a large value is accepted unclamped
        RaiseEnter(nud);
        await SettleAsync(600);

        Assert.Equal(2, rig.Dialog.DestructiveCalls.Count);
        Assert.Equal("Change the resident row cap?", rig.Dialog.DestructiveCalls[0].Title);
        Assert.Equal("Keep this many rows in memory?", rig.Dialog.DestructiveCalls[1].Title);
        Assert.Equal("Save anyway", rig.Dialog.DestructiveCalls[1].ConfirmLabel);
        Assert.Equal(20_000_000, rig.Settings.Current.ResidentRowCap);
    }

    [AvaloniaFact]
    public async Task ResidentRowCap_cancelling_the_second_warning_reverts_and_does_not_commit()
    {
        using var rig = new SettingsViewRig(new AppSettings { ResidentRowCap = 2_500_000 }, new ScriptedDialogs { Results = new Queue<bool>([true, false]) });
        var nud = rig.Field(rig.View.ViewModel.ResidentRowCap);

        nud.Text = "6000000";
        RaiseEnter(nud);
        await SettleAsync(600);

        Assert.Equal(2, rig.Dialog.DestructiveCalls.Count);
        Assert.Equal(2_500_000, rig.Settings.Current.ResidentRowCap);
        Assert.Equal(2_500_000, nud.Value);
    }

    [AvaloniaFact]
    public async Task ResidentRowCap_at_warn_threshold_asks_only_once()
    {
        using var rig = new SettingsViewRig(new AppSettings { ResidentRowCap = 2_500_000 }, new ScriptedDialogs { ResultToReturn = true });
        var nud = rig.Field(rig.View.ViewModel.ResidentRowCap);

        nud.Text = AppSettings.ResidentRowCapWarnThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RaiseEnter(nud);
        await SettleAsync(600);

        Assert.Single(rig.Dialog.DestructiveCalls);
        Assert.Equal(AppSettings.ResidentRowCapWarnThreshold, rig.Settings.Current.ResidentRowCap);
    }
}
