using Avalonia.Threading;

namespace Historia.Platform;

/// <summary>App-side <see cref="IUiDispatcher"/> over <see cref="Dispatcher.UIThread"/> (V4 §split).</summary>
public sealed class UiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new Timer(interval, tick);

    private sealed class Timer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        public Timer(TimeSpan interval, Action tick)
        {
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += (_, _) => tick();
        }

        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
        public void Dispose() => _timer.Stop();
    }
}
