namespace Historia.Platform;

/// <summary>
/// App-side <see cref="IScrollDirectionProbe"/>. Windows is always standard. macOS reads
/// <c>com.apple.swipescrolldirection</c>; Linux best-effort via <c>gsettings</c> (GNOME). The
/// result is cached; <see cref="Refresh"/> re-probes (wired to window focus).
/// </summary>
public sealed class ScrollDirectionProbe : IScrollDirectionProbe
{
    private volatile bool _natural;

    public ScrollDirectionProbe() => Refresh();

    public bool NaturalScrolling => _natural;

    public void Refresh() => _natural = Probe();

    private static bool Probe()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return false;

            if (OperatingSystem.IsMacOS())
                return ReadCommand("defaults", "read", "-g", "com.apple.swipescrolldirection").Trim() == "1";

            if (OperatingSystem.IsLinux())
            {
                foreach (var device in new[] { "mouse", "touchpad" })
                {
                    var v = ReadCommand("gsettings", "get",
                        $"org.gnome.desktop.peripherals.{device}", "natural-scroll").Trim();
                    if (v.Equals("true", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch
        {
            // No such tool / not permitted — fall back to standard; the explicit setting is the
            // escape hatch there.
        }

        return false;
    }

    private static string ReadCommand(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi);
        if (p is null)
            return "";
        if (!p.WaitForExit(1500))
        {
            try { p.Kill(); } catch { /* ignore */ }
            return "";
        }
        return p.StandardOutput.ReadToEnd();
    }
}
