namespace Historia.Platform;

/// <summary>
/// Windows-only caption theming via DWM (the thin remnant of the WPF
/// <c>WindowChromeTheming</c>). Dark caption on 1809+, caption/border/text colours on Win11.
/// A no-op on macOS and Linux, whose window managers theme their own chrome (and Avalonia
/// already follows <c>ActualThemeVariant</c> for the Win32 caption anyway — this just adds the
/// exact palette colour on Win11).
/// </summary>
public static class WindowChrome
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    public static void Apply(IntPtr hwnd, EffectiveTheme theme)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero)
            return;
        ApplyWindows(hwnd, theme);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(IntPtr hwnd, EffectiveTheme theme)
    {
        if (theme == EffectiveTheme.HighContrast)
        {
            int off = 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref off, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref off, sizeof(int));
            return;
        }

        int dark = theme == EffectiveTheme.Dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref dark, sizeof(int));

        if (Environment.OSVersion.Version.Build >= 22000)
        {
            var palette = HistoriaPalette.For(theme);
            int caption = ColorRef(palette["chrome"]);
            int text = ColorRef(palette["caption-text"]);
            int border = ColorRef(palette["border"]);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref text, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
    }

    /// <summary>Historia <see cref="Rgb"/> → Win32 <c>COLORREF</c> (0x00BBGGRR).</summary>
    private static int ColorRef(Rgb c) => c.R | (c.G << 8) | (c.B << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
