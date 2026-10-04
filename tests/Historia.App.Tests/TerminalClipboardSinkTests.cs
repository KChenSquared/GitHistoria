namespace HistoriaApp.Tests;

/// <summary>The dock is the OSC 52 sink: a write the session service forwards lands on the
/// system clipboard, and only while the dock is in a window.</summary>
[Trait("tier", "ui")]
public class TerminalClipboardSinkTests
{
    private sealed class FakeSessions : ITerminalSessionService
    {
        public IReadOnlyList<TerminalSession> Sessions => [];
        public TerminalSession? Focused => null;

        public Task<TerminalSession> OpenOrFocusAsync(TerminalOrigin origin, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TerminalSession> OpenNewAsync(TerminalOrigin origin, CancellationToken ct = default) => throw new NotSupportedException();
        public void RequestActivation() { }
        public void Focus(TerminalSession session) { }
        public Task CloseAsync(Guid sessionId) => Task.CompletedTask;
        public Task CloseForTabAsync(string tabId) => Task.CompletedTask;

        public event EventHandler? SessionsChanged { add { } remove { } }
        public event EventHandler? FocusedSessionChanged { add { } remove { } }
        public event EventHandler<TerminalSession>? SessionExited { add { } remove { } }
        public event EventHandler? ActivationRequested { add { } remove { } }
        public event EventHandler<TerminalClipboardWriteEventArgs>? ClipboardWriteRequested;

        public bool HasClipboardSubscriber => ClipboardWriteRequested is not null;

        public void RaiseClipboardWrite(string text)
            => ClipboardWriteRequested?.Invoke(this, new TerminalClipboardWriteEventArgs(text));
    }

    [AvaloniaFact]
    public async Task ClipboardWriteRequested_SetsClipboardText()
    {
        var sessions = new FakeSessions();
        var dock = new TerminalDockControl();
        var window = new Window { Content = dock };
        window.Show();
        dock.AttachClipboardSink(sessions);

        sessions.RaiseClipboardWrite("from vim");
        Dispatcher.UIThread.RunJobs();

#pragma warning disable CS0618
        Assert.Equal("from vim", await window.Clipboard!.GetTextAsync());
#pragma warning restore CS0618
        window.Close();
    }

    [AvaloniaFact]
    public async Task AfterDetach_EventNoLongerHandled()
    {
        var sessions = new FakeSessions();
        var dock = new TerminalDockControl();
        var window = new Window { Content = dock };
        window.Show();
        dock.AttachClipboardSink(sessions);
#pragma warning disable CS0618
        await window.Clipboard!.SetTextAsync("before");

        window.Content = null;
        Assert.False(sessions.HasClipboardSubscriber);
        sessions.RaiseClipboardWrite("after detach");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("before", await window.Clipboard!.GetTextAsync());
#pragma warning restore CS0618
        window.Close();
    }
}
