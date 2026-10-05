namespace HistoriaApp.Tests;

/// <summary>
/// Every numeric Settings field commits on <c>LostFocus</c> <b>and</b> on Enter,
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
[Trait("tier", "ui")]
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
        var nud = new NumericUpDown
        {
            Minimum = AppSettings.MinResidentRowCap,
            Maximum = AppSettings.MaxResidentRowCap,
            Value = 2_000_000,
        };
        var dialog = new FakeDialogService { ResultToReturn = true };
        var commits = new List<int>();

        SettingsView.WireResidentRowCapCommit(nud, 2_000_000, dialog, next =>
        {
            commits.Add(next);
            return Task.CompletedTask;
        });

        nud.Text = "2500000";
        RaiseEnter(nud);

        // Commit() waits out a real-time delay (guards the Enter-key/dialog race — see the
        // doc comment on WireResidentRowCapCommit) before touching the dialog service.
        await Task.Delay(300);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Single(dialog.DestructiveCalls);
        Assert.Equal("Change the resident row cap?", dialog.DestructiveCalls[0].Title);
        Assert.Equal("Continue", dialog.DestructiveCalls[0].ConfirmLabel);
        Assert.Equal([2_500_000], commits);
        Assert.Equal(2_500_000, nud.Value);
    }

    [AvaloniaFact]
    public async Task ResidentRowCap_decreasing_prompts_destructive_confirmation_and_commits_on_confirm()
    {
        var nud = new NumericUpDown
        {
            Minimum = AppSettings.MinResidentRowCap,
            Maximum = AppSettings.MaxResidentRowCap,
            Value = 2_000_000,
        };
        var dialog = new FakeDialogService { ResultToReturn = true };
        var commits = new List<int>();

        SettingsView.WireResidentRowCapCommit(nud, 2_000_000, dialog, next =>
        {
            commits.Add(next);
            return Task.CompletedTask;
        });

        nud.Text = "1500000";
        RaiseEnter(nud);

        // Commit() waits out a real-time delay (guards the Enter-key/dialog race — see the
        // doc comment on WireResidentRowCapCommit) before touching the dialog service.
        await Task.Delay(300);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Single(dialog.DestructiveCalls);
        Assert.Equal("Change the resident row cap?", dialog.DestructiveCalls[0].Title);
        Assert.Equal([1_500_000], commits);
        Assert.Equal(1_500_000, nud.Value);
    }

    [AvaloniaFact]
    public async Task ResidentRowCap_cancelling_reverts_value_and_does_not_commit()
    {
        var nud = new NumericUpDown
        {
            Minimum = AppSettings.MinResidentRowCap,
            Maximum = AppSettings.MaxResidentRowCap,
            Value = 2_000_000,
        };
        var dialog = new FakeDialogService { ResultToReturn = false };
        var commits = new List<int>();

        SettingsView.WireResidentRowCapCommit(nud, 2_000_000, dialog, next =>
        {
            commits.Add(next);
            return Task.CompletedTask;
        });

        nud.Text = "1500000";
        RaiseEnter(nud);

        // Commit() waits out a real-time delay (guards the Enter-key/dialog race — see the
        // doc comment on WireResidentRowCapCommit) before touching the dialog service.
        await Task.Delay(300);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Single(dialog.DestructiveCalls);
        Assert.Empty(commits);
        Assert.Equal(2_000_000, nud.Value);
    }

    [AvaloniaFact]
    public async Task ResidentRowCap_unchanged_value_does_not_prompt_or_commit()
    {
        var nud = new NumericUpDown
        {
            Minimum = AppSettings.MinResidentRowCap,
            Maximum = AppSettings.MaxResidentRowCap,
            Value = 2_000_000,
        };
        var dialog = new FakeDialogService { ResultToReturn = true };
        var commits = new List<int>();

        SettingsView.WireResidentRowCapCommit(nud, 2_000_000, dialog, next =>
        {
            commits.Add(next);
            return Task.CompletedTask;
        });

        nud.Text = "2000000";
        RaiseEnter(nud);

        // Commit() waits out a real-time delay (guards the Enter-key/dialog race — see the
        // doc comment on WireResidentRowCapCommit) before touching the dialog service.
        await Task.Delay(300);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Empty(dialog.DestructiveCalls);
        Assert.Empty(commits);
    }

    [AvaloniaFact]
    public async Task ResidentRowCap_spin_value_change_does_not_commit_until_Enter()
    {
        var nud = new NumericUpDown
        {
            Minimum = AppSettings.MinResidentRowCap,
            Maximum = AppSettings.MaxResidentRowCap,
            Value = 2_000_000,
        };
        var dialog = new FakeDialogService { ResultToReturn = true };
        var commits = new List<int>();

        SettingsView.WireResidentRowCapCommit(nud, 2_000_000, dialog, next =>
        {
            commits.Add(next);
            return Task.CompletedTask;
        });

        // A spin-button click sets Value directly (no Text/Enter/LostFocus involved) — this
        // must not, on its own, schedule a commit or pop the confirmation dialog.
        nud.Value = 2_100_000;
        nud.Text = "2100000";
        await Task.Delay(300);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Empty(dialog.DestructiveCalls);
        Assert.Empty(commits);

        RaiseEnter(nud);
        await Task.Delay(300);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Single(dialog.DestructiveCalls);
        Assert.Equal([2_100_000], commits);
    }

    [AvaloniaFact]
    public async Task ResidentRowCap_above_warn_threshold_asks_twice_and_commits_on_both_confirms()
    {
        var nud = new NumericUpDown
        {
            Minimum = AppSettings.MinResidentRowCap,
            Maximum = AppSettings.MaxResidentRowCap,
            Value = 2_500_000,
        };
        var dialog = new FakeDialogService { ResultToReturn = true };
        var commits = new List<int>();

        SettingsView.WireResidentRowCapCommit(nud, 2_500_000, dialog, next =>
        {
            commits.Add(next);
            return Task.CompletedTask;
        });

        nud.Text = "20000000"; // above the old 10M hard cap — no longer clamped
        RaiseEnter(nud);

        await Task.Delay(500);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal(2, dialog.DestructiveCalls.Count);
        Assert.Equal("Change the resident row cap?", dialog.DestructiveCalls[0].Title);
        Assert.Equal("Keep this many rows in memory?", dialog.DestructiveCalls[1].Title);
        Assert.Equal("Save anyway", dialog.DestructiveCalls[1].ConfirmLabel);
        Assert.Equal([20_000_000], commits);
    }

    [AvaloniaFact]
    public async Task ResidentRowCap_cancelling_the_second_warning_reverts_and_does_not_commit()
    {
        var nud = new NumericUpDown
        {
            Minimum = AppSettings.MinResidentRowCap,
            Maximum = AppSettings.MaxResidentRowCap,
            Value = 2_500_000,
        };
        var dialog = new FakeDialogService { Results = new Queue<bool>([true, false]) };
        var commits = new List<int>();

        SettingsView.WireResidentRowCapCommit(nud, 2_500_000, dialog, next =>
        {
            commits.Add(next);
            return Task.CompletedTask;
        });

        nud.Text = "6000000";
        RaiseEnter(nud);

        await Task.Delay(500);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal(2, dialog.DestructiveCalls.Count);
        Assert.Empty(commits);
        Assert.Equal(2_500_000, nud.Value);
    }

    [AvaloniaFact]
    public async Task ResidentRowCap_at_warn_threshold_asks_only_once()
    {
        var nud = new NumericUpDown
        {
            Minimum = AppSettings.MinResidentRowCap,
            Maximum = AppSettings.MaxResidentRowCap,
            Value = 2_500_000,
        };
        var dialog = new FakeDialogService { ResultToReturn = true };
        var commits = new List<int>();

        SettingsView.WireResidentRowCapCommit(nud, 2_500_000, dialog, next =>
        {
            commits.Add(next);
            return Task.CompletedTask;
        });

        nud.Text = AppSettings.ResidentRowCapWarnThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RaiseEnter(nud);

        await Task.Delay(500);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Single(dialog.DestructiveCalls);
        Assert.Equal([AppSettings.ResidentRowCapWarnThreshold], commits);
    }

    [AvaloniaFact]
    public void WireNumericCommit_does_not_commit_a_value_change_until_LostFocus()
    {
        var nud = new NumericUpDown { Value = 10 };
        var commits = new List<int>();
        SettingsView.WireNumericCommit(nud, 10, v => v, commits.Add);

        // A bare Value change (e.g. a spin-button click, or Value syncing live from typed Text)
        // must not auto-commit on its own — only Enter/LostFocus should.
        nud.Value = 20;
        nud.Text = "20";
        Assert.Empty(commits);

        RaiseLostFocus(nud);
        Assert.Equal([20], commits);
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ResultToReturn { get; set; } = true;
        /// <summary>Per-call destructive-confirm answers, in order; falls back to <see cref="ResultToReturn"/>.</summary>
        public Queue<bool>? Results { get; set; }
        public List<(string Title, string Effect, string ConfirmLabel)> DestructiveCalls { get; } = [];

        public Task<bool> ConfirmDestructiveAsync(string title, string effect, string confirmLabel)
        {
            DestructiveCalls.Add((title, effect, confirmLabel));
            return Task.FromResult(Results is { Count: > 0 } ? Results.Dequeue() : ResultToReturn);
        }

        public Task<bool> ConfirmAsync(string title, string message, bool destructive = false, string confirmLabel = "OK", bool defaultToCancel = false, DialogIcon icon = DialogIcon.None) =>
            Task.FromResult(ResultToReturn);

        public Task<int> ChooseAsync(string title, string message, IReadOnlyList<string> options, int defaultIndex = 0, int destructiveIndex = -1, DialogIcon icon = DialogIcon.None) =>
            Task.FromResult(defaultIndex);

        public Task<string?> PickFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> PromptAsync(string title, string message, string? defaultValue = null, string confirmLabel = "OK", bool primaryConfirm = false) => Task.FromResult<string?>(null);
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
    }
}
