namespace Historia.Diagnostics;

/// <summary>
/// The application log. One opt-in, structured logger for the whole app: every unhandled
/// exception, every <c>Warning</c>/<c>Error</c> notification and classified git failure, every git
/// operation (through <c>LoggingGitGateway</c>) and every git CLI invocation, plus repository
/// open/close. Terminal session I/O is deliberately not logged.
/// <para>
/// Off unless the Developer panel toggle or the <c>HISTORIA_LOG</c> environment variable turns it
/// on, at which point one file per session is written under <see cref="AppPaths.LogsDirectory"/>
/// (<c>dev-&lt;timestamp&gt;.log</c>); the newest <see cref="RetainedSessions"/> are kept.
/// </para>
/// <para>
/// This is <b>not</b> <see cref="PerfLog"/> — that stays a separate, perf-timing-only tool. Do not
/// add a third logging system; extend this one. Every method is a no-op when disabled and never throws.
/// </para>
/// </summary>
public static partial class AppLog
{
    /// <summary>How many past <c>dev-*.log</c> files to keep when a new session starts.</summary>
    private const int RetainedSessions = 10;

    private static readonly object Gate = new();
    private static readonly OptInFlag Flag = new("HISTORIA_LOG", "dev-logging.on");
    private static string? _sessionPath;

    /// <summary>The session file's writer: the file is held open for as long as logging is
    /// enabled, and lines are written on the writer's own thread, so logging never waits on the
    /// disk. Trap: reopening the file per entry, then writing a held-open file under
    /// <see cref="Gate"/>, both put file I/O on every logging thread — one held-up write stalled
    /// them all.</summary>
    private static QueuedLineWriter? _writer;

    static AppLog()
    {
        try
        {
            if (Flag.IsOptedIn)
                lock (Gate)
                    OpenSessionLocked();
        }
        catch { /* logging must never throw */ }
    }

    /// <summary>The shared injectable instance handed to the DI container.</summary>
    public static IAppLog Current { get; } = new AppLogFacade();

    private static readonly AsyncLocal<AppLogSession?> _testSession = new();

    /// <summary>
    /// Flow-local test session for running tests in parallel without shared log writer state.
    /// Outside tests this is null. Disposal releases the flow's interception in the flow that
    /// disposes it; a session disposed from a deeper async frame still needs <see cref="ResetForTesting"/>
    /// because an <see cref="AsyncLocal{T}"/> write does not propagate back up the async call tree.
    /// </summary>
    internal sealed class AppLogSession : IDisposable
    {
        private readonly object _gate = new();
        private QueuedLineWriter? _writer;
        private bool _disposed;

        public string SessionPath { get; }
        public bool Enabled { get; set; } = true;

        public AppLogSession(string? logsDirectory = null)
        {
            var dir = logsDirectory ?? AppPaths.LogsDirectory;
            Directory.CreateDirectory(dir);
            SessionPath = Path.Combine(dir, $"dev-{DateTime.Now:yyyyMMdd-HHmmss-fffffff}.log");
            _writer = QueuedLineWriter.HoldOpen(SessionPath, "app-log-test-writer");
            var asm = typeof(AppLog).Assembly.GetName();
            WriteLine($"# Historia developer log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            WriteLine($"# {asm.Name} {asm.Version}  |  {RuntimeInformation.OSDescription}  |  pid {Environment.ProcessId}");
            WriteLine($"{Stamp()}  INFO   [app]  logging started");
        }

        public void WriteLine(string line)
        {
            lock (_gate)
            {
                if (_disposed || _writer is null) return;
                _writer.Enqueue(line);
            }
        }

        public bool Flush(TimeSpan timeout)
        {
            QueuedLineWriter? writer;
            lock (_gate) writer = _writer;
            return writer is null || writer.Flush(timeout);
        }

        public void Dispose()
        {
            QueuedLineWriter? writer;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                Enabled = false;
                writer = _writer;
                _writer = null;
            }
            try { writer?.Dispose(); } catch { }

            if (ReferenceEquals(_testSession.Value, this))
                _testSession.Value = null;
        }
    }

    /// <summary>Whether anything is written. Setting it persists the choice (marker file) and
    /// opens a fresh session file / closes the current one.</summary>
    public static bool Enabled
    {
        get => _testSession.Value?.Enabled ?? Flag.Enabled;
        set
        {
            if (_testSession.Value is { } testSession)
            {
                testSession.Enabled = value;
                return;
            }

            QueuedLineWriter? retired = null;
            lock (Gate)
            {
                var wasEnabled = Flag.Enabled;
                Flag.Enabled = value;
                if (value == wasEnabled)
                    return;

                if (value)
                {
                    OpenSessionLocked();
                }
                else
                {
                    WriteLineLocked($"{Stamp()}  INFO   [app]  logging stopped");
                    _sessionPath = null;
                    retired = DetachWriterLocked();
                }
            }

            // Drains the session's queued lines; outside Gate so other loggers never wait on it.
            retired?.Dispose();
        }
    }

    /// <summary>Full path of the active session log, or null when disabled. Shown in the Developer panel.</summary>
    public static string? CurrentLogPath => _testSession.Value?.SessionPath ?? _sessionPath;

    /// <summary>Write one entry. No-op unless <see cref="Enabled"/>.</summary>
    public static void Write(AppLogLevel level, string category, string message, Exception? ex = null)
    {
        if (!Enabled)
            return;

        try
        {
            var sb = new StringBuilder(128);
            sb.Append(Stamp()).Append("  ").Append(LevelTag(level)).Append("  [").Append(category).Append("]  ")
              .Append(Redact(message));

            if (ex is not null)
            {
                sb.Append(Environment.NewLine);
                AppendException(sb, ex);
            }

            var line = sb.ToString();
            if (_testSession.Value is { } session)
            {
                session.WriteLine(line);
                return;
            }

            lock (Gate)
                WriteLineLocked(line);
        }
        catch { /* logging must never throw */ }
    }

    /// <summary>Logs <c>-&gt; message</c> now and <c>&lt;- message (outcome, N.Nms)</c> on dispose.
    /// Cheap default-struct no-op when disabled.</summary>
    public static OperationScope Operation(string category, string message)
        => Enabled ? new OperationScope(category, Redact(message)) : default;

    /// <summary>Test seam: starts an isolated per-flow logging session.</summary>
    internal static AppLogSession StartSessionForTesting(string? dir = null)
    {
        var session = new AppLogSession(dir);
        _testSession.Value = session;
        return session;
    }

    /// <summary>Test seam: forget the current session without touching the marker file.</summary>
    internal static void ResetForTesting()
    {
        if (_testSession.Value is { } session)
        {
            session.Dispose();
            _testSession.Value = null;
        }

        QueuedLineWriter? retired;
        lock (Gate)
        {
            Flag.SetEnabledInMemory(false);
            _sessionPath = null;
            retired = DetachWriterLocked();
        }
        retired?.Dispose();
    }

    /// <summary>Waits (at most <paramref name="timeout"/>) until every line written so far is in
    /// the session file — for crash handlers and exit, where the process may end right after.
    /// True when it got there in time.</summary>
    public static bool Flush(TimeSpan timeout)
    {
        if (_testSession.Value is { } session)
            return session.Flush(timeout);
        QueuedLineWriter? writer;
        lock (Gate) writer = _writer;
        return writer is null || writer.Flush(timeout);
    }

    // ── session lifecycle ──

    private static void OpenSessionLocked()
    {
        var dir = AppPaths.LogsDirectory;
        Directory.CreateDirectory(dir);
        PruneLocked(dir);

        // In case a session is still open. Disposed here, not deferred: the new session file can
        // have the same name (same second), and the old handle must be gone before it reopens.
        DetachWriterLocked()?.Dispose();

        _sessionPath = Path.Combine(dir, $"dev-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        try
        {
            // Held open for the whole session with FileShare.Read, so anything that wants to read
            // the file while it's live — "Open logs folder" + a text editor, `Get-Content -Wait` —
            // still can. No BOM: lines are appended.
            _writer = QueuedLineWriter.HoldOpen(_sessionPath, "app-log-writer");
        }
        catch
        {
            // Can't open the file — WriteLineLocked below (and every later call) becomes a
            // silent no-op via the null check, same as any other diagnostics failure.
            _writer = null;
        }
        Flag.SetEnabledInMemory(true);

        var asm = typeof(AppLog).Assembly.GetName();
        WriteLineLocked($"# Historia developer log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        WriteLineLocked($"# {asm.Name} {asm.Version}  |  {RuntimeInformation.OSDescription}  |  pid {Environment.ProcessId}");
        WriteLineLocked($"{Stamp()}  INFO   [app]  logging started");
    }

    private static QueuedLineWriter? DetachWriterLocked()
    {
        var writer = _writer;
        _writer = null;
        return writer;
    }

    private static void PruneLocked(string dir)
    {
        try
        {
            var stale = Directory.GetFiles(dir, "dev-*.log")
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .Skip(RetainedSessions)
                .ToArray();
            foreach (var f in stale)
            {
                try { File.Delete(f); } catch { /* held open / gone — ignore */ }
            }
        }
        catch { /* enumeration failure is not worth surfacing */ }
    }

    private static void WriteLineLocked(string line)
    {
        _writer?.Enqueue(line);
    }

    // ── formatting ──

    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss.fff");

    private static string LevelTag(AppLogLevel level) => level switch
    {
        AppLogLevel.Trace => "TRACE",
        AppLogLevel.Debug => "DEBUG",
        AppLogLevel.Info => "INFO ",
        AppLogLevel.Warn => "WARN ",
        _ => "ERROR",
    };

    private static void AppendException(StringBuilder sb, Exception ex, int depth = 0)
    {
        var pad = new string(' ', 4 + depth * 2);
        sb.Append(pad).Append(ex.GetType().FullName).Append(": ").Append(Redact(ex.Message));

        if (ex is Historia.Services.Git.HistoriaGitException { Details: { Length: > 0 } details })
        {
            sb.Append(Environment.NewLine).Append(pad).Append("  git: ")
              .Append(Redact(Truncate(details.Replace(Environment.NewLine, " ").Replace('\n', ' '), 500)));
        }

        if (!string.IsNullOrEmpty(ex.StackTrace))
        {
            foreach (var frame in ex.StackTrace.Split('\n'))
                sb.Append(Environment.NewLine).Append(pad).Append("  ").Append(frame.TrimEnd('\r'));
        }

        if (ex.InnerException is { } inner && depth < 5)
        {
            sb.Append(Environment.NewLine).Append(pad).Append("caused by:").Append(Environment.NewLine);
            AppendException(sb, inner, depth + 1);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Strip <c>user:pass</c> / token userinfo from any URL before it reaches the file —
    /// git remote URLs turn up in CLI argument lines.</summary>
    internal static string Redact(string s)
        => string.IsNullOrEmpty(s) ? s : UserInfoPattern().Replace(s, "://<redacted>@");

    [GeneratedRegex(@"://[^/\s@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UserInfoPattern();

    /// <summary>
    /// Enter/exit token for <see cref="Operation"/>. A <c>default</c> instance (logging disabled)
    /// disposes to nothing.
    /// </summary>
    public readonly struct OperationScope : IDisposable
    {
        private readonly string? _category;
        private readonly string _message;
        private readonly long _start;
        private readonly string?[]? _outcome; // 1-slot mutable cell shared with copies of this struct

        internal OperationScope(string category, string message)
        {
            _category = category;
            _message = message;
            _start = Stopwatch.GetTimestamp();
            _outcome = new string?[1];
            Write(AppLogLevel.Info, category, "-> " + message);
        }

        /// <summary>Record a failure; the exit line reads <c>cancelled</c> for a cancellation and <c>FAILED: &lt;type&gt;: &lt;message&gt;</c> for anything else.</summary>
        public void Fail(Exception ex)
        {
            if (_outcome is not null)
            {
                _outcome[0] = ex is OperationCanceledException
                    ? "cancelled"
                    : $"FAILED: {ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>Record a custom outcome string for the exit line (default is <c>ok</c>).</summary>
        public void Outcome(string text)
        {
            if (_outcome is not null)
                _outcome[0] = text;
        }

        public void Dispose()
        {
            if (_category is null)
                return;
            var ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            Write(AppLogLevel.Info, _category, $"<- {_message}  ({_outcome?[0] ?? "ok"}, {ms:F1}ms)");
        }
    }
}
