namespace Historia.Services.Settings;

/// <inheritdoc cref="IRepoLayoutService"/>
public sealed class RepoLayoutService : IRepoLayoutService, IAsyncDisposable
{
    private sealed class LayoutFile
    {
        public int SchemaVersion { get; set; } = 2;

        /// <summary>§V3.1: column order + visibility + flex are application-wide. Only visibility
        /// (and the flex column) survive a launch; the order resets to canonical (§V3-B / D2).</summary>
        public GlobalDto? Global { get; set; }

        public Dictionary<string, RepoLayoutDto> Repos { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class GlobalDto
    {
        public List<GlobalColumnDto> Columns { get; set; } = [];
    }

    private sealed class GlobalColumnDto
    {
        public GraphColumnKind Kind { get; set; }
        public bool Visible { get; set; }
        public bool IsFlex { get; set; }
    }

    private sealed class RepoLayoutDto
    {
        /// <summary>Schema v1 only — a per-repo column list. Ignored on read in v2 (order and
        /// visibility moved to <see cref="LayoutFile.Global"/>); never written by v2.</summary>
        public List<GraphColumn> Columns { get; set; } = [];
        public bool GraphWidthPinned { get; set; }
        public bool SshOnly { get; set; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, RepoLayout> _byKey = new(StringComparer.Ordinal);
    private readonly AtomicFileWriter _writer;

    /// <summary>Global column order + visibility + flex. Widths here are the canonical defaults;
    /// per-repo widths are merged in by <see cref="Get"/>. Order resets to canonical each launch.</summary>
    private IReadOnlyList<GraphColumn> _global = GraphColumnDefaults.All;

    public event EventHandler<RepoLayoutChangedEventArgs>? Changed;

    public RepoLayoutService() : this(new AtomicFileWriter(AppPaths.RepoLayoutFile)) { }

    internal RepoLayoutService(AtomicFileWriter writer)
    {
        _writer = writer;
        Load();
    }

    public IReadOnlyList<GraphColumn> GlobalColumns
    {
        get { lock (_sync) return _global; }
    }

    public RepoLayout Get(string repoPath)
    {
        var key = IRepoLayoutService.KeyFor(repoPath);
        lock (_sync)
        {
            var entry = _byKey.GetValueOrDefault(key);
            return new RepoLayout
            {
                Columns = ComposeColumns(entry),
                GraphWidthPinned = entry?.GraphWidthPinned ?? false,
                SshOnly = entry?.SshOnly ?? false,
            };
        }
    }

    public Task UpdateAsync(string repoPath, Func<RepoLayout, RepoLayout> mutate)
    {
        var key = IRepoLayoutService.KeyFor(repoPath);
        RepoLayout next;
        bool globalChanged;
        IReadOnlyList<GraphColumn> globalSnapshot;
        lock (_sync)
        {
            var entry = _byKey.GetValueOrDefault(key);
            var current = new RepoLayout
            {
                Columns = ComposeColumns(entry),
                GraphWidthPinned = entry?.GraphWidthPinned ?? false,
                SshOnly = entry?.SshOnly ?? false,
            };

            var mutated = mutate(current);

            // Split the write: order + visibility + flex go to the global set; widths, the pin
            // and SshOnly stay on this repo's entry.
            var newGlobal = StripToShape(GraphColumnDefaults.Normalize(mutated.Columns));
            globalChanged = !SameShape(newGlobal, _global);
            if (globalChanged)
                _global = newGlobal;

            next = new RepoLayout
            {
                Columns = MergeWidths(_global, mutated.Columns),
                GraphWidthPinned = mutated.GraphWidthPinned,
                SshOnly = mutated.SshOnly,
            };
            _byKey[key] = next;
            globalSnapshot = _global;
            Persist();
        }

        Changed?.Invoke(this, new RepoLayoutChangedEventArgs(key, next));
        if (globalChanged)
            Changed?.Invoke(this, new RepoLayoutChangedEventArgs(
                IRepoLayoutService.GlobalKey, new RepoLayout { Columns = globalSnapshot }));
        return Task.CompletedTask;
    }

    public Task UpdateGlobalAsync(Func<IReadOnlyList<GraphColumn>, IReadOnlyList<GraphColumn>> mutate)
    {
        IReadOnlyList<GraphColumn> snapshot;
        lock (_sync)
        {
            var proposed = StripToShape(GraphColumnDefaults.Normalize(mutate(_global)));
            if (SameShape(proposed, _global))
                return Task.CompletedTask;
            _global = proposed;
            snapshot = _global;
            Persist();
        }
        Changed?.Invoke(this, new RepoLayoutChangedEventArgs(
            IRepoLayoutService.GlobalKey, new RepoLayout { Columns = snapshot }));
        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct = default) => _writer.FlushAsync(ct);

    public ValueTask DisposeAsync() => _writer.DisposeAsync();

    // ── composition helpers ──

    /// <summary>The global order/visibility with <paramref name="entry"/>'s per-repo widths
    /// merged in (canonical defaults where the repo has never sized a column).</summary>
    private IReadOnlyList<GraphColumn> ComposeColumns(RepoLayout? entry)
        => MergeWidths(_global, entry?.Columns);

    private static IReadOnlyList<GraphColumn> MergeWidths(
        IReadOnlyList<GraphColumn> shape, IReadOnlyList<GraphColumn>? widths)
    {
        var byKind = widths?.ToDictionary(c => c.Kind);
        return shape.Select(g => g with
        {
            Width = byKind is not null && byKind.TryGetValue(g.Kind, out var w)
                ? w.Width
                : DefaultWidth(g.Kind),
        }).ToList();
    }

    private static List<GraphColumn> StripToShape(IReadOnlyList<GraphColumn> cols)
        => cols.Select(c => new GraphColumn
        {
            Kind = c.Kind,
            Width = DefaultWidth(c.Kind),
            Visible = c.Visible,
            IsFlex = c.IsFlex,
        }).ToList();

    private static bool SameShape(IReadOnlyList<GraphColumn> a, IReadOnlyList<GraphColumn> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i].Kind != b[i].Kind || a[i].Visible != b[i].Visible || a[i].IsFlex != b[i].IsFlex)
                return false;
        return true;
    }

    private static double DefaultWidth(GraphColumnKind kind)
        => GraphColumnDefaults.All.First(c => c.Kind == kind).Width;

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

            // §V3-B / D2: the order resets to canonical every launch; only visibility (and the
            // flex column) persist. A v1 file (no `global`) migrates to the defaults here.
            if (file.Global?.Columns is { Count: > 0 } gc)
            {
                var byKind = gc.ToDictionary(c => c.Kind);
                var merged = GraphColumnDefaults.All.Select(def =>
                    byKind.TryGetValue(def.Kind, out var p)
                        ? def with { Visible = p.Visible, IsFlex = p.IsFlex }
                        : def).ToList();
                _global = StripToShape(GraphColumnDefaults.Normalize(merged));
            }

            foreach (var (k, dto) in file.Repos)
            {
                // Widths and the pin reset every launch; only SshOnly is carried over.
                _byKey[k] = new RepoLayout
                {
                    Columns = _global,
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
            SchemaVersion = 2,
            Global = new GlobalDto
            {
                Columns = _global.Select(c => new GlobalColumnDto
                {
                    Kind = c.Kind,
                    Visible = c.Visible,
                    IsFlex = c.IsFlex,
                }).ToList(),
            },
            Repos = _byKey.ToDictionary(
                kv => kv.Key,
                kv => new RepoLayoutDto { SshOnly = kv.Value.SshOnly },
                StringComparer.Ordinal),
        };
        _writer.QueueWrite(JsonSerializer.Serialize(file, HistoriaJson.Options));
    }
}
