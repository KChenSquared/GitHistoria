namespace Historia.Tests;
[Trait("tier", "unit")]

public sealed class PerfLogTests : IDisposable
{
    public void Dispose()
    {
        PerfLog.OverrideEnabledForTesting(null);
    }

    [Fact]
    public async Task Enabled_is_isolated_between_concurrent_flows()
    {
        using var barrier = new Barrier(2);
        bool? flow1Observed = null;
        bool? flow2Observed = null;

        var t1 = Task.Run(() =>
        {
            PerfLog.OverrideEnabledForTesting(true);
            barrier.SignalAndWait();
            flow1Observed = PerfLog.Enabled;
        });

        var t2 = Task.Run(() =>
        {
            PerfLog.OverrideEnabledForTesting(false);
            barrier.SignalAndWait();
            flow2Observed = PerfLog.Enabled;
        });

        await Task.WhenAll(t1, t2);

        Assert.True(flow1Observed);
        Assert.False(flow2Observed);
    }

    [Fact]
    public void Scope_creates_timer_only_when_enabled()
    {
        PerfLog.OverrideEnabledForTesting(false);
        using var disabledScope = PerfLog.Scope("test-disabled");

        PerfLog.OverrideEnabledForTesting(true);
        using var enabledScope = PerfLog.Scope("test-enabled");
        Assert.NotNull(enabledScope);
    }
}

/// <summary>Swaps the process-wide perf-log writer, so it runs alone.</summary>
[Trait("tier", "unit")]
[Collection("serial")]
public sealed class PerfLogWriterTests : IDisposable
{
    private readonly StateDir _state = new();

    public void Dispose()
    {
        PerfLog.OverrideEnabledForTesting(null);
        _state.Dispose();
    }

    [Fact]
    public void Marks_reach_perf_log_once_flushed()
    {
        PerfLog.OverrideEnabledForTesting(true);
        PerfLog.Mark("perf-log-writer-test: one");
        PerfLog.Mark("perf-log-writer-test: two");

        Assert.True(PerfLog.Flush(TimeSpan.FromSeconds(10)));
        var lines = File.ReadAllLines(PerfLog.LogPath);
        Assert.Contains(lines, l => l.EndsWith("perf-log-writer-test: one", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith("perf-log-writer-test: two", StringComparison.Ordinal));
    }

    [Fact]
    public void Mark_line_starts_with_full_date()
    {
        PerfLog.OverrideEnabledForTesting(true);
        PerfLog.Mark("date test");
        Assert.True(PerfLog.Flush(TimeSpan.FromSeconds(10)));
        var lines = File.ReadAllLines(PerfLog.LogPath);
        var line = Assert.Single(lines, l => l.EndsWith("date test", StringComparison.Ordinal));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}  \+", line);
    }

    [Fact]
    public void A_mark_never_waits_on_a_stalled_write()
    {
        PerfLog.OverrideEnabledForTesting(true);
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var stalled = QueuedLineWriter.OpenPerBatch("test-perf-writer",
            p => new QueuedLineWriterTests.StallingStream(QueuedLineWriterTests.Append(p), release, entered));
        var previous = PerfLog.SwapWriterForTesting(stalled);
        try
        {
            PerfLog.Mark("first");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "the writer thread never reached the file");

            var sw = Stopwatch.StartNew();
            for (var i = 0; i < 200; i++)
                PerfLog.Mark($"behind a stalled write {i}");
            Assert.True(sw.ElapsedMilliseconds < 50, $"a mark waited on the stalled write: {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            release.Set();
            PerfLog.SwapWriterForTesting(previous);
            stalled.Dispose();
        }

        Assert.Equal(201, QueuedLineWriterTests.ReadShared(PerfLog.LogPath).Length);
    }
}
