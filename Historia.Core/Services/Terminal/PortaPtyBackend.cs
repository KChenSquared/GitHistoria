namespace Historia.Services.Terminal;

/// <summary>
/// The one production <see cref="ITerminalBackend"/>: a thin adapter over <c>Porta.Pty</c>
/// (ConPTY on Windows, a <c>forkpty</c>/<c>execvp</c> native shim on macOS/Linux). Kept thin on
/// purpose — every line of logic here is a line that cannot be unit-tested.
/// </summary>
public sealed class PortaPtyBackend : ITerminalBackend
{
    public async Task<IPtyConnection> StartAsync(PtyStartInfo info, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(info);

        var options = new PortaPty.PtyOptions
        {
            App = info.ShellPath,
            CommandLine = [.. info.Arguments],
            Cwd = info.WorkingDirectory ?? Environment.CurrentDirectory,
            Name = "xterm-256color",
            Cols = Math.Max(1, info.Columns),
            Rows = Math.Max(1, info.Rows),
            Environment = new Dictionary<string, string>(info.Environment),
        };

        PortaPty.IPtyConnection inner;
        try
        {
            inner = await PortaPty.PtyProvider.SpawnAsync(options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var (kind, message, details) = TerminalErrorClassifier.Classify(ex);
            throw new TerminalStartException(kind == TerminalErrorKind.None ? TerminalErrorKind.PtyUnavailable : kind,
                details ?? message);
        }

        return new Connection(inner);
    }

    internal sealed class Connection : IPtyConnection
    {
        private readonly PortaPty.IPtyConnection _inner;
        private readonly PtyIoPump _pump;
        private readonly object _subscribersGate = new();
        private readonly ManualResetEventSlim _outputReleased = new();
        private Action<ReadOnlyMemory<byte>>? _outputReceived;
        private int _disposed;

        /// <summary>
        /// The read thread starts as soon as the shell is spawned, but the session only subscribes
        /// after <see cref="StartAsync"/> returns — and the shell's first prompt can land in that
        /// gap. So the read thread holds its first chunk until a handler attaches here (or the
        /// connection is disposed), and every chunk reaches the subscriber, in order, on the read
        /// thread. Holding the chunk stops the reader draining the pipe, which only back-pressures
        /// the shell; nothing is dropped.
        /// </summary>
        public event Action<ReadOnlyMemory<byte>>? OutputReceived
        {
            add
            {
                lock (_subscribersGate)
                    _outputReceived += value;
                if (value is not null)
                    _outputReleased.Set();
            }
            remove
            {
                lock (_subscribersGate)
                    _outputReceived -= value;
            }
        }

        public event Action<int>? Exited;

        public Connection(PortaPty.IPtyConnection inner)
        {
            _inner = inner;
            _inner.ProcessExited += OnInnerProcessExited;

            // Dedicated background threads, never the ThreadPool: Porta.Pty's ReaderStream is a
            // synchronous FileStream, so an async read loop would block a pool worker in an
            // uninterruptible ReadFile and never give it back.
            _pump = new PtyIoPump(
                output: _inner.ReaderStream,
                input: _inner.WriterStream,
                onOutput: OnPumpOutput,
                onOutputEnded: null,
                name: "pty");
        }

        private void OnPumpOutput(ReadOnlyMemory<byte> chunk)
        {
            _outputReleased.Wait();
            Volatile.Read(ref _outputReceived)?.Invoke(chunk);
        }

        private void OnInnerProcessExited(object? sender, PortaPty.PtyExitedEventArgs e)
        {
            try { Exited?.Invoke(e.ExitCode); }
            catch { /* a subscriber throwing must not crash the pty callback */ }
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            if (Volatile.Read(ref _disposed) != 0 || data.IsEmpty)
                return;
            _pump.Write(data);
        }

        public void Resize(int columns, int rows)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            try { _inner.Resize(Math.Max(1, columns), Math.Max(1, rows)); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        public void Kill()
        {
            try { _inner.Kill(); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { /* already exited */ }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try { _inner.ProcessExited -= OnInnerProcessExited; } catch { /* best effort */ }

            // Order matters. Release a reader still holding output for a subscriber that never
            // came, so it goes back to draining the pipe. Kill the child, then dispose the inner
            // connection so the ConPTY pipe handles close — that is what makes the pump's blocked
            // synchronous ReadFile throw (a CancellationToken cannot). Only then can the pump join
            // its threads promptly instead of timing out and leaking one forever.
            _outputReleased.Set();
            try { _inner.Kill(); } catch { /* best effort */ }
            try { _inner.Dispose(); } catch { /* best effort */ }
            try { _pump.Dispose(); } catch { /* best effort */ }
        }
    }
}
