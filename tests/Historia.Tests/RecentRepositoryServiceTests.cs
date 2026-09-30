namespace Historia.Tests;

[Trait("tier", "unit")]
public sealed class RecentRepositoryServiceTests : IAsyncDisposable
{
    private readonly StateDir _state = new();
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _state.Dispose();
    }

    [Fact]
    public async Task Racing_adds_leave_the_newest_list_pending()
    {
        const int rounds = 1_000;
        var svc = new RecentRepositoryService(_host.Settings);
        var writer = typeof(RecentRepositoryService).GetField("_writer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(svc)!;
        var pendingOf = typeof(AtomicFileWriter).GetProperty("PendingContentForTesting", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var pathA = Path.Combine(_state.Path, "repo-a");
        var pathB = Path.Combine(_state.Path, "repo-b");

        var inversions = 0;
        using var barrier = new Barrier(2);
        for (var r = 0; r < rounds; r++)
        {
            async Task Add(string path)
            {
                barrier.SignalAndWait();
                await svc.AddAsync(path, Path.GetFileName(path));
            }
            await Task.WhenAll(Task.Run(() => Add(pathA)), Task.Run(() => Add(pathB)));

            // A debounce that fired between rounds already put a snapshot on disk; nothing to compare.
            if (pendingOf.GetValue(writer) is not string pending)
                continue;
            using var doc = JsonDocument.Parse(pending);
            var queued = doc.RootElement.GetProperty("items").EnumerateArray()
                .Select(e => e.GetProperty("path").GetString()).ToList();
            if (!queued.SequenceEqual(svc.Items.Select(i => i.Path)))
                inversions++;
        }

        Assert.Equal(0, inversions);
    }
}
