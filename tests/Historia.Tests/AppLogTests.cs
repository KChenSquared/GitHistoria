using Historia.Diagnostics;

namespace Historia.Tests;
[Trait("tier", "unit")]

public sealed class AppLogTests : IDisposable
{
    private readonly StateDir _dir = new();

    public AppLogTests()
    {
        AppLog.Enabled = false;
        AppLog.ResetForTesting();
    }

    public void Dispose()
    {
        AppLog.Enabled = false;
        AppLog.ResetForTesting();
        _dir.Dispose();
    }

    private string[] SessionFiles() =>
        Directory.Exists(AppPaths.LogsDirectory)
            ? Directory.GetFiles(AppPaths.LogsDirectory, "dev-*.log")
            : [];

    /// <summary>Reads the session file's content. Disables logging first — the session writer
    /// now holds the file open for the whole session (§findings: no more open/close per line),
    /// so the handle must be closed before a second reader can open the same file.</summary>
    private string ReadSession()
    {
        AppLog.Enabled = false;
        return File.ReadAllText(SessionFiles().Single());
    }

    [Fact]
    public void Disabled_writes_nothing()
    {
        AppLog.Write(AppLogLevel.Error, "test", "should not appear");
        using (AppLog.Operation("test", "op")) { }

        Assert.Empty(SessionFiles());
        Assert.Null(AppLog.CurrentLogPath);
    }

    [Fact]
    public void Enabling_creates_one_session_file_with_a_header()
    {
        AppLog.Enabled = true;

        var file = Assert.Single(SessionFiles());
        Assert.Equal(file, AppLog.CurrentLogPath);
        AppLog.Enabled = false; // close the session writer before reopening the file for read
        var text = File.ReadAllText(file);
        Assert.Contains("# Historia developer log", text);
        Assert.Contains("logging started", text);
    }

    [Fact]
    public void Write_formats_level_category_and_message()
    {
        AppLog.Enabled = true;
        AppLog.Write(AppLogLevel.Info, "git", "checkout main");

        Assert.Matches(@"\d\d:\d\d:\d\d\.\d\d\d  INFO   \[git\]  checkout main", ReadSession());
    }

    [Fact]
    public void Operation_logs_enter_and_exit_with_a_duration_and_outcome()
    {
        AppLog.Enabled = true;
        using (var op = AppLog.Operation("git", "CommitAsync"))
            op.Outcome("abc1234567");

        var text = ReadSession();
        Assert.Contains("-> CommitAsync", text);
        Assert.Matches(@"<- CommitAsync  \(abc1234567, \d+(\.\d+)?ms\)", text);
    }

    [Fact]
    public void Operation_Fail_records_the_exception_on_the_exit_line()
    {
        AppLog.Enabled = true;
        using (var op = AppLog.Operation("git", "PushAsync"))
            op.Fail(new InvalidOperationException("no upstream"));

        Assert.Contains("FAILED: InvalidOperationException: no upstream", ReadSession());
    }

    [Fact]
    public void Write_with_exception_renders_a_type_message_and_stack_block()
    {
        AppLog.Enabled = true;
        Exception caught;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception ex) { caught = ex; }

        AppLog.Write(AppLogLevel.Error, "unhandled", "crash", caught);

        var text = ReadSession();
        Assert.Contains("System.InvalidOperationException: boom", text);
        Assert.Contains("   at ", text);
    }

    [Fact]
    public void Credentials_in_urls_are_redacted()
    {
        AppLog.Enabled = true;
        AppLog.Write(AppLogLevel.Info, "git-cli", "git push https://alice:s3cr3t@example.com/r.git");

        var text = ReadSession();
        Assert.DoesNotContain("s3cr3t", text);
        Assert.Contains("://<redacted>@example.com", text);
    }

    [Fact]
    public void Starting_a_session_prunes_all_but_the_ten_newest()
    {
        Directory.CreateDirectory(AppPaths.LogsDirectory);
        for (var i = 0; i < 14; i++)
            File.WriteAllText(Path.Combine(AppPaths.LogsDirectory, $"dev-2020010{i:D2}-000000.log"), "old");

        AppLog.Enabled = true;

        // 10 retained + the one just opened.
        Assert.Equal(11, SessionFiles().Length);
        Assert.False(File.Exists(Path.Combine(AppPaths.LogsDirectory, "dev-20200100-000000.log")));
    }

    [Fact]
    public void The_session_file_stays_open_for_writing_across_multiple_entries()
    {
        // §findings: every previous version reopened, wrote, and closed the file on every single
        // entry — no handle was ever held between calls. Now one handle spans the whole session:
        // a second attempt to open the same file for writing must be rejected while it's live.
        AppLog.Enabled = true;
        AppLog.Write(AppLogLevel.Info, "test", "one");

        var file = AppLog.CurrentLogPath!;
        Assert.Throws<IOException>(() => File.Open(file, FileMode.Open, FileAccess.Write, FileShare.None));

        // The held-open handle keeps working for later entries too.
        AppLog.Write(AppLogLevel.Info, "test", "two");
        Assert.Contains("two", ReadSession());
    }

    [Fact]
    public void Disabling_stops_writes_and_clears_the_current_path()
    {
        AppLog.Enabled = true;
        var file = AppLog.CurrentLogPath!;
        AppLog.Enabled = false;

        AppLog.Write(AppLogLevel.Error, "test", "after disable");

        Assert.Null(AppLog.CurrentLogPath);
        Assert.DoesNotContain("after disable", File.ReadAllText(file));
    }
}
