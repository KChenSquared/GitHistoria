namespace Historia.Tests;
[Trait("tier", "unit")]
[Collection("serial")]
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
    /// now holds the file open for the whole session (no more open/close per line),
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

    public static TheoryData<Exception> Cancellations() =>
    [
        new OperationCanceledException(),
        new TaskCanceledException(),
    ];

    [Theory]
    [MemberData(nameof(Cancellations))]
    public void Operation_Fail_with_a_cancellation_records_cancelled(Exception ex)
    {
        AppLog.Enabled = true;
        using (var op = AppLog.Operation("git", "GetStatusAsync"))
            op.Fail(ex);

        var text = ReadSession();
        Assert.Contains("(cancelled, ", text);
        Assert.DoesNotContain("FAILED", text);
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
        // every previous version reopened, wrote, and closed the file on every single
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
    public void Flush_puts_every_written_line_in_the_live_session_file()
    {
        AppLog.Enabled = true;
        for (var i = 0; i < 200; i++)
            AppLog.Write(AppLogLevel.Info, "test", $"line {i}");

        Assert.True(AppLog.Flush(TimeSpan.FromSeconds(10)));
        // Still enabled: the session holds the file open, so read with a sharing reader.
        var lines = QueuedLineWriterTests.ReadShared(AppLog.CurrentLogPath!);
        var text = string.Join(Environment.NewLine, lines);
        Assert.Contains("line 0", text);
        Assert.Contains("line 199", text);
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

    [Fact]
    public async Task Sessions_are_isolated_between_concurrent_flows()
    {
        using var barrier = new Barrier(2);
        string? session1File = null;
        string? session2File = null;

        using var dir1 = new StateDir();
        using var dir2 = new StateDir();

        var t1 = Task.Run(() =>
        {
            AppPaths.OverrideForTesting(dir1.Path);
            using var session1 = AppLog.StartSessionForTesting();
            session1File = session1.SessionPath;
            barrier.SignalAndWait();
            AppLog.Write(AppLogLevel.Info, "flow1", "message from flow 1");
        });

        var t2 = Task.Run(() =>
        {
            AppPaths.OverrideForTesting(dir2.Path);
            using var session2 = AppLog.StartSessionForTesting();
            session2File = session2.SessionPath;
            barrier.SignalAndWait();
            AppLog.Write(AppLogLevel.Info, "flow2", "message from flow 2");
        });

        await Task.WhenAll(t1, t2);

        Assert.NotNull(session1File);
        Assert.NotNull(session2File);
        Assert.NotEqual(session1File, session2File);

        var text1 = File.ReadAllText(session1File);
        var text2 = File.ReadAllText(session2File);

        Assert.Contains("message from flow 1", text1);
        Assert.DoesNotContain("message from flow 2", text1);

        Assert.Contains("message from flow 2", text2);
        Assert.DoesNotContain("message from flow 1", text2);
    }

    [Fact]
    public void A_disposed_session_hands_the_logger_back_to_the_global_writer()
    {
        string sessionPath;
        using (var session = AppLog.StartSessionForTesting(_dir.Path))
        {
            sessionPath = session.SessionPath;
        }

        AppLog.Enabled = true;
        try
        {
            AppLog.Write(AppLogLevel.Info, "global", "after session disposal");

            var globalPath = AppLog.CurrentLogPath;
            Assert.NotNull(globalPath);
            Assert.NotEqual(sessionPath, globalPath);

            AppLog.Enabled = false;
            var text = File.ReadAllText(globalPath);
            Assert.Contains("after session disposal", text);
        }
        finally
        {
            AppLog.Enabled = false;
        }
    }

    [Fact]
    public void Enabling_after_a_disposed_session_opens_a_real_session_file()
    {
        using (AppLog.StartSessionForTesting(_dir.Path)) { }

        AppLog.Enabled = true;
        try
        {
            var path = AppLog.CurrentLogPath;
            Assert.NotNull(path);
            Assert.True(File.Exists(path));
        }
        finally
        {
            AppLog.Enabled = false;
        }
    }

    [Fact]
    public void OptInFlag_precedence_env_var_and_flag_file()
    {
        var envVar = "HISTORIA_TEST_FLAG_" + Guid.NewGuid().ToString("N");
        var flagFile = "test-flag-" + Guid.NewGuid().ToString("N") + ".on";
        var flagPath = Path.Combine(AppPaths.MachineDirectory, flagFile);

        try
        {
            var flag = new OptInFlag(envVar, flagFile);
            Assert.False(flag.IsOptedIn);
            Assert.False(flag.Enabled);

            // Flag file enables it
            File.WriteAllText(flagPath, "");
            Assert.True(flag.FlagFileExists());
            Assert.True(flag.IsOptedIn);

            File.Delete(flagPath);
            Assert.False(flag.FlagFileExists());

            // Env var enables it upon construction
            Environment.SetEnvironmentVariable(envVar, "1");
            var envFlag = new OptInFlag(envVar, flagFile);
            Assert.True(envFlag.EnvOptIn());
            Assert.True(envFlag.IsOptedIn);
            Assert.True(envFlag.Enabled);

            // Test override wins over env var
            envFlag.OverrideForTesting(false);
            Assert.False(envFlag.Enabled);

            envFlag.OverrideForTesting(true);
            Assert.True(envFlag.Enabled);

            envFlag.OverrideForTesting(null);
            Assert.True(envFlag.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
            try { if (File.Exists(flagPath)) File.Delete(flagPath); } catch { }
        }
    }
}
