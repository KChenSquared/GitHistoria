namespace Historia.Diagnostics;

/// <summary>
/// Trims logs older than 48 hours at the start of each auto-fetch round.
/// If trimming fails on stale files and logging is enabled, writes an error to the log,
/// flushes it, and automatically disables logging via settings.
/// </summary>
public sealed class LogTrimmer : ILogTrimmer
{
    private static readonly TimeSpan CutoffAge = TimeSpan.FromHours(48);
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);

    private readonly ISettingsService _settings;
    private readonly TimeProvider _timeProvider;
    private readonly string _logsDirectory;

    public LogTrimmer(
        ISettingsService settings,
        TimeProvider? timeProvider = null,
        string? logsDirectory = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logsDirectory = logsDirectory ?? AppPaths.LogsDirectory;
    }

    public async Task TrimAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var now = _timeProvider.GetLocalNow().DateTime;
        var cutoff = now - CutoffAge;

        try
        {
            ct.ThrowIfCancellationRequested();
            LogRetention.TrimDevLogs(_logsDirectory, cutoff, AppLog.CurrentLogPath);

            ct.ThrowIfCancellationRequested();
            var perfPath = Path.Combine(_logsDirectory, "perf.log");
            await PerfLog.TrimAsync(cutoff, perfPath).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (LoggingSwitch.Enabled)
            {
                AppLog.Write(
                    AppLogLevel.Error,
                    "logs",
                    "Trimming logs older than 48 hours failed; logging was automatically disabled.",
                    ex);
                AppLog.Flush(FlushTimeout);
                try
                {
                    await _settings.UpdateAsync(s => s with { LoggingEnabled = false }).ConfigureAwait(false);
                }
                catch { /* settings failure must not throw */ }
            }
        }
    }
}
