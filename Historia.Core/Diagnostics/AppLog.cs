namespace Historia.Diagnostics;

/// <summary>
/// The application log (§V9.6). One opt-in, structured logger for the whole app: every unhandled
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

    private static bool _enabled;
    private static string? _sessionPath;

    static AppLog()
    {
        try
        {
            if (EnvOptIn() || FlagFileExists())
                lock (Gate)
                    OpenSessionLocked();
        }
        catch { /* logging must never throw */ }
    }

    /// <summary>The shared injectable instance handed to the DI container.</summary>
    public static IAppLog Current { get; } = new AppLogFacade();

    /// <summary>Marker file (mirrors <see cref="PerfLog"/>'s) that persists the opt-in across launches.</summary>
    private static string FlagFile => Path.Combine(AppPaths.MachineDirectory, "dev-logging.on");

    /// <summary>Whether anything is written. Setting it persists the choice (marker file) and
    /// opens a fresh session file / closes the current one.</summary>
    public static bool Enabled
    {
        get => Volatile.Read(ref _enabled);
        set
        {
            lock (Gate)
            {
                PersistFlag(value);
                if (value == _enabled)
                    return;

                if (value)
                {
                    OpenSessionLocked();
                }
                else
                {
                    WriteLineLocked($"{Stamp()}  INFO   [app]  logging stopped");
                    _enabled = false;
                    _sessionPath = null;
                }
            }
        }
    }

    /// <summary>Full path of the active session log, or null when disabled. Shown in the Developer panel.</summary>
    public static string? CurrentLogPath => _sessionPath;

    /// <summary>Write one entry. No-op unless <see cref="Enabled"/>.</summary>
    public static void Write(AppLogLevel level, string category, string message, Exception? ex = null)
    {
        if (!_enabled)
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

            lock (Gate)
                WriteLineLocked(sb.ToString());
        }
        catch { /* logging must never throw */ }
    }

    /// <summary>Logs <c>-&gt; message</c> now and <c>&lt;- message (outcome, N.Nms)</c> on dispose.
    /// Cheap default-struct no-op when disabled.</summary>
    public static OperationScope Operation(string category, string message)
        => _enabled ? new OperationScope(category, Redact(message)) : default;

    /// <summary>Test seam: forget the current session without touching the marker file.</summary>
    internal static void ResetForTesting()
    {
        lock (Gate)
        {
            _enabled = false;
            _sessionPath = null;
        }
    }

    // ── session lifecycle ──

    private static void OpenSessionLocked()
    {
        var dir = AppPaths.LogsDirectory;
        Directory.CreateDirectory(dir);
        PruneLocked(dir);

        _sessionPath = Path.Combine(dir, $"dev-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _enabled = true;

        var asm = typeof(AppLog).Assembly.GetName();
        WriteLineLocked($"# Historia developer log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        WriteLineLocked($"# {asm.Name} {asm.Version}  |  {RuntimeInformation.OSDescription}  |  pid {Environment.ProcessId}");
        WriteLineLocked($"{Stamp()}  INFO   [app]  logging started");
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
        if (_sessionPath is null)
            return;
        try { File.AppendAllText(_sessionPath, line + Environment.NewLine); }
        catch { /* disk full / permission — drop the line */ }
    }

    private static void PersistFlag(bool on)
    {
        try
        {
            if (on)
                File.WriteAllText(FlagFile, "");
            else if (File.Exists(FlagFile))
                File.Delete(FlagFile);
        }
        catch { /* diagnostics must never throw */ }
    }

    private static bool EnvOptIn() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HISTORIA_LOG"));
    private static bool FlagFileExists() { try { return File.Exists(FlagFile); } catch { return false; } }

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

        /// <summary>Record a failure; the exit line reads <c>FAILED: &lt;type&gt;: &lt;message&gt;</c>.</summary>
        public void Fail(Exception ex)
        {
            if (_outcome is not null)
                _outcome[0] = $"FAILED: {ex.GetType().Name}: {ex.Message}";
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
