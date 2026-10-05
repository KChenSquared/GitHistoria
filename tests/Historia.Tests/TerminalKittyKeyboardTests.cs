namespace Historia.Tests;

/// <summary>
/// <see cref="TerminalSession.TryEncodeKittyKey"/> and <see cref="TerminalSession.KeyboardProtocol"/>
/// against flags the child pushes. Expected bytes follow XTerm.NET's
/// <c>Input/KittyKeyboard.cs</c> (XTerm.NET 2.0.2) and the kitty keyboard-protocol spec
/// (https://sw.kovidgoyal.net/kitty/keyboard-protocol/).
/// </summary>
[Trait("tier", "unit")]
public class TerminalKittyKeyboardTests
{
    private static TerminalSession NewSession(out FakePtyConnection pty, bool kittyKeyboard = true)
    {
        pty = new FakePtyConnection();
        return new TerminalSession(TerminalOrigin.Home("/home/me"), pty, new ImmediateUiDispatcher(), 80, 24, 1000,
            kittyKeyboard: kittyKeyboard);
    }

    private static string Encode(TerminalSession session, TerminalKeyInput input, TerminalKeyEventKind kind = TerminalKeyEventKind.Press)
    {
        Assert.True(session.TryEncodeKittyKey(input, kind, out var bytes));
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static readonly TerminalKeyInput CtrlEnter = new("Enter", "Enter", TerminalModifiers.Control);
    private static readonly TerminalKeyInput Escape = new("Escape", "Escape", TerminalModifiers.None);
    private static readonly TerminalKeyInput LetterA = new("a", "KeyA", TerminalModifiers.None);

    [Fact]
    public void Inactive_ReturnsFalse()
    {
        using var session = NewSession(out _);

        Assert.False(session.TryEncodeKittyKey(CtrlEnter, TerminalKeyEventKind.Press, out var bytes));
        Assert.Empty(bytes);
        Assert.Equal(default, session.KeyboardProtocol);
    }

    [Fact]
    public void SettingOff_PushIgnored_StaysInactive()
    {
        using var session = NewSession(out var pty, kittyKeyboard: false);
        pty.EmitOutput("\x1b[>1u");

        Assert.False(session.TryEncodeKittyKey(CtrlEnter, TerminalKeyEventKind.Press, out _));
        Assert.False(session.KeyboardProtocol.KittyActive);
    }

    [Fact]
    public void Flag1_CtrlEnter_AndEscape_UseCsiU()
    {
        using var session = NewSession(out var pty);
        pty.EmitOutput("\x1b[>1u");

        // KittyKeyboard.cs Evaluate: a modified non-text key takes CSI u ("modifiers > 0 &&
        // Key.Length != 1"); modifiers are 1 + bits, Ctrl = 4 → 5. Spec: "CSI 13;5u".
        Assert.Equal("\x1b[13;5u", Encode(session, CtrlEnter));
        // Escape is a functional key (FunctionalKeyCodes["Escape"] = 27), so disambiguate sends
        // CSI 27 u. Spec: "the Esc key ... CSI 27u".
        Assert.Equal("\x1b[27u", Encode(session, Escape));
        Assert.Equal(new TerminalKeyboardProtocol(true, false, false), session.KeyboardProtocol);
    }

    [Fact]
    public void Flag1_PlainLetter_StaysLegacy()
    {
        using var session = NewSession(out var pty);
        pty.EmitOutput("\x1b[>1u");

        // Evaluate's last branch: no flag asks for an escape code, so the text itself is sent.
        Assert.Equal("a", Encode(session, LetterA));
    }

    [Fact]
    public void Flag8_PlainLetter_ReportedAsEscapeCode()
    {
        using var session = NewSession(out var pty);
        pty.EmitOutput("\x1b[>8u");

        // ReportAllKeysAsEscapeCodes forces CSI u for text keys; spec: "a" → CSI 97u.
        Assert.Equal("\x1b[97u", Encode(session, LetterA));
        Assert.Equal(new TerminalKeyboardProtocol(true, false, true), session.KeyboardProtocol);
    }

    [Fact]
    public void Flag2_Release_CarriesEventType()
    {
        using var session = NewSession(out var pty);
        pty.EmitOutput("\x1b[>2u");

        // BuildCsiUSequence: no modifiers but an event type → ";1:3"; release = 3 in the spec.
        Assert.Equal("\x1b[97;1:3u", Encode(session, LetterA, TerminalKeyEventKind.Release));
        Assert.Equal(new TerminalKeyboardProtocol(true, true, false), session.KeyboardProtocol);
    }

    [Fact]
    public void Flag2_Repeat_CarriesEventType()
    {
        using var session = NewSession(out var pty);
        pty.EmitOutput("\x1b[>3u");

        // Ctrl+Enter repeat: ";5:2" — repeat = 2 in the spec.
        Assert.Equal("\x1b[13;5:2u", Encode(session, CtrlEnter, TerminalKeyEventKind.Repeat));
    }

    [Fact]
    public void BareShiftPress_SendsNothing()
    {
        using var session = NewSession(out var pty);
        pty.EmitOutput("\x1b[>1u");

        // Evaluate: a modifier pressed alone is suppressed unless every key is reported.
        Assert.Equal("", Encode(session, new TerminalKeyInput("Shift", "ShiftLeft", TerminalModifiers.Shift)));
    }

    [Fact]
    public void Pop_ReturnsToInactive()
    {
        using var session = NewSession(out var pty);
        pty.EmitOutput("\x1b[>1u");
        Assert.True(session.KeyboardProtocol.KittyActive);

        pty.EmitOutput("\x1b[<u");

        Assert.False(session.KeyboardProtocol.KittyActive);
        Assert.False(session.TryEncodeKittyKey(CtrlEnter, TerminalKeyEventKind.Press, out _));
    }

    [Fact]
    public void AlternateScreen_KeepsItsOwnFlagStack()
    {
        using var session = NewSession(out var pty);
        pty.EmitOutput("\x1b[>1u");

        // KittyKeyboardState: one stack per screen, switched with the buffer.
        pty.EmitOutput("\x1b[?1049h");
        Assert.False(session.KeyboardProtocol.KittyActive);
        pty.EmitOutput("\x1b[>8u");
        Assert.Equal(new TerminalKeyboardProtocol(true, false, true), session.KeyboardProtocol);

        pty.EmitOutput("\x1b[?1049l");
        Assert.Equal(new TerminalKeyboardProtocol(true, false, false), session.KeyboardProtocol);
        Assert.Equal("a", Encode(session, LetterA));
    }
}
