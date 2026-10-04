using Avalonia.Headless;

namespace HistoriaApp.Tests;

/// <summary>
/// Key routing through a real <see cref="TerminalSession"/>: with the Kitty keyboard protocol
/// pushed by the child the control sends Kitty bytes, and without it the legacy bytes are
/// unchanged.
/// </summary>
[Trait("tier", "ui")]
public class TerminalKittyRoutingTests
{
    private sealed class RecordingPty : IPtyConnection
    {
        public readonly List<byte[]> Writes = [];

        public event Action<ReadOnlyMemory<byte>>? OutputReceived;
        public event Action<int>? Exited;

        public void Write(ReadOnlySpan<byte> data) => Writes.Add(data.ToArray());
        public void Resize(int columns, int rows) { }
        public void Kill() { }
        public void Dispose() { }

        public void EmitOutput(string text) => OutputReceived?.Invoke(System.Text.Encoding.UTF8.GetBytes(text));
        public void EmitExit(int code) => Exited?.Invoke(code);

        public List<string> WrittenStrings() => Writes.Select(w => System.Text.Encoding.UTF8.GetString(w)).ToList();
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new NoTimer();

        private sealed class NoTimer : IUiTimer
        {
            public void Start() { }
            public void Stop() { }
            public void Dispose() { }
        }
    }

    private sealed record Rig(Window Window, TerminalControl Control, TerminalSession Session, RecordingPty Pty) : IDisposable
    {
        public void Dispose()
        {
            Window.Close();
            Session.Dispose();
        }
    }

    private static Rig NewRig(string? pushFlags)
    {
        var pty = new RecordingPty();
        var session = new TerminalSession(TerminalOrigin.Home("/home/me"), pty, new InlineDispatcher(), 80, 24, 1000,
            kittyKeyboard: true);
        var control = new TerminalControl { Width = 640, Height = 384 };
        var window = new Window { Content = control, Width = 640, Height = 384 };
        window.Show();
        control.SetSession(session);
        control.Focus();
        Dispatcher.UIThread.RunJobs();

        if (pushFlags is not null)
            pty.EmitOutput("\x1b[>" + pushFlags + "u");
        pty.Writes.Clear();
        return new Rig(window, control, session, pty);
    }

    [AvaloniaFact]
    public void Flag1_CtrlEnter_WritesCsiU()
    {
        using var rig = NewRig("1");

        rig.Window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, "\r");

        Assert.Equal(["\x1b[13;5u"], rig.Pty.WrittenStrings());
    }

    [AvaloniaFact]
    public void Flag8_TypingA_WritesOnceWithNoTextInputDuplicate()
    {
        using var rig = NewRig("8");

        rig.Window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
        rig.Window.KeyTextInput("a");

        Assert.Equal(["\x1b[97u"], rig.Pty.WrittenStrings());
    }

    [AvaloniaFact]
    public void Flag1_TypingA_StaysOnTextInput()
    {
        using var rig = NewRig("1");

        rig.Window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
        rig.Window.KeyTextInput("a");

        Assert.Equal(["a"], rig.Pty.WrittenStrings());
    }

    [AvaloniaFact]
    public void Flag2_KeyUp_WritesRelease()
    {
        using var rig = NewRig("3");

        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "\x1b");
        rig.Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "\x1b");

        Assert.Equal(["\x1b[27u", "\x1b[27;1:3u"], rig.Pty.WrittenStrings());
    }

    [AvaloniaFact]
    public void Flag2_HeldKey_SendsRepeat()
    {
        using var rig = NewRig("3");

        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "\x1b");
        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "\x1b");

        Assert.Equal(["\x1b[27u", "\x1b[27;1:2u"], rig.Pty.WrittenStrings());
    }

    [AvaloniaFact]
    public void LegacyPress_NeverSendsARelease()
    {
        using var rig = NewRig(null);

        rig.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "\x1b");
        rig.Pty.EmitOutput("\x1b[>3u");
        rig.Window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "\x1b");

        Assert.Equal(["\x1b"], rig.Pty.WrittenStrings());
    }

    [AvaloniaFact]
    public void Inactive_CtrlEnter_FallsBackToLegacy()
    {
        using var rig = NewRig(null);

        rig.Window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, "\r");

        Assert.Equal(["\r"], rig.Pty.WrittenStrings());
    }

    [AvaloniaFact]
    public async Task CtrlShiftC_StillCopies_AndSendsNothing()
    {
        using var rig = NewRig("8");
        rig.Pty.EmitOutput("hello");
        rig.Pty.Writes.Clear();

        rig.Window.MouseDown(new Point(1, 1), MouseButton.Left);
        rig.Window.MouseMove(new Point(400, 1));
        rig.Window.MouseUp(new Point(400, 1), MouseButton.Left);
        rig.Window.KeyPress(Key.C, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.C, "C");
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(rig.Pty.Writes);
#pragma warning disable CS0618
        Assert.Equal("hello", await rig.Window.Clipboard!.GetTextAsync());
#pragma warning restore CS0618
    }

    [AvaloniaFact]
    public void ClaimsChordNow_CtrlEnter_OnlyWhileKittyActive()
    {
        using var rig = NewRig(null);
        Assert.False(rig.Control.ClaimsChordNow(Key.Enter, KeyModifiers.Control));

        rig.Pty.EmitOutput("\x1b[>1u");

        Assert.True(rig.Control.ClaimsChordNow(Key.Enter, KeyModifiers.Control));
        Assert.False(rig.Control.ClaimsChordNow(Key.OemComma, KeyModifiers.Control));
        Assert.False(rig.Control.ClaimsChordNow(Key.OemTilde, KeyModifiers.Control));
    }
}
