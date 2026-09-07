namespace Historia.Services.Terminal;

/// <summary>
/// Moves a pseudoterminal's bytes on <b>dedicated background threads</b>, never on the
/// <see cref="System.Threading.ThreadPool"/> (V5 §3.1).
///
/// <para>
/// <b>Deviation D5.</b> <c>Porta.Pty</c> 2.2.1's Windows <c>ReaderStream</c> / <c>WriterStream</c>
/// are <i>synchronous</i> <see cref="FileStream"/>s — the ConPTY pipes are not opened for
/// overlapped I/O. Calling <c>ReadAsync</c> on such a stream does not yield: it parks a pool
/// worker in a blocking <c>ReadFile</c> for the shell's entire idle time (via the runtime's
/// <c>AsyncOverSyncWithIoCancellation</c> shim), and a <see cref="CancellationToken"/> cannot
/// interrupt it. One live session then permanently consumes one pool thread, and closing the
/// session never returns it — a handful of open/close cycles starves the whole pool and the app
/// appears hung while its window still pumps messages.
/// </para>
/// <para>
/// The fix is to stay off the pool entirely: one reader thread and one writer thread per
/// connection, both <see cref="Thread.IsBackground"/> so a wedged pipe can never block process
/// exit, each running a plain <b>synchronous</b> loop (no state machine, no pool frame).
/// Shutdown is handle-driven: <see cref="Dispose"/> expects the caller to have already closed
/// the underlying connection, which closes the pipe handles, which makes the blocked
/// <c>Read</c> throw — see <c>PortaPtyBackend.Connection.Dispose</c>. If a thread still does not
/// exit in time it dies with the process; it is never a leaked pool worker.
/// </para>
/// </summary>
internal sealed class PtyIoPump : IDisposable
{
    private const int ReadBufferSize = 16 * 1024;

    /// <summary>Outbound writes that have not reached the pty yet. Bounded: a full queue means
    /// the pty is not draining, and dropping keystrokes beats blocking the UI thread.</summary>
    private const int MaxQueuedWrites = 1024;

    private readonly Stream _output;   // pty ──▶ us   (Porta.Pty ReaderStream)
    private readonly Stream _input;    // us  ──▶ pty  (Porta.Pty WriterStream)
    private readonly Action<ReadOnlyMemory<byte>> _onOutput;
    private readonly Action? _onOutputEnded;
    private readonly Thread _readThread;
    private readonly Thread _writeThread;
    private readonly BlockingCollection<byte[]> _writeQueue = new(MaxQueuedWrites);

    private volatile bool _stop;
    private int _endedRaised;
    private int _disposed;

    public PtyIoPump(
        Stream output,
        Stream input,
        Action<ReadOnlyMemory<byte>> onOutput,
        Action? onOutputEnded,
        string name)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _onOutput = onOutput ?? throw new ArgumentNullException(nameof(onOutput));
        _onOutputEnded = onOutputEnded;

        _readThread = new Thread(RunRead) { IsBackground = true, Name = $"{name}-read" };
        _writeThread = new Thread(RunWrite) { IsBackground = true, Name = $"{name}-write" };
        _readThread.Start();
        _writeThread.Start();
    }

    /// <summary>Number of threads this pump is running that have not yet exited. Test hook.</summary>
    internal int LiveThreadCount => (_readThread.IsAlive ? 1 : 0) + (_writeThread.IsAlive ? 1 : 0);

    // ── outbound ────────────────────────────────────────────────────────────────────────────

    /// <summary>Queues bytes for the pty. Never blocks the caller: if the queue is full the
    /// chunk is dropped (the pty is not draining, and a stalled keystroke path is worse).</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (_stop || Volatile.Read(ref _disposed) != 0 || data.IsEmpty)
            return;

        try
        {
            _writeQueue.TryAdd(data.ToArray());
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding has been called — shutting down.
        }
    }

    private void RunWrite()
    {
        try
        {
            foreach (var chunk in _writeQueue.GetConsumingEnumerable())
            {
                if (_stop)
                    break;
                try
                {
                    _input.Write(chunk, 0, chunk.Length);
                    _input.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
                {
                    break;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Queue completed and drained.
        }
        catch
        {
            // A writer thread must never crash the process.
        }
    }

    // ── inbound ─────────────────────────────────────────────────────────────────────────────

    private void RunRead()
    {
        var buffer = new byte[ReadBufferSize];
        try
        {
            while (!_stop)
            {
                int n;
                try
                {
                    n = _output.Read(buffer, 0, buffer.Length);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    break;
                }

                if (n <= 0)
                    break;              // EOF — the write side of the pipe closed (child gone)
                if (_stop)
                    break;

                try
                {
                    _onOutput(new ReadOnlyMemory<byte>(buffer, 0, n));
                }
                catch
                {
                    // A consumer throwing must not kill or leak the pump thread.
                }
            }
        }
        catch
        {
            // A reader thread must never crash the process.
        }
        finally
        {
            RaiseEndedOnce();
        }
    }

    private void RaiseEndedOnce()
    {
        if (Interlocked.Exchange(ref _endedRaised, 1) != 0)
            return;
        try { _onOutputEnded?.Invoke(); }
        catch { /* best effort */ }
    }

    // ── teardown ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Signals both loops to stop and waits briefly for their threads. The caller must already
    /// have closed the underlying streams so a blocked <c>Read</c> throws; a thread that does
    /// not exit in time is a background thread and dies with the process — never a pool worker.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stop = true;
        try { _writeQueue.CompleteAdding(); } catch { /* best effort */ }

        try { _readThread.Join(TimeSpan.FromMilliseconds(500)); } catch { /* best effort */ }
        try { _writeThread.Join(TimeSpan.FromMilliseconds(500)); } catch { /* best effort */ }

        try { _writeQueue.Dispose(); } catch { /* best effort */ }
    }
}
