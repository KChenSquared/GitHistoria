namespace Historia.Controls;

internal enum HoverTargetKind
{
    Icon,
    Pill,
    RefListBadge,
    Overflow,
    Message,
}

internal enum DwellStep
{
    Keep,
    Restart,
    Clear,
}

internal readonly record struct HoverTarget(HoverTargetKind Kind, int Row, int Index);

internal sealed class GraphHoverDwell
{
    public static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    private HoverTarget? _pending;
    private HoverTarget? _shown;

    public HoverTarget? Pending => _pending;
    public HoverTarget? Shown => _shown;

    public DwellStep Move(HoverTarget? target)
    {
        if (target is null)
        {
            _pending = null;
            _shown = null;
            return DwellStep.Clear;
        }

        if (target.Equals(_shown) || target.Equals(_pending))
            return DwellStep.Keep;

        _pending = target;
        _shown = null;
        return DwellStep.Restart;
    }

    public HoverTarget? Elapse()
    {
        if (_pending is not { } target)
            return null;

        _shown = target;
        _pending = null;
        return target;
    }

    public void Reset()
    {
        _pending = null;
        _shown = null;
    }
}
