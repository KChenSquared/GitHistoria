namespace Historia.ViewModels;

/// <summary>
/// The shell window's state: the status bar text and the close workflow. Closing is a two-step
/// exchange with the window: <see cref="EvaluateClose"/> decides synchronously, so the window can
/// cancel the close in the same event, and <see cref="ResolveUnsavedWorkAsync"/> then asks the user
/// what to do with the unsaved work and reports whether the window may close. Unsaved work blocks
/// the close until the user resolves it, and a second close request while the prompt is open is
/// refused without a second prompt.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly IUnsavedWorkRegistry? _unsavedWork;
    private readonly IDialogService? _dialogs;
    private readonly UnsavedWorkCloseGate _closeGate = new();

    private IReadOnlyList<IUnsavedWork> _pendingWork = [];

    public MainWindowViewModel(IUnsavedWorkRegistry? unsavedWork = null, IDialogService? dialogs = null)
    {
        _unsavedWork = unsavedWork;
        _dialogs = dialogs;
    }

    /// <summary>The text shown in the status bar.</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    /// <summary>
    /// Decides what a close request does. <see cref="CloseDecision.CancelAndPrompt"/> means the
    /// caller cancels the close and then awaits <see cref="ResolveUnsavedWorkAsync"/>;
    /// <see cref="CloseDecision.CancelOnly"/> means a prompt is already open. Without a dialog
    /// service there is nothing to ask, so the window closes.
    /// </summary>
    public CloseDecision EvaluateClose()
    {
        var work = _unsavedWork?.For(null) ?? [];
        var decision = _closeGate.Evaluate(_dialogs is not null ? work.Count : 0);
        if (decision == CloseDecision.CancelAndPrompt)
            _pendingWork = work;
        return decision;
    }

    /// <summary>
    /// Asks the user to save or discard the unsaved work found by the last
    /// <see cref="EvaluateClose"/> and returns whether the window may now close. A prompt that
    /// fails counts as a refusal. The gate is released either way, so the next close request
    /// prompts again.
    /// </summary>
    public async Task<bool> ResolveUnsavedWorkAsync()
    {
        var work = _pendingWork;
        _pendingWork = [];
        var proceed = false;
        try
        {
            if (_dialogs is not null)
                proceed = await UnsavedWorkPrompt.ResolveAsync(work, _dialogs);
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "MainWindow", "Resolving unsaved work before close failed.", ex);
        }
        finally
        {
            _closeGate.Complete(proceed);
        }

        return proceed;
    }
}
