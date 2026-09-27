namespace Historia.Tests;

/// <summary>
/// The byte table is load-bearing. Expected sequences are cross-checked against
/// XTerm.NET's own generator, captured in <c>docs/memory/2026-08-31-v5-m1-spike.md</c>.
/// </summary>
[Trait("tier", "unit")]
public class KeyEncoderTests
{
    private static readonly TerminalModes Normal = new(ApplicationCursor: false, ApplicationKeypad: false);
    private static readonly TerminalModes AppCursor = new(ApplicationCursor: true, ApplicationKeypad: false);

    private static byte[] Enc(TerminalKey k, TerminalModifiers m = TerminalModifiers.None, TerminalModes? modes = null)
        => KeyEncoder.Encode(k, m, modes ?? Normal).ToArray();

    [Fact]
    public void Arrows_NormalMode()
    {
        Assert.Equal("\x1b[A"u8.ToArray(), Enc(TerminalKey.Up));
        Assert.Equal("\x1b[B"u8.ToArray(), Enc(TerminalKey.Down));
        Assert.Equal("\x1b[C"u8.ToArray(), Enc(TerminalKey.Right));
        Assert.Equal("\x1b[D"u8.ToArray(), Enc(TerminalKey.Left));
    }

    [Fact]
    public void Arrows_ApplicationCursorMode()
    {
        Assert.Equal("\x1bOA"u8.ToArray(), Enc(TerminalKey.Up, modes: AppCursor));
        Assert.Equal("\x1bOD"u8.ToArray(), Enc(TerminalKey.Left, modes: AppCursor));
    }

    [Fact]
    public void HomeEnd_ModeDependent()
    {
        Assert.Equal("\x1b[H"u8.ToArray(), Enc(TerminalKey.Home));
        Assert.Equal("\x1b[F"u8.ToArray(), Enc(TerminalKey.End));
        Assert.Equal("\x1bOH"u8.ToArray(), Enc(TerminalKey.Home, modes: AppCursor));
    }

    [Fact]
    public void NavKeys_Tilde()
    {
        Assert.Equal("\x1b[5~"u8.ToArray(), Enc(TerminalKey.PageUp));
        Assert.Equal("\x1b[6~"u8.ToArray(), Enc(TerminalKey.PageDown));
        Assert.Equal("\x1b[2~"u8.ToArray(), Enc(TerminalKey.Insert));
        Assert.Equal("\x1b[3~"u8.ToArray(), Enc(TerminalKey.Delete));
    }

    [Fact]
    public void FunctionKeys_F1ToF12()
    {
        Assert.Equal("\x1bOP"u8.ToArray(), Enc(TerminalKey.F1));
        Assert.Equal("\x1bOQ"u8.ToArray(), Enc(TerminalKey.F2));
        Assert.Equal("\x1bOR"u8.ToArray(), Enc(TerminalKey.F3));
        Assert.Equal("\x1bOS"u8.ToArray(), Enc(TerminalKey.F4));
        Assert.Equal("\x1b[15~"u8.ToArray(), Enc(TerminalKey.F5));
        Assert.Equal("\x1b[17~"u8.ToArray(), Enc(TerminalKey.F6));
        Assert.Equal("\x1b[18~"u8.ToArray(), Enc(TerminalKey.F7));
        Assert.Equal("\x1b[19~"u8.ToArray(), Enc(TerminalKey.F8));
        Assert.Equal("\x1b[20~"u8.ToArray(), Enc(TerminalKey.F9));
        Assert.Equal("\x1b[21~"u8.ToArray(), Enc(TerminalKey.F10));
        Assert.Equal("\x1b[23~"u8.ToArray(), Enc(TerminalKey.F11));
        Assert.Equal("\x1b[24~"u8.ToArray(), Enc(TerminalKey.F12));
    }

    [Fact]
    public void Modifiers_ProduceParameterisedForm()
    {
        Assert.Equal("\x1b[1;5C"u8.ToArray(), Enc(TerminalKey.Right, TerminalModifiers.Control));
        Assert.Equal("\x1b[1;2A"u8.ToArray(), Enc(TerminalKey.Up, TerminalModifiers.Shift));
        Assert.Equal("\x1b[1;3D"u8.ToArray(), Enc(TerminalKey.Left, TerminalModifiers.Alt));
        Assert.Equal("\x1b[1;4A"u8.ToArray(), Enc(TerminalKey.Up, TerminalModifiers.Shift | TerminalModifiers.Alt));
        // SS3 finals collapse to CSI 1;n<final> under a modifier.
        Assert.Equal("\x1b[1;5P"u8.ToArray(), Enc(TerminalKey.F1, TerminalModifiers.Control));
        // CSI <num>~ becomes CSI <num>;n~
        Assert.Equal("\x1b[3;5~"u8.ToArray(), Enc(TerminalKey.Delete, TerminalModifiers.Control));
    }

    [Fact]
    public void Enter_IsCarriageReturn() => Assert.Equal(new byte[] { 0x0D }, Enc(TerminalKey.Enter));

    [Fact]
    public void Backspace_IsDel() => Assert.Equal(new byte[] { 0x7F }, Enc(TerminalKey.Backspace));

    [Fact]
    public void Tab_And_ShiftTab()
    {
        Assert.Equal(new byte[] { 0x09 }, Enc(TerminalKey.Tab));
        Assert.Equal("\x1b[Z"u8.ToArray(), Enc(TerminalKey.Tab, TerminalModifiers.Shift));
    }

    [Fact]
    public void Alt_PrefixesEscape()
    {
        Assert.Equal(new byte[] { 0x1B, 0x0D }, Enc(TerminalKey.Enter, TerminalModifiers.Alt));
        Assert.Equal(new byte[] { 0x1B, 0x7F }, Enc(TerminalKey.Backspace, TerminalModifiers.Alt));
    }

    [Fact]
    public void CtrlC_IsZeroThree()
    {
        Assert.Equal((byte)0x03, KeyEncoder.EncodeControlChar('c', TerminalModifiers.Control));
        Assert.Equal((byte)0x03, KeyEncoder.EncodeControlChar('C', TerminalModifiers.Control));
    }

    [Fact]
    public void ControlChar_NullWithoutControl()
        => Assert.Null(KeyEncoder.EncodeControlChar('c', TerminalModifiers.None));

    [Fact]
    public void ControlChar_Punctuation()
    {
        Assert.Equal((byte)0x00, KeyEncoder.EncodeControlChar(' ', TerminalModifiers.Control));
        Assert.Equal((byte)0x1B, KeyEncoder.EncodeControlChar('[', TerminalModifiers.Control));
        Assert.Equal((byte)0x1D, KeyEncoder.EncodeControlChar(']', TerminalModifiers.Control));
        Assert.Equal((byte)0x1F, KeyEncoder.EncodeControlChar('_', TerminalModifiers.Control));
    }

    [Fact]
    public void ClaimsUnencodedKey_False_ForNoModifierAndShift()
    {
        Assert.False(KeyEncoder.ClaimsUnencodedKey(TerminalModifiers.None));
        Assert.False(KeyEncoder.ClaimsUnencodedKey(TerminalModifiers.Shift));
    }

    [Fact]
    public void ClaimsUnencodedKey_False_ForCtrlAlt_AltGr()
    {
        Assert.False(KeyEncoder.ClaimsUnencodedKey(TerminalModifiers.Control | TerminalModifiers.Alt));
        Assert.False(KeyEncoder.ClaimsUnencodedKey(TerminalModifiers.Control | TerminalModifiers.Alt | TerminalModifiers.Shift));
    }

    [Fact]
    public void ClaimsUnencodedKey_True_ForCtrlOnly_AndAltOnly()
    {
        Assert.True(KeyEncoder.ClaimsUnencodedKey(TerminalModifiers.Control));
        Assert.True(KeyEncoder.ClaimsUnencodedKey(TerminalModifiers.Control | TerminalModifiers.Shift));
        Assert.True(KeyEncoder.ClaimsUnencodedKey(TerminalModifiers.Alt));
        Assert.True(KeyEncoder.ClaimsUnencodedKey(TerminalModifiers.Alt | TerminalModifiers.Shift));
    }

    [Fact]
    public void EncodeMeta_PrefixesEscape()
    {
        Assert.Equal(new byte[] { 0x1B, (byte)'b' }, KeyEncoder.EncodeMeta('b', TerminalModifiers.Alt));
        Assert.Equal(new byte[] { 0x1B, (byte)'B' }, KeyEncoder.EncodeMeta('B', TerminalModifiers.Alt));
        Assert.Equal(new byte[] { 0x1B, (byte)'.' }, KeyEncoder.EncodeMeta('.', TerminalModifiers.Alt));
    }

    [Fact]
    public void EncodeMeta_Null_WithCtrl_OrWithoutAlt()
    {
        Assert.Null(KeyEncoder.EncodeMeta('b', TerminalModifiers.None));
        Assert.Null(KeyEncoder.EncodeMeta('b', TerminalModifiers.Control));
        Assert.Null(KeyEncoder.EncodeMeta('b', TerminalModifiers.Control | TerminalModifiers.Alt));
        Assert.Null(KeyEncoder.EncodeMeta('\t', TerminalModifiers.Alt));
    }
}
