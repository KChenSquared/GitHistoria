namespace Historia.Tests;
[Trait("tier", "unit")]

public class AtomicFileWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Historia-aw-" + Guid.NewGuid().ToString("N"));

    public AtomicFileWriterTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task WriteNowAsync_persists_content_and_leaves_no_temp_file()
    {
        var path = Path.Combine(_dir, "a.json");
        await using var writer = new AtomicFileWriter(path);

        await writer.WriteNowAsync("{\"x\":1}");

        Assert.Equal("{\"x\":1}", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task QueueWrite_coalesces_rapid_writes_to_the_final_value()
    {
        var path = Path.Combine(_dir, "b.json");
        await using var writer = new AtomicFileWriter(path);

        for (var i = 0; i < 20; i++)
            writer.QueueWrite($"value-{i}");

        await writer.FlushAsync();

        Assert.Equal("value-19", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Failed_write_keeps_the_payload_queued_for_the_next_attempt()
    {
        var path = Path.Combine(_dir, "d.json");
        Directory.CreateDirectory(path); // occupies the target name so the atomic Move fails
        await using var writer = new AtomicFileWriter(path);

        writer.QueueWrite("{\"x\":1}");
        await writer.FlushAsync(); // must not throw, and must not drop the payload on failure

        Directory.Delete(path); // clear the obstruction
        await writer.FlushAsync(); // should now succeed using the still-queued content

        Assert.Equal("{\"x\":1}", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Concurrent_WriteNow_calls_do_not_corrupt_the_file()
    {
        var path = Path.Combine(_dir, "c.json");
        await using var writer = new AtomicFileWriter(path);

        var payloads = Enumerable.Range(0, 50).Select(i => new string((char)('a' + i % 26), 500)).ToArray();
        await Task.WhenAll(payloads.Select(p => writer.WriteNowAsync(p)));

        var final = await File.ReadAllTextAsync(path);
        Assert.Contains(final, payloads); // exactly one writer's full payload, never a mix
    }

    [Fact]
    public async Task An_older_sequence_never_replaces_a_newer_pending_payload()
    {
        var path = Path.Combine(_dir, "seq.json");
        await using var writer = new AtomicFileWriter(path);

        writer.QueueWrite("newest", sequence: 5);
        writer.QueueWrite("older", sequence: 3);
        Assert.Equal("newest", writer.PendingContentForTesting);

        writer.QueueWrite("same number again", sequence: 5);
        Assert.Equal("newest", writer.PendingContentForTesting);

        await writer.FlushAsync();
        Assert.Equal("newest", await File.ReadAllTextAsync(path));

        // Even once the newest payload has reached disk, an older one is still refused.
        writer.QueueWrite("late and stale", sequence: 4);
        Assert.Null(writer.PendingContentForTesting);
        await writer.FlushAsync();
        Assert.Equal("newest", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_newer_sequence_restarts_the_debounce()
    {
        var path = Path.Combine(_dir, "debounce.json");
        await using var writer = new AtomicFileWriter(path);

        writer.QueueWrite("first", sequence: 1);
        await Task.Delay(150);
        var secondQueuedAt = Stopwatch.StartNew();
        writer.QueueWrite("second", sequence: 2);
        await Task.Delay(100);

        // The 250 ms window restarted at the second queue, so the first one's deadline (100 ms
        // ago) has passed without a write. Skipped when a loaded machine stretched the delay.
        if (secondQueuedAt.ElapsedMilliseconds < 200)
            Assert.False(File.Exists(path), "the debounce did not restart");

        await writer.FlushAsync();
        Assert.Equal("second", await File.ReadAllTextAsync(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }
}
