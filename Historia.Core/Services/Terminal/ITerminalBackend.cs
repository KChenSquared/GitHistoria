namespace Historia.Services.Terminal;

/// <summary>Everything <see cref="ITerminalBackend"/> needs to spawn a pseudoterminal.</summary>
public sealed record PtyStartInfo
{
    public required string ShellPath { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public required IReadOnlyDictionary<string, string> Environment { get; init; }
    public required int Columns { get; init; }
    public required int Rows { get; init; }
}

/// <summary>
/// Spawns a pseudoterminal. The seam that isolates <c>Porta.Pty</c> so an
/// <c>xterm.js</c>-in-WebView surface — or a test fake — can substitute without touching the
/// session service, settings, theming, or entry points.
/// </summary>
public interface ITerminalBackend
{
    Task<IPtyConnection> StartAsync(PtyStartInfo info, CancellationToken ct);
}

/// <summary>
/// A live pseudoterminal. Raw bytes both ways — never strings: decoding each chunk to a
/// string splits multi-byte UTF-8 at chunk boundaries. Decoding is the emulator's job.
/// </summary>
public interface IPtyConnection : IDisposable
{
    void Write(ReadOnlySpan<byte> data);
    void Resize(int columns, int rows);

    /// <summary>
    /// Raised on a background thread. <b>The buffer is only valid for the duration of the
    /// call</b> — the implementation may pool or reuse it. Consumers must copy or consume
    /// synchronously.
    /// </summary>
    event Action<ReadOnlyMemory<byte>>? OutputReceived;

    /// <summary>Raised once, with the child's exit code, when the shell terminates.</summary>
    event Action<int>? Exited;

    /// <summary>Kills the whole process tree, not just the shell.</summary>
    void Kill();
}
