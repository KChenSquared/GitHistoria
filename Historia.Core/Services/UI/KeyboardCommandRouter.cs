namespace Historia.Services.UI;

/// <summary>
/// Shared keyboard-accelerator router for the WPF region and the WebView (§6.15.2). A WebView
/// that swallows accelerators is a Phase 1 defect, so unhandled <c>keydown</c> events from the
/// Blazor side are forwarded here and dispatched to the same handlers the WPF side uses.
/// Registered as a singleton.
/// </summary>
public interface IKeyboardCommandRouter
{
    /// <summary>Registers a handler for a chord such as <c>"Ctrl+Enter"</c>. Returns a token that unregisters on dispose.</summary>
    IDisposable Register(string chord, Func<Task> handler);

    /// <summary>Dispatches a chord. Returns true when a handler ran (the caller should then swallow the key).</summary>
    Task<bool> DispatchAsync(string chord);
}

public sealed class KeyboardCommandRouter : IKeyboardCommandRouter
{
    private readonly ConcurrentDictionary<string, Func<Task>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public IDisposable Register(string chord, Func<Task> handler)
    {
        var normalized = Normalize(chord);
        _handlers[normalized] = handler;
        return new Registration(this, normalized, handler);
    }

    public async Task<bool> DispatchAsync(string chord)
    {
        if (_handlers.TryGetValue(Normalize(chord), out var handler))
        {
            await handler().ConfigureAwait(false);
            return true;
        }

        return false;
    }

    private static string Normalize(string chord)
    {
        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mods = parts.Where(p => p is "Ctrl" or "Shift" or "Alt")
                        .OrderBy(p => p, StringComparer.Ordinal);
        var key = parts.LastOrDefault(p => p is not ("Ctrl" or "Shift" or "Alt")) ?? string.Empty;
        if (key.Length == 1)
            key = key.ToUpperInvariant();
        return string.Join('+', mods.Append(key));
    }

    private sealed class Registration(KeyboardCommandRouter owner, string chord, Func<Task> handler) : IDisposable
    {
        public void Dispose()
        {
            owner._handlers.TryRemove(new KeyValuePair<string, Func<Task>>(chord, handler));
        }
    }
}
