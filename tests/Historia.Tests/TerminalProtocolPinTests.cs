namespace Historia.Tests;

/// <summary>Pins what the emulator tells the child about itself. The Kitty keyboard query is
/// answered only when the setting turned the protocol on; clipboard reads and graphics are never
/// answered, and the device attributes must not drift.</summary>
[Trait("tier", "unit")]
public class TerminalProtocolPinTests
{
    private static TerminalSession NewSession(out FakePtyConnection pty, int columns = 80, int rows = 24, bool kittyKeyboard = false)
    {
        pty = new FakePtyConnection();
        return new TerminalSession(TerminalOrigin.Home("/home/me"), pty, new ImmediateUiDispatcher(), columns, rows, 1000,
            kittyKeyboard: kittyKeyboard);
    }

    // XTerm InputHandler.Modes.cs KittyKeyboardQuery (:267): answers CSI ? flags u, and nothing
    // at all when the option is off.
    [Fact]
    public void KittyKeyboardQuery_WhenEnabled_RepliesWithPushedFlags()
    {
        using var session = NewSession(out var pty, kittyKeyboard: true);

        pty.EmitOutput("\x1b[>1u");
        pty.EmitOutput("\x1b[?u");

        Assert.Equal("\x1b[?1u", pty.AllWrittenText());
    }

    [Fact]
    public void KittyKeyboardQuery_WhenDisabled_GetsNoReply()
    {
        using var session = NewSession(out var pty, kittyKeyboard: false);

        pty.EmitOutput("\x1b[>1u");
        pty.EmitOutput("\x1b[?u");

        Assert.Empty(pty.Writes);
    }

    [Fact]
    public void KittyKeyboardPush_DoesNotChangeKeyEncoding()
    {
        using var session = NewSession(out var pty);
        var before = session.CurrentModes;

        pty.EmitOutput("\x1b[>1u");
        pty.EmitOutput("\x1b[?u");

        Assert.Equal(before, session.CurrentModes);
        Assert.Empty(pty.Writes);
    }

    [Fact]
    public void KittyGraphicsQuery_GetsNoReply()
    {
        using var session = NewSession(out var pty);

        pty.EmitOutput("\x1b_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA\x1b\\");

        Assert.Empty(pty.Writes);
    }

    [Fact]
    public void Osc52Read_GetsNoReply()
    {
        using var session = NewSession(out var pty);

        pty.EmitOutput("\x1b]52;c;?\x07");

        Assert.Empty(pty.Writes);
    }

    [Fact]
    public void PrimaryDeviceAttributes_ArePinned()
    {
        using var session = NewSession(out var pty);

        pty.EmitOutput("\x1b[c");

        Assert.Equal("\x1b[?65;4;1;2;6;9;15;16;17;18;21;22;28;29c", pty.AllWrittenText());
    }

    [Fact]
    public void SecondaryDeviceAttributes_ArePinned()
    {
        using var session = NewSession(out var pty);

        pty.EmitOutput("\x1b[>c");

        Assert.Equal("\x1b[>64;383;0c", pty.AllWrittenText());
    }

    [Fact]
    public void Resize_NarrowThenWiden_KeepsLongLine()
    {
        using var session = NewSession(out var pty, columns: 40);
        var line = "0123456789abcdefghijABCDEFGHIJ";

        pty.EmitOutput(line + "\r\n");
        session.Resize(10, 24);
        session.Resize(40, 24);

        var text = session.ReadBuffer(view =>
            Enumerable.Range(0, view.TotalLines).Select(view.GetLineText).FirstOrDefault(t => t.Length > 0));
        Assert.Equal(line, text);
    }
}
