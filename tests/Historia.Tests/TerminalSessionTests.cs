namespace Historia.Tests;

/// <summary>Behaviour of a single <see cref="TerminalSession"/> against a fake pty — OSC
/// hardening (V5 §9.3), the UTF-8 decoder (D1), the damage layer (D2), exit handling.</summary>
public class TerminalSessionTests
{
    private static TerminalSession NewSession(out FakePtyConnection pty, int scrollback = 1000)
    {
        pty = new FakePtyConnection();
        var origin = TerminalOrigin.Home("/home/me");
        return new TerminalSession(origin, pty, new ImmediateUiDispatcher(), 80, 24, scrollback);
    }

    [Theory]
    [InlineData("plain title", "plain title")]
    [InlineData("has\ttabs\tand\nnewlines", "hastabsandnewlines")]
    [InlineData("   trimmed   ", "trimmed")]
    public void SanitiseTitle_StripsControlAndTrims(string input, string expected)
        => Assert.Equal(expected, TerminalSession.SanitiseTitle(input));

    [Fact]
    public void SanitiseTitle_TruncatesToSixty()
    {
        var s = TerminalSession.SanitiseTitle(new string('x', 200));
        Assert.Equal(60, s!.Length);
    }

    [Fact]
    public void SanitiseTitle_EmptyBecomesNull()
    {
        Assert.Null(TerminalSession.SanitiseTitle(""));
        Assert.Null(TerminalSession.SanitiseTitle("\x01\x02"));
    }

    [Fact]
    public void Osc0_SetsReportedTitle_Sanitised()
    {
        using var session = NewSession(out var pty);
        string? seen = null;
        session.TitleChanged += (_, _) => seen = session.ReportedTitle;

        pty.EmitOutput("\x1b]0;my-shell\x07");

        Assert.Equal("my-shell", session.ReportedTitle);
        Assert.Equal("my-shell", seen);
    }

    [Fact]
    public void Osc52_ClipboardWrite_IsIgnored_NoThrow()
    {
        using var session = NewSession(out var pty);
        var ex = Record.Exception(() => pty.EmitOutput("\x1b]52;c;aGVsbG8=\x07hello world\r\n"));
        Assert.Null(ex);
    }

    [Fact]
    public void SplitUtf8AcrossChunks_DecodesWithoutMangling()
    {
        using var session = NewSession(out var pty);
        var bytes = System.Text.Encoding.UTF8.GetBytes("héllo"); // 'é' is two bytes
        pty.EmitOutput(bytes[..2]);
        pty.EmitOutput(bytes[2..]);

        var line = session.ReadBuffer(v => v.GetLineText(0));
        Assert.Contains("héllo", line);
    }

    [Fact]
    public void ShellExit_MovesToExitedAndAnnounces()
    {
        using var session = NewSession(out var pty);
        pty.EmitExit(3);

        Assert.Equal(TerminalSessionState.Exited, session.State);
        Assert.Equal(3, session.ExitCode);
        var text = session.ReadBuffer(v =>
            string.Join("\n", Enumerable.Range(0, v.TotalLines).Select(v.GetLineText)));
        Assert.Contains("process exited with code 3", text);
    }

    [Fact]
    public void DirtyRows_ReportedAfterOutput_ThenCleared()
    {
        using var session = NewSession(out var pty);
        pty.EmitOutput("hello\r\nworld\r\n");

        Assert.NotEmpty(session.TakeDirtyRows());
        Assert.Empty(session.TakeDirtyRows()); // cleared by the take
    }

    [Fact]
    public void Resize_ForwardsToPty()
    {
        using var session = NewSession(out var pty);
        session.Resize(100, 40);
        Assert.Contains((100, 40), pty.Resizes);
    }
}
