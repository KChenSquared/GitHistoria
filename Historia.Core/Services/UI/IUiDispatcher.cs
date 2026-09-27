namespace Historia.Services.UI;

/// <summary>
/// The UI thread, abstracted so <see cref="Historia.Core"/> stays free of any UI framework.
/// The App layer implements it over <c>Avalonia.Threading.Dispatcher.UIThread</c>;
/// tests supply a synchronous inline implementation.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the caller is already on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Queues <paramref name="action"/> to run on the UI thread.</summary>
    void Post(Action action);

    /// <summary>
    /// Creates a repeating timer (initially <b>stopped</b>) whose <paramref name="tick"/> runs
    /// on the UI thread. Used for trailing-edge coalescing, so it must support Stop/Start.
    /// </summary>
    IUiTimer CreateTimer(TimeSpan interval, Action tick);
}

/// <summary>A restartable UI-thread timer handed out by <see cref="IUiDispatcher.CreateTimer"/>.</summary>
public interface IUiTimer : IDisposable
{
    void Start();
    void Stop();
}
