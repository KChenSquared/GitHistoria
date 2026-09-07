
namespace Historia.Services.Settings;

public sealed record SettingsChangedEventArgs(AppSettings OldSettings, AppSettings NewSettings);

/// <summary>
/// App-wide settings store (§10). Loaded once synchronously at startup (§6.2.2), cached, and
/// persisted on change with debounced atomic writes (§9.3). Registered as a singleton.
/// </summary>
public interface ISettingsService
{
    /// <summary>The current cached settings. Always a non-blocking read.</summary>
    AppSettings Current { get; }

    /// <summary>
    /// Applies a mutation, updates the cache, queues a debounced persist, and raises
    /// <see cref="Changed"/>. Safe to call from any thread.
    /// </summary>
    Task UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default);

    /// <summary>Forces any pending debounced write to disk (used at shutdown).</summary>
    Task FlushAsync(CancellationToken ct = default);

    /// <summary>Raised after <see cref="Current"/> changes.</summary>
    event EventHandler<SettingsChangedEventArgs>? Changed;
}
