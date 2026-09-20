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
    private static readonly AsyncLocal<bool?> _testEnabledOverride = new();

    /// <summary>Whether marks are written. Setting it persists the choice across launches.</summary>
    public static bool Enabled
    {
        get => _testEnabledOverride.Value ?? _enabled;
        set
        {
            if (_testEnabledOverride.Value.HasValue)
            {
                _testEnabledOverride.Value = value;
                return;
            }

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

    /// <summary>Test seam: overrides Enabled for the current async flow without touching the flag file.</summary>
    internal static void OverrideEnabledForTesting(bool? enabled)
    {
        _testEnabledOverride.Value = enabled;
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

    /// <summary>
    /// One line of GC/process memory stats,
    /// tagged with <paramref name="label"/> so a run can be sliced by phase (quick paint, first
    /// paint, every 50k rows of silent pre-load, every 100k rows of the disk writer, before/after
    /// a tab-switch purge, 60s idle). Cheap enough to call from a hot loop when disabled — the
    /// early-out below is the only cost — but every field here is itself a GC query, so callers
    /// still gate frequent call sites behind a row-count stride rather than calling per row.
    /// </summary>
    public static void Memory(string label)
    {
        if (!Enabled)
            return;
        try
        {
            var info = GC.GetGCMemoryInfo();
            var gen = info.GenerationInfo;
            var loh = gen.Length > 3 ? gen[3].SizeAfterBytes : 0L;
            var poh = gen.Length > 4 ? gen[4].SizeAfterBytes : 0L;
            static long Mb(long bytes) => bytes / (1024 * 1024);
            Mark(
                $"memory: {label}  heap={Mb(info.HeapSizeBytes)}MB  frag={Mb(info.FragmentedBytes)}MB  " +
                $"loh={Mb(loh)}MB  poh={Mb(poh)}MB  committed={Mb(info.TotalCommittedBytes)}MB  " +
                $"workingSet={Mb(Environment.WorkingSet)}MB  totalAllocated={Mb(GC.GetTotalAllocatedBytes())}MB");
        }
        catch { /* diagnostics must never throw */ }
    }

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
