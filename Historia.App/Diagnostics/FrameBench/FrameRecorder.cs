namespace Historia.Diagnostics;

/// <summary>
/// Records one workload's frames by chaining <see cref="TopLevel.RequestAnimationFrame"/>: each
/// callback timestamps itself, drains the graph render time <see cref="FrameBench"/> accumulated
/// since the previous callback, polls the GC counters, steps the workload, and asks for the next
/// frame until the workload says it is done.
/// <para>
/// In Avalonia 11.3 a requested animation frame runs at the start of the UI thread's next render
/// pass (<c>MediaContext.RenderCore</c> pulses the clock first, then layout and the visuals'
/// <c>Render</c> are committed to the compositor). While a commit is in flight the next pass waits
/// for the compositor to take it, and the compositor takes batches once per render tick of its
/// render timer — on Windows the DWM/WinUI composition commit, which is paced by the display's
/// refresh. So with a commit every frame the callbacks fire once per composited frame, and an
/// interval between two callbacks is the time between two UI render passes: it stretches when the
/// UI thread's layout and render work, or the compositor, misses a vblank. A pass with nothing
/// to commit is paced by a 16 ms dispatcher timer instead, so every callback here requests a
/// commit to keep the loop on the compositor's clock even when a frame changes nothing.
/// </para>
/// <para>
/// The buffers are allocated before the first frame; a callback allocates nothing of its own,
/// except one GC info read on a frame in which a collection happened.
/// </para>
/// </summary>
internal sealed class FrameRecorder
{
    private readonly TopLevel _topLevel;
    private readonly Action<TimeSpan> _onFrame;
    private readonly long[] _stamps;
    private readonly long[] _renderTicks;
    private readonly int[] _renderCounts;
    private Avalonia.Rendering.Composition.Compositor? _compositor;

    private IFrameWorkload? _workload;
    private TaskCompletionSource? _done;
    private int _count;
    private bool _overflowed;
    private Exception? _error;

    private GcSnapshot _gcBefore;
    private GcSnapshot _gcAfter;
    private int _lastGen0;
    private int _lastGen1;
    private int _lastGen2;
    private TimeSpan _lastPause;
    private TimeSpan _maxPause;

    public FrameRecorder(TopLevel topLevel, double seconds)
    {
        _topLevel = topLevel;
        _onFrame = OnFrame;
        // Room for a 360 Hz display plus slack; a longer run stops recording and is flagged.
        var capacity = Math.Max(4_096, (int)Math.Ceiling(seconds * 400) + 1_024);
        _stamps = new long[capacity];
        _renderTicks = new long[capacity];
        _renderCounts = new int[capacity];
    }

    /// <summary>Runs <paramref name="workload"/> frame by frame until it reports done, then
    /// returns what was recorded. Must be called on the UI thread.</summary>
    public async Task<FrameCapture> RecordAsync(IFrameWorkload workload)
    {
        Dispatcher.UIThread.VerifyAccess();
        _workload = workload;
        _count = 0;
        _overflowed = false;
        _error = null;
        _maxPause = TimeSpan.Zero;
        _compositor = Avalonia.Rendering.Composition.ElementComposition.GetElementVisual(_topLevel)?.Compositor;
        _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _topLevel.RequestAnimationFrame(_onFrame);
        RequestCommit();
        await _done.Task.ConfigureAwait(true);
        return BuildCapture();
    }

    private void OnFrame(TimeSpan _)
    {
        var now = Stopwatch.GetTimestamp();
        var more = false;
        try
        {
            var i = _count;
            _stamps[i] = now;
            var (ticks, renders) = FrameBench.TakeGraphRender();
            if (i == 0)
            {
                _gcBefore = TakeGcSnapshot();
                _lastGen0 = _gcBefore.Gen0Collections;
                _lastGen1 = _gcBefore.Gen1Collections;
                _lastGen2 = _gcBefore.Gen2Collections;
                _lastPause = _gcBefore.TotalPause;
                _workload!.Begin();
            }
            else
            {
                _renderTicks[i] = ticks;
                _renderCounts[i] = renders;
                PollGc();
            }
            _count = i + 1;

            var elapsed = (double)(now - _stamps[0]) / Stopwatch.Frequency;
            more = _workload!.Step(elapsed);
            if (more && _count >= _stamps.Length)
            {
                _overflowed = true;
                more = false;
            }
        }
        catch (Exception ex)
        {
            _error = ex;
            more = false;
        }

        if (more)
        {
            _topLevel.RequestAnimationFrame(_onFrame);
            RequestCommit();
            return;
        }

        _gcAfter = TakeGcSnapshot();
        _done?.TrySetResult();
    }

    private void RequestCommit() => _compositor?.RequestCompositionBatchCommitAsync();

    // The longest single pause: on a frame with one collection the frame's pause delta is exact;
    // with several, the last one's own pause is read and the rest are counted as one, which can
    // only overstate. FrameStats caps it at the window's total.
    private void PollGc()
    {
        var g0 = GC.CollectionCount(0);
        var g1 = GC.CollectionCount(1);
        var g2 = GC.CollectionCount(2);
        if (g0 == _lastGen0 && g1 == _lastGen1 && g2 == _lastGen2)
            return;

        var collections = Math.Max(g0 - _lastGen0, Math.Max(g1 - _lastGen1, g2 - _lastGen2));
        var pause = GC.GetTotalPauseDuration();
        var delta = pause - _lastPause;
        var single = delta;
        if (collections > 1)
        {
            var last = TimeSpan.Zero;
            foreach (var p in GC.GetGCMemoryInfo(GCKind.Any).PauseDurations)
                last += p;
            single = last > delta - last ? last : delta - last;
        }
        if (single > _maxPause)
            _maxPause = single;

        _lastGen0 = g0;
        _lastGen1 = g1;
        _lastGen2 = g2;
        _lastPause = pause;
    }

    private static GcSnapshot TakeGcSnapshot() => new(
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        GC.GetTotalPauseDuration(),
        GC.GetTotalAllocatedBytes(false));

    private FrameCapture BuildCapture()
    {
        var n = _count;
        var stamps = new double[n];
        var intervals = new double[Math.Max(0, n - 1)];
        var render = new double[Math.Max(0, n - 1)];
        var renderCounts = new int[Math.Max(0, n - 1)];
        var renderSamples = new List<double>(n);
        var toMs = 1000.0 / Stopwatch.Frequency;
        for (var i = 0; i < n; i++)
        {
            stamps[i] = (_stamps[i] - _stamps[0]) * toMs;
            if (i == 0)
                continue;
            intervals[i - 1] = (_stamps[i] - _stamps[i - 1]) * toMs;
            render[i - 1] = _renderTicks[i] * toMs;
            renderCounts[i - 1] = _renderCounts[i];
            if (_renderCounts[i] > 0)
                renderSamples.Add(render[i - 1]);
        }

        var seconds = n > 1 ? (_stamps[n - 1] - _stamps[0]) / (double)Stopwatch.Frequency : 0;
        var gc = new GcDelta(_gcBefore, _gcAfter, _maxPause);
        return new FrameCapture(stamps, intervals, render, renderCounts, renderSamples.ToArray(), gc, seconds, _overflowed, _error);
    }
}

/// <summary>
/// One workload's recording. <see cref="TimesMs"/> holds every callback's time from the first;
/// <see cref="IntervalsMs"/>, <see cref="RenderMs"/> and <see cref="RenderCounts"/> are per frame,
/// one entry per callback after the first: the frame's interval and the graph render time spent
/// producing it (the render passes between the two callbacks). <see cref="RenderSamplesMs"/> keeps
/// only the frames that rendered the graph.
/// </summary>
internal sealed record FrameCapture(
    double[] TimesMs,
    double[] IntervalsMs,
    double[] RenderMs,
    int[] RenderCounts,
    double[] RenderSamplesMs,
    GcDelta Gc,
    double Seconds,
    bool Overflowed,
    Exception? Error)
{
    public FrameSummary Summarize() => FrameStats.Summarize(IntervalsMs, RenderSamplesMs, Gc, Seconds);
}
