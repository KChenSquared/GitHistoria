
namespace Historia.Services.UI;

/// <summary>
/// Reports whether the OS is set to "natural / inverted" scrolling. Windows is always
/// standard; on macOS / Linux this reflects the system preference. The value is cached and
/// re-probed on demand (window focus).
/// </summary>
public interface IScrollDirectionProbe
{
    /// <summary>True when the OS reverses wheel / trackpad scroll (macOS "natural", GNOME
    /// "natural-scroll"). Always false on Windows.</summary>
    bool NaturalScrolling { get; }

    /// <summary>Re-read the OS preference (cheap; result is cached until the next call).</summary>
    void Refresh();
}

/// <summary>Pure wheel-direction resolution shared by the custom wheel handlers (graph body,
/// tab strip). A raw wheel delta is multiplied by this sign before the handler's
/// Windows-tuned math runs.</summary>
public static class WheelInput
{
    public static double Sign(WheelScrollDirection setting, bool naturalScrolling) => setting switch
    {
        WheelScrollDirection.Standard => 1,
        WheelScrollDirection.Inverted => -1,
        _ => naturalScrolling ? -1 : 1, // Auto
    };
}
