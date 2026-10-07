namespace Historia.Services.Ai;

/// <inheritdoc cref="IAiIntegrationsPanel"/>
public sealed class AiIntegrationsPanel : IAiIntegrationsPanel
{
    public bool IsOpen { get; private set; }

    public string? FocusProfileId { get; private set; }

    public event EventHandler? OpenChanged;

    public void Open(string? focusProfileId = null)
    {
        IsOpen = true;
        FocusProfileId = focusProfileId;
        OpenChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        if (!IsOpen)
            return;
        IsOpen = false;
        FocusProfileId = null;
        OpenChanged?.Invoke(this, EventArgs.Empty);
    }
}
