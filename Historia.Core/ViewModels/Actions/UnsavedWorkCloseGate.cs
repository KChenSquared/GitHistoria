namespace Historia.Services.UI;

public enum CloseDecision
{
    Allow,
    CancelOnly,
    CancelAndPrompt,
}

/// <summary>
/// State machine guarding the window close workflow against re-entrant prompts.
/// UI-thread only.
/// </summary>
public sealed class UnsavedWorkCloseGate
{
    private bool _confirmed;
    private bool _promptOpen;

    public CloseDecision Evaluate(int unsavedCount)
    {
        if (_confirmed)
        {
            return CloseDecision.Allow;
        }

        if (_promptOpen)
        {
            return CloseDecision.CancelOnly;
        }

        if (unsavedCount == 0)
        {
            return CloseDecision.Allow;
        }

        _promptOpen = true;
        return CloseDecision.CancelAndPrompt;
    }

    public void Complete(bool proceed)
    {
        _promptOpen = false;
        _confirmed = proceed;
    }
}
