using System.Diagnostics;
using Historia.Services.Storage;

namespace Historia.Diagnostics;

/// <summary>
/// Opt-in stopwatch logging for startup / repo-open latency work. On unless every call is a
/// couple of cheap no-ops. Turned on by the <c>HISTORIA_PERF</c> environment variable or the
/// Developer panel toggle (persisted as a marker file); lines append to <c>&lt;logs&gt;/perf.log</c>.
/// </summary>
public static class PerfLog
{
    private static readonly string FlagFile = Path.Combine(AppPaths.MachineDirectory, "perf-logging.on");

    private static bool _enabled = EnvOptIn() || FlagFileExists();

    /// <summary>Whether marks are written. Setting it persists the choice across launches.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            try
            {
                if (value)
                    File.WriteAllText(FlagFile, "");
                else if (File.Exists(FlagFile))
                    File.Delete(FlagFile);
            }
            catch { /* diagnostics must never throw */ }
        }
    }

    /// <summary>Full path of the log file, for surfacing in the Developer panel.</summary>
    public static string LogPath => Path.Combine(AppPaths.LogsDirectory, "perf.log");

    private static bool EnvOptIn() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HISTORIA_PERF"));
    private static bool FlagFileExists() { try { return File.Exists(FlagFile); } catch { return false; } }

    private static readonly object Gate = new();
    private static readonly long Origin = Stopwatch.GetTimestamp();

    public static void Mark(string message)
    {
        if (!Enabled)
            return;

        var ms = Stopwatch.GetElapsedTime(Origin).TotalMilliseconds;
        var line = $"{DateTime.Now:HH:mm:ss.fff}  +{ms,10:F1}ms  {message}";
        try
        {
            lock (Gate)
                File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { /* diagnostics must never throw */ }
    }

    /// <summary>Times the scope and logs <c>label: N.Nms</c> on dispose.</summary>
    public static IDisposable Scope(string label) =>
        Enabled ? new Timer(label) : Noop.Instance;

    private sealed class Timer(string label) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public void Dispose() => Mark($"{label}: {Stopwatch.GetElapsedTime(_start).TotalMilliseconds:F1}ms");
    }

    private sealed class Noop : IDisposable
    {
        public static readonly Noop Instance = new();
        public void Dispose() { }
    }
}
