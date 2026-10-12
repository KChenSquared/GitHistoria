namespace Historia.ViewModels;

/// <summary>One tab in the terminal dock's strip: a live session, its label and whether it is the
/// active one. The dock replaces the whole set whenever the sessions or the focus change, so a tab
/// never changes after it is built.</summary>
public sealed partial class TerminalTabViewModel : ObservableObject
{
    private readonly Action<TerminalSession> _activate;
    private readonly Func<TerminalSession, Task> _close;

    public TerminalTabViewModel(
        TerminalSession session,
        string label,
        bool isActive,
        Action<TerminalSession> activate,
        Func<TerminalSession, Task> close)
    {
        Session = session;
        Label = label;
        IsActive = isActive;
        _activate = activate;
        _close = close;
    }

    public TerminalSession Session { get; }

    /// <summary>The session's reported title (or its origin's title), with an exit marker once the shell is gone.</summary>
    public string Label { get; }

    public bool IsActive { get; }

    [RelayCommand]
    private void Activate() => _activate(Session);

    [RelayCommand]
    private async Task CloseAsync() => await _close(Session);
}
