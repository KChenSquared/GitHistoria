namespace Historia.Tests;

/// <summary>In-memory <see cref="ISettingsService"/> — no file, no debounce.</summary>
public sealed class FakeSettingsService(AppSettings? initial = null) : ISettingsService
{
    public AppSettings Current { get; private set; } = initial ?? new AppSettings();

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
    {
        var old = Current;
        Current = mutate(old);
        Changed?.Invoke(this, new SettingsChangedEventArgs(old, Current));
        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Runs everything inline on the calling thread; the coalesce timer fires its tick
/// synchronously on <see cref="IUiTimer.Start"/>. Deterministic for terminal-session tests.</summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;
    public void Post(Action action) => action();
    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new ImmediateTimer(tick);

    private sealed class ImmediateTimer(Action tick) : IUiTimer
    {
        public void Start() => tick();
        public void Stop() { }
        public void Dispose() { }
    }
}

/// <summary>A scripted <see cref="IPtyConnection"/>: records writes/resizes, replays bytes on
/// demand, and exits with a chosen code. Drives every terminal test (V5 §3.1).</summary>
public sealed class FakePtyConnection : IPtyConnection
{
    public readonly List<byte[]> Writes = [];
    public readonly List<(int Cols, int Rows)> Resizes = [];
    public int KillCount { get; private set; }
    public bool Disposed { get; private set; }

    public event Action<ReadOnlyMemory<byte>>? OutputReceived;
    public event Action<int>? Exited;

    public void Write(ReadOnlySpan<byte> data) => Writes.Add(data.ToArray());
    public void Resize(int columns, int rows) => Resizes.Add((columns, rows));
    public void Kill() => KillCount++;
    public void Dispose() => Disposed = true;

    public void EmitOutput(string text) => OutputReceived?.Invoke(System.Text.Encoding.UTF8.GetBytes(text));
    public void EmitOutput(byte[] bytes) => OutputReceived?.Invoke(bytes);
    public void EmitExit(int code) => Exited?.Invoke(code);

    public string AllWrittenText() => System.Text.Encoding.UTF8.GetString(Writes.SelectMany(w => w).ToArray());
}

/// <summary>Hands out <see cref="FakePtyConnection"/>s and records the <see cref="PtyStartInfo"/>
/// it was asked for. Never touches a real pseudoterminal.</summary>
public sealed class FakeTerminalBackend : ITerminalBackend
{
    public readonly List<PtyStartInfo> Starts = [];
    public readonly List<FakePtyConnection> Connections = [];
    public int StartDelayMs { get; set; }
    public Func<PtyStartInfo, Exception?>? FailWith { get; set; }

    public async Task<IPtyConnection> StartAsync(PtyStartInfo info, CancellationToken ct)
    {
        if (StartDelayMs > 0)
            await Task.Delay(StartDelayMs, ct).ConfigureAwait(false);

        if (FailWith?.Invoke(info) is { } ex)
            throw ex;

        Starts.Add(info);
        var conn = new FakePtyConnection();
        Connections.Add(conn);
        return conn;
    }
}

/// <summary>In-memory credential vault for tests — never touches Windows Credential Manager.</summary>
public sealed class FakeVault : ICredentialVaultService
{
    private readonly ConcurrentDictionary<string, string> _store = new();

    public Task StoreTokenAsync(string key, string token, CancellationToken ct = default)
    {
        _store[key] = token;
        return Task.CompletedTask;
    }

    public Task<string?> RetrieveTokenAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_store.GetValueOrDefault(key));

    public Task DeleteTokenAsync(string key, CancellationToken ct = default)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public int Count => _store.Count;
}

/// <summary>A CLI service that reports Git as unavailable, so config writes become no-ops.</summary>
public sealed class NoGitCli : IGitCliService
{
    public bool IsGitAvailable => false;

    public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
        => Task.FromResult(new GitCliResult(-1, "", "git not available", GitErrorKind.Unknown, TimeSpan.Zero));

    public Task<GitCliResult> RunAsync(GitCliRequest request, IProgress<GitProgress>? progress, Action<string>? onStdoutLine, CancellationToken ct = default)
        => RunAsync(request, ct);
}
