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
}
