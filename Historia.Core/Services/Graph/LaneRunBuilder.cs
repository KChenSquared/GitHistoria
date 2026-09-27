namespace Historia.Services.Graph;

/// <summary>
/// A maximal vertical stretch of one lane in one colour, in <b>half-row</b> units relative to
/// the first row fed to <see cref="LaneRunBuilder"/>: row <c>i</c>'s top is <c>2i</c>, its node
/// is <c>2i + 1</c> and its bottom is <c>2i + 2</c>. <see cref="EndHalf"/> is exclusive of the
/// next piece, so a lane through every one of <c>n</c> rows is <c>(0, 2n)</c>.
/// </summary>
public readonly record struct LaneRun(int Lane, int ColorIndex, int StartHalf, int EndHalf);

/// <summary>
/// Merges the per-row vertical lane pieces (<see cref="RowLaneSegment"/>) of the visible rows
/// into maximal <see cref="LaneRun"/>s, so the commit-graph render pass issues one line per
/// run instead of one per row per lane — about lanes plus lane breaks, not rows × lanes.
/// Framework-free so it is unit-testable; the pens and <c>DrawLine</c> calls stay in
/// <c>CommitGraphControl</c>.
/// <para>
/// Several pieces can share one lane in one row (edges converging into a parent lane), in
/// different colours. Every piece in a lane shares an x and a pen width, so on screen the one
/// drawn last covers the rest. The builder keeps exactly that: within a row, a later piece
/// overwrites the colour of the halves it covers (last writer wins), and only the survivor is
/// merged into a run.
/// </para>
/// <para>
/// One instance is reused across frames (Rule 24). Its lane-indexed arrays and run buffer grow
/// on demand by doubling and never shrink; every growth is counted in <see cref="Growths"/>, so
/// a frame after the first must record zero.
/// </para>
/// </summary>
public sealed class LaneRunBuilder
{
    // Per-row scratch, -1 = empty. A lane is "touched" when either half is written this row.
    private int[] _topColor;
    private int[] _bottomColor;
    private int[] _touched;
    private int _touchedCount;

    // The one open run per lane.
    private int[] _runColor;
    private int[] _runStart;
    private int[] _runEnd;
    private bool[] _runOpen;

    private LaneRun[] _runs;
    private int _runCount;

    private int _row;
    private int _highWater;   // one past the highest lane written since Begin

    public LaneRunBuilder(int laneCapacity = 16, int runCapacity = 64)
    {
        laneCapacity = Math.Max(1, laneCapacity);
        _topColor = new int[laneCapacity];
        _bottomColor = new int[laneCapacity];
        Array.Fill(_topColor, -1);
        Array.Fill(_bottomColor, -1);
        _touched = new int[laneCapacity];
        _runColor = new int[laneCapacity];
        _runStart = new int[laneCapacity];
        _runEnd = new int[laneCapacity];
        _runOpen = new bool[laneCapacity];
        _runs = new LaneRun[Math.Max(1, runCapacity)];
    }

    /// <summary>How many times an internal array had to grow since the last
    /// <see cref="ResetCounters"/> (Rule 24's miss counter).</summary>
    public int Growths { get; private set; }

    public void ResetCounters() => Growths = 0;

    /// <summary>Starts a frame. <paramref name="laneCountHint"/> pre-sizes the lane arrays;
    /// lanes past it still work and are counted as a growth.</summary>
    public void Begin(int laneCountHint)
    {
        Array.Clear(_runOpen, 0, _highWater);
        _highWater = 0;
        _row = 0;
        _runCount = 0;
        _touchedCount = 0;
        if (laneCountHint > _topColor.Length)
            GrowLanes(laneCountHint);
    }

    /// <summary>Feeds the next consecutive row's segments (row <c>i</c> is the number of calls
    /// since <see cref="Begin"/>). The span is consumed before returning.</summary>
    public void AddRow(ReadOnlySpan<RowLaneSegment> segments)
    {
        // Pass 1: last writer wins per lane per half.
        foreach (var seg in segments)
        {
            var lane = seg.Lane;
            if (lane < 0)
                continue;
            if (lane >= _topColor.Length)
                GrowLanes(lane + 1);

            if (_topColor[lane] < 0 && _bottomColor[lane] < 0)
                _touched[_touchedCount++] = lane;
            if (seg.Type != LaneSegmentType.NodeToBottom)
                _topColor[lane] = seg.ColorIndex;
            if (seg.Type != LaneSegmentType.TopToNode)
                _bottomColor[lane] = seg.ColorIndex;
        }

        // Pass 2: extend each touched lane's open run, or flush it and open a new one. Untouched
        // lanes keep their open run; it ends at 2i or earlier, so the next piece on that lane is
        // not contiguous and flushes it.
        var top = 2 * _row;
        for (var t = 0; t < _touchedCount; t++)
        {
            var lane = _touched[t];
            if (_topColor[lane] >= 0)
                Feed(lane, _topColor[lane], top);
            if (_bottomColor[lane] >= 0)
                Feed(lane, _bottomColor[lane], top + 1);
            _topColor[lane] = -1;
            _bottomColor[lane] = -1;
            if (lane >= _highWater)
                _highWater = lane + 1;
        }
        _touchedCount = 0;
        _row++;
    }

    /// <summary>Flushes every open run and returns all runs of the frame, in no particular
    /// order. The span is valid until the next <see cref="Begin"/>.</summary>
    public ReadOnlySpan<LaneRun> Finish()
    {
        for (var lane = 0; lane < _highWater; lane++)
        {
            if (!_runOpen[lane])
                continue;
            Flush(lane);
            _runOpen[lane] = false;
        }
        return _runs.AsSpan(0, _runCount);
    }

    private void Feed(int lane, int color, int start)
    {
        if (_runOpen[lane])
        {
            if (_runColor[lane] == color && _runEnd[lane] == start)
            {
                _runEnd[lane] = start + 1;
                return;
            }
            Flush(lane);
        }
        _runOpen[lane] = true;
        _runColor[lane] = color;
        _runStart[lane] = start;
        _runEnd[lane] = start + 1;
    }

    private void Flush(int lane)
    {
        if (_runCount == _runs.Length)
        {
            Array.Resize(ref _runs, _runs.Length * 2);
            Growths++;
        }
        _runs[_runCount++] = new LaneRun(lane, _runColor[lane], _runStart[lane], _runEnd[lane]);
    }

    private void GrowLanes(int needed)
    {
        var old = _topColor.Length;
        var size = Math.Max(needed, old * 2);
        Array.Resize(ref _topColor, size);
        Array.Resize(ref _bottomColor, size);
        Array.Fill(_topColor, -1, old, size - old);
        Array.Fill(_bottomColor, -1, old, size - old);
        Array.Resize(ref _touched, size);
        Array.Resize(ref _runColor, size);
        Array.Resize(ref _runStart, size);
        Array.Resize(ref _runEnd, size);
        Array.Resize(ref _runOpen, size);
        Growths++;
    }
}
