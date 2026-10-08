namespace Historia.Diagnostics;

/// <summary>
/// Trims application and performance logs older than 48 hours.
/// </summary>
public interface ILogTrimmer
{
    Task TrimAsync(CancellationToken ct = default);
}
