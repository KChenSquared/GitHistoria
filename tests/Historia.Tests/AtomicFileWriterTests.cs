namespace Historia.Tests;

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
    public async Task Concurrent_WriteNow_calls_do_not_corrupt_the_file()
    {
        var path = Path.Combine(_dir, "c.json");
        await using var writer = new AtomicFileWriter(path);

        var payloads = Enumerable.Range(0, 50).Select(i => new string((char)('a' + i % 26), 500)).ToArray();
        await Task.WhenAll(payloads.Select(p => writer.WriteNowAsync(p)));

        var final = await File.ReadAllTextAsync(path);
        Assert.Contains(final, payloads); // exactly one writer's full payload, never a mix
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignore */ }
    }
}
