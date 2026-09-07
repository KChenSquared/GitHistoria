namespace Historia.Services.Storage;

/// <summary>
/// Atomic, serialized file writes (§9.3). Every write to a given path is funneled through a
/// per-path <see cref="SemaphoreSlim"/>; the payload is written to a temp file and then
/// atomically renamed over the target so a crash never leaves a half-written file.
/// Rapid successive writes are coalesced on a 250 ms trailing debounce — only the final
/// state reaches disk.
/// </summary>
public sealed class AtomicFileWriter : IAsyncDisposable
{
    private const int DebounceMilliseconds = 250;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _pendingLock = new();

    private string? _pendingContent;
    private CancellationTokenSource? _debounceCts;
    private Task _debounceTask = Task.CompletedTask;

    public string FilePath { get; }

    public AtomicFileWriter(string filePath)
    {
        FilePath = filePath;
        var dir = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(dir);

        // Each write now uses a unique temp name (so two processes writing the same target
        // cannot interleave into one shared temp), which means a hard crash mid-write can
        // leave an orphan. Sweep our own stale temps on construction — best effort.
        try
        {
            var prefix = Path.GetFileName(filePath) + ".";
            foreach (var stale in Directory.EnumerateFiles(dir, prefix + "*.tmp"))
            {
                try { File.Delete(stale); } catch { /* locked or gone */ }
            }
        }
        catch { /* directory unreadable — not fatal */ }
    }

    /// <summary>Writes immediately, bypassing the debounce. Used at shutdown and for first-write.</summary>
    public async Task WriteNowAsync(string content, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(content, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Queues a debounced write. Successive calls within the debounce window replace the
    /// pending payload; the timer restarts on each call.
    /// </summary>
    public void QueueWrite(string content)
    {
        lock (_pendingLock)
        {
            _pendingContent = content;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;
            _debounceTask = RunDebouncedAsync(token);
        }
    }

    /// <summary>Flushes any pending debounced write synchronously with respect to completion.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        Task toAwait;
        string? pending;
        lock (_pendingLock)
        {
            toAwait = _debounceTask;
            pending = _pendingContent;
        }

        try
        {
            await toAwait.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A newer QueueWrite superseded it; fall through and write the latest below.
        }

        lock (_pendingLock)
        {
            pending = _pendingContent;
        }

        if (pending is not null)
        {
            await WriteNowAsync(pending, ct).ConfigureAwait(false);
            lock (_pendingLock)
            {
                if (ReferenceEquals(_pendingContent, pending))
                    _pendingContent = null;
            }
        }
    }

    private async Task RunDebouncedAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        string? content;
        lock (_pendingLock)
        {
            content = _pendingContent;
            _pendingContent = null;
        }

        if (content is null)
            return;

        await _writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(content, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown; FlushAsync will pick up the latest pending content.
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task WriteAtomicAsync(string content, CancellationToken ct)
    {
        // Unique per write: a second process (or a second AtomicFileWriter) targeting the same
        // path can no longer truncate a temp file this write is still appending to.
        var tempPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        using (Historia.Diagnostics.PerfLog.Scope($"AtomicFileWriter WriteAllTextAsync ({System.IO.Path.GetFileName(FilePath)})"))
            await File.WriteAllTextAsync(tempPath, content, ct).ConfigureAwait(false);

        // File.Move with overwrite is atomic on the same volume on Windows (ReplaceFile semantics).
        // §V3-9.1: Documents is often OneDrive-backed and the sync client can hold a transient
        // lock across the replace. Retry three times, 50 ms apart, then give up non-fatally.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tempPath, FilePath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
                return; // give-up stays non-fatal
            }
            catch (UnauthorizedAccessException)
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Historia.Diagnostics.PerfLog.Mark($"AtomicFileWriter.DisposeAsync start ({System.IO.Path.GetFileName(FilePath)})");
        try
        {
            using (Historia.Diagnostics.PerfLog.Scope($"AtomicFileWriter FlushAsync ({System.IO.Path.GetFileName(FilePath)})"))
                await FlushAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort flush on dispose.
        }

        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _writeGate.Dispose();
        Historia.Diagnostics.PerfLog.Mark($"AtomicFileWriter.DisposeAsync done ({System.IO.Path.GetFileName(FilePath)})");
    }
}
