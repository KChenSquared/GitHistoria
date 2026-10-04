namespace HistoriaApp.Tests;

[Trait("tier", "ui")]
public class TerminalKeyRoutingTests
{
    [Fact]
    public void MetaCharFor_Letters_RespectShift()
    {
        Assert.Equal('b', TerminalControl.MetaCharFor(Key.B, shift: false));
        Assert.Equal('B', TerminalControl.MetaCharFor(Key.B, shift: true));
        Assert.Equal('f', TerminalControl.MetaCharFor(Key.F, shift: false));
        Assert.Equal('F', TerminalControl.MetaCharFor(Key.F, shift: true));
    }

    [Fact]
    public void MetaCharFor_ShiftedDigit_IsNull()
    {
        Assert.Equal('1', TerminalControl.MetaCharFor(Key.D1, shift: false));
        Assert.Null(TerminalControl.MetaCharFor(Key.D1, shift: true));
        Assert.Equal('5', TerminalControl.MetaCharFor(Key.NumPad5, shift: false));
        Assert.Null(TerminalControl.MetaCharFor(Key.NumPad5, shift: true));
    }

    [Fact]
    public void ClaimsChord_CtrlB_True()
    {
        Assert.True(TerminalControl.ClaimsChord(Key.B, KeyModifiers.Control));
    }

    [Fact]
    public void ClaimsChord_CtrlComma_False()
    {
        Assert.False(TerminalControl.ClaimsChord(Key.OemComma, KeyModifiers.Control));
    }

    [Fact]
    public void ClaimsChord_CtrlEnter_False()
    {
        Assert.False(TerminalControl.ClaimsChord(Key.Enter, KeyModifiers.Control));
    }

    [Fact]
    public void ClaimsChord_CtrlBacktick_False()
    {
        Assert.False(TerminalControl.ClaimsChord(Key.OemTilde, KeyModifiers.Control));
    }

    [Fact]
    public void ClaimsChord_AltF_True()
    {
        Assert.True(TerminalControl.ClaimsChord(Key.F, KeyModifiers.Alt));
    }

    [Fact]
    public void ClaimsChord_PlainLetter_False()
    {
        Assert.False(TerminalControl.ClaimsChord(Key.F, KeyModifiers.None));
    }

    [Theory]
    [InlineData(Key.B, KeyModifiers.Control, true)]
    [InlineData(Key.OemComma, KeyModifiers.Control, false)]
    [InlineData(Key.Enter, KeyModifiers.Control, false)]
    [InlineData(Key.OemTilde, KeyModifiers.Control, false)]
    public void ClaimsChord_MatchesRegisteredAppChords(Key key, KeyModifiers modifiers, bool expectedTerminalClaim)
    {
        Assert.Equal(expectedTerminalClaim, TerminalControl.ClaimsChord(key, modifiers));
    }

    // ── Kitty mapping: browser KeyboardEvent.key / .code names ──

    [Theory]
    [InlineData(Key.A, "a", "a")]
    [InlineData(Key.A, "A", "A")]
    [InlineData(Key.A, "\u0001", "a")]          // Ctrl+A: the symbol is a control code
    [InlineData(Key.Z, null, "z")]
    [InlineData(Key.D5, "5", "5")]
    [InlineData(Key.D5, "%", "%")]
    [InlineData(Key.D5, null, "5")]
    [InlineData(Key.NumPad7, "7", "7")]
    [InlineData(Key.OemOpenBrackets, "\u001b", "[")]
    [InlineData(Key.Space, " ", " ")]
    [InlineData(Key.Enter, "\r", "Enter")]
    [InlineData(Key.Tab, "\t", "Tab")]
    [InlineData(Key.Back, "\b", "Backspace")]
    [InlineData(Key.Escape, "\u001b", "Escape")]
    [InlineData(Key.Up, null, "ArrowUp")]
    [InlineData(Key.Down, null, "ArrowDown")]
    [InlineData(Key.Left, null, "ArrowLeft")]
    [InlineData(Key.Right, null, "ArrowRight")]
    [InlineData(Key.Home, null, "Home")]
    [InlineData(Key.PageDown, null, "PageDown")]
    [InlineData(Key.Delete, null, "Delete")]
    [InlineData(Key.F1, null, "F1")]
    [InlineData(Key.F12, null, "F12")]
    [InlineData(Key.F13, null, "F13")]
    [InlineData(Key.LeftShift, null, "Shift")]
    [InlineData(Key.RightCtrl, null, "Control")]
    [InlineData(Key.LeftAlt, null, "Alt")]
    [InlineData(Key.LWin, null, "Meta")]
    [InlineData(Key.ImeProcessed, null, "")]
    public void ToBrowserKey_Maps(Key key, string? symbol, string expected)
        => Assert.Equal(expected, TerminalControl.ToBrowserKey(key, symbol));

    [Theory]
    [InlineData(PhysicalKey.A, "KeyA")]
    [InlineData(PhysicalKey.Z, "KeyZ")]
    [InlineData(PhysicalKey.Digit5, "Digit5")]
    [InlineData(PhysicalKey.NumPad7, "Numpad7")]
    [InlineData(PhysicalKey.NumPadEnter, "NumpadEnter")]
    [InlineData(PhysicalKey.NumPadAdd, "NumpadAdd")]
    [InlineData(PhysicalKey.ShiftLeft, "ShiftLeft")]
    [InlineData(PhysicalKey.ShiftRight, "ShiftRight")]
    [InlineData(PhysicalKey.ControlLeft, "ControlLeft")]
    [InlineData(PhysicalKey.AltRight, "AltRight")]
    [InlineData(PhysicalKey.MetaLeft, "MetaLeft")]
    [InlineData(PhysicalKey.F1, "F1")]
    [InlineData(PhysicalKey.F12, "F12")]
    [InlineData(PhysicalKey.ArrowUp, "ArrowUp")]
    [InlineData(PhysicalKey.ArrowLeft, "ArrowLeft")]
    [InlineData(PhysicalKey.Enter, "Enter")]
    [InlineData(PhysicalKey.Escape, "Escape")]
    [InlineData(PhysicalKey.BracketLeft, "BracketLeft")]
    [InlineData(PhysicalKey.None, "")]
    public void ToBrowserCode_Maps(PhysicalKey key, string expected)
        => Assert.Equal(expected, TerminalControl.ToBrowserCode(key));

    [Theory]
    [InlineData(KeyModifiers.None, "a", true)]
    [InlineData(KeyModifiers.Shift, "A", true)]
    [InlineData(KeyModifiers.Control | KeyModifiers.Alt, "@", true)] // AltGr
    [InlineData(KeyModifiers.Control, "a", false)]
    [InlineData(KeyModifiers.Alt, "a", false)]
    [InlineData(KeyModifiers.None, "\r", false)]
    [InlineData(KeyModifiers.None, "\u001b", false)]
    [InlineData(KeyModifiers.None, null, false)]
    public void IsTextKey_Classifies(KeyModifiers modifiers, string? symbol, bool expected)
        => Assert.Equal(expected, TerminalControl.IsTextKey(modifiers, symbol));
}
