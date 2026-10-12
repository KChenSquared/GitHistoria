namespace Historia.Views;

/// <summary>Application settings. The page is markup bound to a <see cref="SettingsViewModel"/>;
/// the code here is view mechanics: clearing focus on a press outside an input, and committing a
/// numeric field when it loses focus or Enter is pressed.</summary>
public sealed partial class SettingsView : UserControl, IDisposable
{
    public SettingsView(IServiceProvider sp)
    {
        InitializeComponent();
        ViewModel = ActivatorUtilities.CreateInstance<SettingsViewModel>(sp, DevMode.Enabled);
        DataContext = ViewModel;

        // On the view itself, not the page's content: empirically, a Tunnel handler on the
        // ScrollViewer's content host never saw a press that landed on a plain TextBlock —
        // only on box-typed controls — while the same handler on the UserControl root sees
        // every press in the subtree, close button included.
        this.AddHandler(PointerPressedEvent, OnRootPointerPressedTunnel, RoutingStrategies.Tunnel);

        // Enter is caught with handledEventsToo so an inner TextBox can't swallow it first.
        // NumericUpDown's own Enter handling parses the typed text into Value synchronously
        // (checked against Avalonia 11.3.20's NumericUpDown.OnKeyDown), and it runs on the same
        // routed event before this ancestor sees it, so Value already reflects what is on screen.
        this.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    internal SettingsViewModel ViewModel { get; }

    /// <summary>Every settings textbox commits on <c>LostFocus</c>, but clicking a
    /// non-focusable label or blank space never moves focus, so an edit sits uncommitted until
    /// the user happens to click another input. Clearing focus here on any press that does not
    /// land on a real input lets the previously focused textbox's own LostFocus commit handler
    /// run — no commit logic moves or is duplicated. Tunnel routing so this runs before the
    /// pressed control's own handler, and <c>e.Handled</c> is left alone so that control still
    /// receives the press normally.</summary>
    internal static void OnRootPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual v)
            return;
        if (v is TextBox or ComboBox or ToggleSwitch or CheckBox or Button or NumericUpDown)
            return;
        if (v.FindAncestorOfType<TextBox>() is not null
            || v.FindAncestorOfType<ComboBox>() is not null
            || v.FindAncestorOfType<ToggleSwitch>() is not null
            || v.FindAncestorOfType<CheckBox>() is not null
            || v.FindAncestorOfType<Button>() is not null
            || v.FindAncestorOfType<NumericUpDown>() is not null)
            return;

        TopLevel.GetTopLevel(v)?.FocusManager?.ClearFocus();
    }

    private void OnNumericLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is NumericUpDown { DataContext: NumericSettingViewModel setting })
            _ = CommitAsync(setting);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter
            && (e.Source as Visual)?.FindAncestorOfType<NumericUpDown>(includeSelf: true) is { DataContext: NumericSettingViewModel setting })
            _ = CommitAsync(setting);
    }

    private static async Task CommitAsync(NumericSettingViewModel setting)
    {
        try
        {
            await setting.CommitAsync(SettleBeforeDialogAsync);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "settings", "A numeric setting could not be committed", ex);
        }
    }

    // Runs once a confirmation is about to open from a field commit.
    private static async Task SettleBeforeDialogAsync()
    {
        // Committing from the same Enter keystroke that's about to open a modal dialog
        // races the physical key: the dialog's own default button (Cancel, here — a
        // destructive confirm defaults to safe) can end up catching that same keystroke's
        // trailing key-up the instant the new window takes focus, auto-cancelling itself
        // before the user ever sees it. Flushing the Input-priority queue alone
        // isn't enough — that's Avalonia's queue, not the OS message queue the
        // native key-up is still sitting in. A short real-time delay lets that message
        // land on the field (harmless — NumericUpDown has no Enter-linked side effect
        // after commit) before the dialog exists to steal it. Also covers the same race
        // for the spin-button-click-driven LostFocus path (see the capture note below).
        await Task.Delay(SettingsViewModel.KeyReleaseSettle);

        // A click on the spin-button arrows moves focus in/out of the control's internal
        // TextBox, which fires this same LostFocus — while its RepeatButton's own pointer
        // capture is still active. Opening a modal dialog right away can strand that
        // capture (the button reads as permanently pressed, repeating forever) because the
        // dialog's nested loop takes over before the pending PointerReleased is processed.
        // Flushing the Input-priority queue first lets that release land normally.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
    }

    public void Dispose() => ViewModel.Dispose();
}
