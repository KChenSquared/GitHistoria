namespace Historia.ViewModels;

/// <summary>
/// Base class for view models. Anything that must be undone when the view model goes away goes
/// through <see cref="Subscribe"/> or <see cref="Track{T}"/>, so <see cref="Dispose"/> releases it
/// in one place and a long-lived service never keeps a disposed view model alive.
/// </summary>
public abstract class ViewModelBase : ObservableObject, IDisposable
{
    private readonly List<Action> _teardown = [];
    private readonly object _teardownSync = new();

    /// <summary>True once <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>
    /// Runs <paramref name="subscribe"/> now and remembers <paramref name="unsubscribe"/> to run on
    /// <see cref="Dispose"/>. On an already disposed view model nothing is subscribed.
    /// </summary>
    protected void Subscribe(Action subscribe, Action unsubscribe)
    {
        ArgumentNullException.ThrowIfNull(subscribe);
        ArgumentNullException.ThrowIfNull(unsubscribe);
        if (IsDisposed)
            return;

        subscribe();
        lock (_teardownSync)
            _teardown.Add(unsubscribe);
    }

    /// <summary>Remembers <paramref name="disposable"/> to dispose on <see cref="Dispose"/> and returns it.</summary>
    protected T Track<T>(T disposable) where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(disposable);
        if (IsDisposed)
        {
            disposable.Dispose();
            return disposable;
        }

        lock (_teardownSync)
            _teardown.Add(disposable.Dispose);
        return disposable;
    }

    /// <summary>
    /// Undoes every subscription and disposes every tracked item, newest first, then calls
    /// <see cref="OnDisposed"/>. A teardown step that throws is logged and the rest still run.
    /// Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        Action[] teardown;
        lock (_teardownSync)
        {
            if (IsDisposed)
                return;
            IsDisposed = true;
            teardown = [.. _teardown];
            _teardown.Clear();
        }

        // Callbacks run outside the lock so a teardown step can never deadlock against Subscribe.
        for (var i = teardown.Length - 1; i >= 0; i--)
        {
            try { teardown[i](); }
            catch (Exception ex) { AppLog.Write(AppLogLevel.Warn, "ViewModel", "A view model teardown step failed.", ex); }
        }

        OnDisposed();
        GC.SuppressFinalize(this);
    }

    /// <summary>Runs once, after every subscription is undone and every tracked item disposed.</summary>
    protected virtual void OnDisposed()
    {
    }
}
