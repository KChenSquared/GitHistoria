using System.IO;
using System.Text.Json;
using Historia.Services.Settings;
using Historia.Services.Storage;

namespace Historia.Services.Workspace;

/// <inheritdoc cref="IRecentRepositoryService"/>
public sealed class RecentRepositoryService : IRecentRepositoryService, IAsyncDisposable
{
    private sealed record RecentEntry(string Path, string DisplayName, DateTimeOffset LastOpenedAt);

    private sealed record RecentFile
    {
        public int SchemaVersion { get; init; } = 1;
        public List<RecentEntry> Items { get; init; } = [];
    }

    private readonly ISettingsService _settings;
    private readonly AtomicFileWriter _writer;
    private readonly object _sync = new();
    private List<RecentEntry> _entries;

    public event EventHandler? Changed;

    public RecentRepositoryService(ISettingsService settings)
    {
        _settings = settings;
        _writer = new AtomicFileWriter(AppPaths.RecentRepositoriesFile);
        _entries = Load();
    }

    public IReadOnlyList<RecentRepository> Items
    {
        get
        {
            lock (_sync)
            {
                return _entries
                    .OrderByDescending(e => e.LastOpenedAt)
                    .Select(e => new RecentRepository
                    {
                        Path = e.Path,
                        DisplayName = e.DisplayName,
                        LastOpenedAt = e.LastOpenedAt,
                        IsStale = !SafeExists(e.Path),
                    })
                    .ToList();
            }
        }
    }

    public Task AddAsync(string path, string displayName, CancellationToken ct = default)
    {
        var normalized = Normalize(path);
        var max = Math.Max(1, _settings.Current.MaxRecentRepositories);

        lock (_sync)
        {
            _entries.RemoveAll(e => string.Equals(Normalize(e.Path), normalized, StringComparison.OrdinalIgnoreCase));
            _entries.Insert(0, new RecentEntry(path, displayName, DateTimeOffset.Now));
            if (_entries.Count > max)
                _entries = _entries.OrderByDescending(e => e.LastOpenedAt).Take(max).ToList();
        }

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string path, CancellationToken ct = default)
    {
        var normalized = Normalize(path);
        lock (_sync)
            _entries.RemoveAll(e => string.Equals(Normalize(e.Path), normalized, StringComparison.OrdinalIgnoreCase));

        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        lock (_sync)
            _entries.Clear();
        Persist();
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private List<RecentEntry> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.RecentRepositoriesFile))
                return [];
            var json = File.ReadAllText(AppPaths.RecentRepositoriesFile);
            var file = JsonSerializer.Deserialize<RecentFile>(json, HistoriaJson.Options);
            return file?.Items ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Persist()
    {
        RecentFile file;
        lock (_sync)
            file = new RecentFile { Items = _entries.ToList() };
        _writer.QueueWrite(JsonSerializer.Serialize(file, HistoriaJson.Options));
    }

    private static bool SafeExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }

    private static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path; }
    }

    public ValueTask DisposeAsync() => _writer.DisposeAsync();
}
