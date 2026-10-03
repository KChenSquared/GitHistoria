namespace Historia.Services.Git;

/// <summary>
/// Pull-based <see cref="IEnumerator{T}"/> over a live <c>git log --date-order</c> process — the
/// commit-graph walk (see <c>Historia.Services.Graph.GraphLayoutService</c>). Git's own in-degree
/// topological sort expands lazily by generation number (using a commit-graph file when present)
/// instead of sorting the whole history up front, so on a huge history the first rows arrive in
/// a fraction of a second.
/// <para>
/// Tip SHAs are written to the process's stdin (<c>--stdin</c>) and then closed — a large tag/
/// branch count can exceed the ~32K Windows command-line limit if passed as arguments. Pipe
/// back-pressure between pulls means the walk's in-degree state lives entirely inside the git
/// process; pausing (letting a caller stop pulling) and resuming (pulling again later) costs
/// nothing extra.
/// </para>
/// </summary>
public sealed class GitLogStream : IEnumerator<RawCommit>
{
    // %H commit, %P space-separated parents, %aI/%cI strict ISO-8601 author/commit dates,
    // %an/%ae author name/email, %s subject. One field per record, split on 0x1f (unit
    // separator); -z terminates each record with NUL instead of the default blank line, so a
    // record can be read byte-by-byte without ambiguity even though none of these fields can
    // themselves contain a NUL or embedded newline.
    private const string Format = "%H%x1f%P%x1f%aI%x1f%cI%x1f%an%x1f%ae%x1f%s";
    private const char FieldSep = '\x1f';

    // Started and not yet disposed, by process id. Only Dispose removes an entry — a stream
    // dropped without Dispose stays listed. Holds no reference to the stream or its process.
    private static readonly ConcurrentDictionary<int, string> Undisposed = new();

    private readonly Process _process;
    private readonly int _processId;
    private readonly NulRecordReader _reader;
    private readonly StringBuilder _stderr;
    private readonly Task _stderrDrain;
    private bool _disposed;

    public RawCommit Current { get; private set; }
    object IEnumerator.Current => Current;

    /// <summary>True once the underlying process has failed — exited non-zero, or produced a
    /// record this parser could not understand. The caller should dispose it; the rows already
    /// yielded stay valid, but the walk cannot continue.</summary>
    public bool Failed { get; private set; }

    /// <summary>Why the stream <see cref="Failed"/> — the exit code and a tail of git's stderr, or
    /// the read/parse error. Null while it hasn't failed.</summary>
    public string? FailureReason { get; private set; }

    private GitLogStream(Process process, Task stderrDrain, StringBuilder stderr)
    {
        _process = process;
        try { _processId = process.Id; } catch (InvalidOperationException) { _processId = -1; }
        _reader = new NulRecordReader(process.StandardOutput.BaseStream);
        _stderrDrain = stderrDrain;
        _stderr = stderr;
    }

    /// <summary>
    /// Starts <c>git log --date-order -z --stdin --format=...</c> over <paramref name="tipShas"/>.
    /// Returns null (spawning nothing further) when git isn't available, there are no tips, or
    /// the process could not be started.
    /// <para>
    /// A non-empty <paramref name="pathFilter"/> limits the walk to commits touching that path
    /// (file or folder), matched literally (<c>GIT_LITERAL_PATHSPECS=1</c>). It adds
    /// <c>--parents</c>, which turns on parent rewriting so <c>%P</c> names the nearest shown
    /// ancestors instead of parents that never appear in the output — without it the graph
    /// would be full of edges to missing rows. Renames are not followed. The pathspec goes on the
    /// command line after <c>--</c>; <c>--stdin</c> still reads only revisions, since paths are
    /// taken from stdin only after a <c>--</c> line there.
    /// </para>
    /// </summary>
    public static GitLogStream? TryStart(
        IGitExecutableLocator locator, string repositoryPath, IReadOnlyList<string> tipShas, GitProfile? profile = null,
        string? pathFilter = null)
    {
        var git = locator.Current;
        if (!git.Found || git.Path is null || tipShas.Count == 0)
            return null;

        var psi = new ProcessStartInfo(git.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = repositoryPath,
        };

        foreach (var arg in GitCliProcessSetup.DeterministicPrefix)
            psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add("log");
        psi.ArgumentList.Add("--date-order");
        psi.ArgumentList.Add("-z");
        psi.ArgumentList.Add("--no-mailmap");
        psi.ArgumentList.Add("--no-show-signature");
        psi.ArgumentList.Add("--stdin");
        psi.ArgumentList.Add($"--format={Format}");
        var filtered = !string.IsNullOrEmpty(pathFilter);
        if (filtered)
        {
            psi.ArgumentList.Add("--parents");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(pathFilter!);
        }

        GitCliProcessSetup.ComposeEnvironment(psi, profile, extraEnvironment: null);
        // Graph rows must name real commit objects consistently across details, diffs, and decorations;
        // ignore replace refs entirely.
        psi.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        if (filtered)
            psi.Environment["GIT_LITERAL_PATHSPECS"] = "1";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = false };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Diagnostics.PerfLog.Mark($"GitLogStream: failed to start git: {ex.Message}");
            return null;
        }

        try
        {
            var writer = process.StandardInput;
            writer.NewLine = "\n";
            foreach (var sha in tipShas)
                writer.WriteLine(sha);
            writer.Close();
        }
        catch (IOException)
        {
            // The process may have exited before consuming all of stdin — MoveNext observes it.
        }

        var stderr = new StringBuilder();
        var drain = Task.Run(() => DrainStderr(process, stderr));
        var stream = new GitLogStream(process, drain, stderr);
        Undisposed[stream._processId] = repositoryPath;
        return stream;
    }

    private static async Task DrainStderr(Process process, StringBuilder into)
    {
        try
        {
            var text = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            lock (into)
                into.Append(text);
        }
        catch
        {
            // Best-effort — surfaced only in a failure PerfLog mark.
        }
    }

    /// <summary>Test seam: streams started for <paramref name="repositoryPath"/> and not yet
    /// disposed.</summary>
    internal static int LiveCountForTesting(string repositoryPath) =>
        Undisposed.Count(kv => string.Equals(kv.Value, repositoryPath, StringComparison.OrdinalIgnoreCase));

    public bool MoveNext()
    {
        if (_disposed || Failed)
            return false;

        string? record;
        try
        {
            record = _reader.ReadRecord();
        }
        catch (Exception ex)
        {
            Fail($"read failure: {ex.Message}");
            return false;
        }

        if (record is null)
        {
            // EOF on stdout. A clean exit means the walk is exhausted; anything else is a
            // failure.
            try { _process.WaitForExit(5_000); } catch { /* best effort */ }
            var exitCode = SafeExitCode();
            if (exitCode != 0)
                Fail($"git log exited {exitCode}: {StderrTail()}");
            return false;
        }

        try
        {
            Current = ParseRecord(record);
            return true;
        }
        catch (Exception ex)
        {
            Fail($"parse failure: {ex.Message} (record: {Truncate(record, 200)})");
            return false;
        }
    }

    private void Fail(string reason)
    {
        Failed = true;
        FailureReason = reason;
        Diagnostics.PerfLog.Mark($"GitLogStream: {reason}");
    }

    private string StderrTail()
    {
        try { _stderrDrain.Wait(1_000); } catch { /* best effort */ }
        lock (_stderr)
            return Truncate(_stderr.ToString().Trim(), 300);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private int SafeExitCode()
    {
        try { return _process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }

    /// <summary>Splits <see cref="RawCommit"/> field text — internal (not private) so tests can
    /// feed it fixture records directly without spawning a process.</summary>
    internal static RawCommit ParseRecord(string record)
    {
        var parts = record.Split(FieldSep);
        if (parts.Length < 7)
            throw new FormatException($"expected 7 fields, got {parts.Length}");

        var id = GitObjectId.Parse(parts[0]);
        var parentIds = parts[1].Length == 0
            ? Array.Empty<GitObjectId>()
            : parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => GitObjectId.Parse(s)).ToArray();

        var authorWhen = DateTimeOffset.Parse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var commitWhen = DateTimeOffset.Parse(parts[3], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        var metadata = new CommitMetadata
        {
            Id = id,
            MessageShort = parts[6],
            AuthorName = parts[4],
            AuthorEmail = parts[5],
            AuthorWhen = authorWhen,
            CommitWhen = commitWhen,
            ParentCount = parentIds.Length,
        };

        return new RawCommit(id, parentIds, metadata);
    }

    public void Reset() => throw new NotSupportedException("GitLogStream is a forward-only, one-shot enumerator.");

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Undisposed.TryRemove(_processId, out _);
        GitCliProcessSetup.TryKill(_process);
        try { _stderrDrain.Wait(1_000); } catch { /* best effort */ }
        try { _process.Dispose(); } catch { /* best effort */ }
        _reader.Dispose();
    }
}

/// <summary>
/// Reads NUL-delimited (<c>git log -z</c>) UTF-8 text records out of a <see cref="Stream"/>, one
/// at a time, buffering only the bytes of the record currently being assembled. Internal (not
/// private to <see cref="GitLogStream"/>) so a test can feed it a <see cref="MemoryStream"/> of
/// fixture bytes without spawning a real git process.
/// </summary>
internal sealed class NulRecordReader(Stream stream) : IDisposable
{
    private readonly MemoryStream _recordBuf = new(256);
    private readonly byte[] _readBuf = new byte[64 * 1024];
    private int _readPos;
    private int _readLen;
    private bool _eofSeen;

    /// <summary>Reads up to the next NUL byte and decodes it as UTF-8. Returns null at end of
    /// stream (no more bytes, including a trailing terminator-less final record — git always
    /// terminates the last record too, but this is defensive).</summary>
    public string? ReadRecord()
    {
        _recordBuf.SetLength(0);
        while (true)
        {
            if (_readPos >= _readLen)
            {
                if (_eofSeen)
                    break;
                _readLen = stream.Read(_readBuf, 0, _readBuf.Length);
                _readPos = 0;
                if (_readLen <= 0)
                {
                    _eofSeen = true;
                    break;
                }
            }

            var start = _readPos;
            var span = new ReadOnlySpan<byte>(_readBuf, start, _readLen - start);
            var nul = span.IndexOf((byte)0);
            if (nul < 0)
            {
                _recordBuf.Write(_readBuf, start, _readLen - start);
                _readPos = _readLen;
                continue;
            }

            _recordBuf.Write(_readBuf, start, nul);
            _readPos = start + nul + 1;
            return Encoding.UTF8.GetString(_recordBuf.GetBuffer(), 0, (int)_recordBuf.Length);
        }

        return _recordBuf.Length > 0
            ? Encoding.UTF8.GetString(_recordBuf.GetBuffer(), 0, (int)_recordBuf.Length)
            : null;
    }

    public void Dispose() => _recordBuf.Dispose();
}
