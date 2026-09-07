namespace Historia.Diagnostics;

/// <summary>Severity of an <see cref="AppLog"/> entry. <c>Trace</c>/<c>Debug</c> are routine flow,
/// <c>Info</c> is a notable operation, <c>Warn</c>/<c>Error</c> are failures.</summary>
public enum AppLogLevel { Trace, Debug, Info, Warn, Error }

/// <summary>
/// The injectable face of <see cref="AppLog"/> (§V9.6). Registered as a singleton so any
/// DI-constructed service can take an <c>IAppLog</c> in its constructor; classes the framework
/// builds (Avalonia views/controls) and static helpers use <see cref="AppLog.Current"/> or the
/// static <see cref="AppLog"/> methods directly. Every call is a cheap no-op unless logging is
/// enabled, and never throws.
/// </summary>
public interface IAppLog
{
    void Trace(string category, string message);
    void Debug(string category, string message);
    void Info(string category, string message);
    void Warn(string category, string message, Exception? ex = null);
    void Error(string category, string message, Exception? ex = null);

    /// <summary>Logs <c>-&gt; message</c> now and <c>&lt;- message (outcome, N.Nms)</c> on dispose.
    /// Call <see cref="AppLog.OperationScope.Fail"/> / <see cref="AppLog.OperationScope.Outcome"/>
    /// on the returned token to record the result.</summary>
    IDisposable Operation(string category, string message);
}

/// <summary>Forwards every call to the static <see cref="AppLog"/>. The single shared instance is
/// <see cref="AppLog.Current"/>.</summary>
internal sealed class AppLogFacade : IAppLog
{
    public void Trace(string category, string message) => AppLog.Write(AppLogLevel.Trace, category, message);
    public void Debug(string category, string message) => AppLog.Write(AppLogLevel.Debug, category, message);
    public void Info(string category, string message) => AppLog.Write(AppLogLevel.Info, category, message);
    public void Warn(string category, string message, Exception? ex = null) => AppLog.Write(AppLogLevel.Warn, category, message, ex);
    public void Error(string category, string message, Exception? ex = null) => AppLog.Write(AppLogLevel.Error, category, message, ex);
    public IDisposable Operation(string category, string message) => AppLog.Operation(category, message);
}
