namespace Historia.Tests;

[Trait("tier", "unit")]
public sealed class LogRetentionTests : IDisposable
{
    private readonly string _dir = TestScratch.NewDirectory("log-retention-");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void TrimDevLogs_deletes_stale_keeps_fresh_active_and_non_dev_files()
    {
        var now = DateTime.Now;
        var cutoff = now.AddHours(-48);

        var stale = Path.Combine(_dir, "dev-stale.log");
        var fresh = Path.Combine(_dir, "dev-fresh.log");
        var active = Path.Combine(_dir, "dev-active.log");
        var other = Path.Combine(_dir, "audit-stale.log");

        File.WriteAllText(stale, "stale");
        File.SetLastWriteTime(stale, cutoff.AddHours(-1));

        File.WriteAllText(fresh, "fresh");
        File.SetLastWriteTime(fresh, cutoff.AddHours(1));

        File.WriteAllText(active, "active");
        File.SetLastWriteTime(active, cutoff.AddHours(-1));

        File.WriteAllText(other, "other");
        File.SetLastWriteTime(other, cutoff.AddHours(-1));

        var (found, removed) = LogRetention.TrimDevLogs(_dir, cutoff, active);

        Assert.Equal(1, found);
        Assert.Equal(1, removed);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(active));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void TrimDevLogs_throws_when_stale_file_is_locked()
    {
        var cutoff = DateTime.Now.AddHours(-48);
        var stale = Path.Combine(_dir, "dev-locked.log");

        File.WriteAllText(stale, "locked stale");
        File.SetLastWriteTime(stale, cutoff.AddHours(-1));

        using var stream = File.Open(stale, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => LogRetention.TrimDevLogs(_dir, cutoff, null));
    }

    [Fact]
    public void TrimPerfFile_deletes_whole_file_when_mtime_older_than_cutoff()
    {
        var cutoff = DateTime.Now.AddHours(-48);
        var path = Path.Combine(_dir, "perf.log");

        File.WriteAllText(path, "old perf lines");
        File.SetLastWriteTime(path, cutoff.AddHours(-2));

        var modified = LogRetention.TrimPerfFile(path, cutoff);

        Assert.True(modified);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TrimPerfFile_rewrites_mixed_age_file_keeping_only_newer_lines()
    {
        var cutoff = new DateTime(2026, 10, 6, 12, 0, 0);
        var path = Path.Combine(_dir, "perf.log");

        var lines = new[]
        {
            "2026-10-05 10:00:00.000  +      10.0ms  old line",
            "   continuation of old line",
            "2026-10-07 10:00:00.000  +      20.0ms  fresh line 1",
            "   continuation of fresh line",
            "2026-10-05 11:00:00.000  +      30.0ms  old line 2",
            "2026-10-07 11:00:00.000  +      40.0ms  fresh line 2",
        };
        File.WriteAllLines(path, lines);
        File.SetLastWriteTime(path, DateTime.Now);

        var modified = LogRetention.TrimPerfFile(path, cutoff);

        Assert.True(modified);
        var result = File.ReadAllLines(path);
        Assert.Equal(
        [
            "2026-10-07 10:00:00.000  +      20.0ms  fresh line 1",
            "   continuation of fresh line",
            "2026-10-07 11:00:00.000  +      40.0ms  fresh line 2",
        ], result);
    }

    [Fact]
    public void TrimPerfFile_drops_legacy_undated_head()
    {
        var cutoff = new DateTime(2026, 10, 6, 12, 0, 0);
        var path = Path.Combine(_dir, "perf.log");

        var lines = new[]
        {
            "10:00:00.000  +      10.0ms  legacy undated head",
            "10:00:01.000  +      11.0ms  legacy undated head 2",
            "2026-10-07 12:00:00.000  +      20.0ms  new dated line",
        };
        File.WriteAllLines(path, lines);
        File.SetLastWriteTime(path, DateTime.Now);

        var modified = LogRetention.TrimPerfFile(path, cutoff);

        Assert.True(modified);
        var result = File.ReadAllLines(path);
        Assert.Equal(["2026-10-07 12:00:00.000  +      20.0ms  new dated line"], result);
    }

    [Fact]
    public void TrimPerfFile_leaves_fully_fresh_file_byte_identical()
    {
        var cutoff = new DateTime(2026, 10, 6, 12, 0, 0);
        var path = Path.Combine(_dir, "perf.log");

        var lines = new[]
        {
            "2026-10-07 10:00:00.000  +      20.0ms  fresh line 1",
            "2026-10-07 11:00:00.000  +      30.0ms  fresh line 2",
        };
        File.WriteAllLines(path, lines);
        var originalBytes = File.ReadAllBytes(path);

        var modified = LogRetention.TrimPerfFile(path, cutoff);

        Assert.False(modified);
        Assert.Equal(originalBytes, File.ReadAllBytes(path));
    }
}
