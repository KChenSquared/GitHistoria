namespace Historia.Services.Settings;

/// <inheritdoc cref="IRepoLayoutService"/>
public sealed class RepoLayoutService : IRepoLayoutService, IAsyncDisposable
{
    private sealed class LayoutFile
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, RepoLayoutDto> Repos { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class RepoLayoutDto
    {
        public List<GraphColumn> Columns { get; set; } = [];
        public bool GraphWidthPinned { get; set; }
        public bool SshOnly { get; set; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, RepoLayout> _byKey = new(StringComparer.Ordinal);
    private readonly AtomicFileWriter _writer;

    public event EventHandler<RepoLayoutChangedEventArgs>? Changed;

    public RepoLayoutService() : this(new AtomicFileWriter(AppPaths.RepoLayoutFile)) { }

    internal RepoLayoutService(AtomicFileWriter writer)
    {
        _writer = writer;
        Load();
    }

    public RepoLayout Get(string repoPath)
    {
        var key = IRepoLayoutService.KeyFor(repoPath);
        lock (_sync)
            return _byKey.TryGetValue(key, out var layout) ? layout : new RepoLayout();
    }

    public Task UpdateAsync(string repoPath, Func<RepoLayout, RepoLayout> mutate)
    {
        var key = IRepoLayoutService.KeyFor(repoPath);
        RepoLayout next;
        lock (_sync)
        {
            var current = _byKey.TryGetValue(key, out var l) ? l : new RepoLayout();
            next = Sanitize(mutate(current));
            _byKey[key] = next;
            Persist();
        }

        Changed?.Invoke(this, new RepoLayoutChangedEventArgs(key, next));
        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct = default) => _writer.FlushAsync(ct);

    public ValueTask DisposeAsync() => _writer.DisposeAsync();

    // ── persistence ──

    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.RepoLayoutFile))
                return;
            var file = JsonSerializer.Deserialize<LayoutFile>(File.ReadAllText(AppPaths.RepoLayoutFile), HistoriaJson.Options);
            if (file is null)
                return;

            foreach (var (k, dto) in file.Repos)
            {
                // §V3-B: order + widths + the width pin reset every launch.
                _byKey[k] = new RepoLayout
                {
                    Columns = GraphColumnDefaults.ResetLayout(GraphColumnDefaults.Normalize(dto.Columns)),
                    GraphWidthPinned = false,
                    SshOnly = dto.SshOnly,
                };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Start empty on a corrupt file.
        }
    }

    private void Persist()
    {
        var file = new LayoutFile
        {
            Repos = _byKey.ToDictionary(
                kv => kv.Key,
                kv => new RepoLayoutDto
                {
                    Columns = kv.Value.Columns.ToList(),
                    GraphWidthPinned = kv.Value.GraphWidthPinned,
                    SshOnly = kv.Value.SshOnly,
                },
                StringComparer.Ordinal),
        };
        _writer.QueueWrite(JsonSerializer.Serialize(file, HistoriaJson.Options));
    }

    private static RepoLayout Sanitize(RepoLayout l) => l with
    {
        Columns = GraphColumnDefaults.Normalize(l.Columns),
    };
}
