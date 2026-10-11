namespace HistoriaApp.Tests;

/// <summary>An <see cref="AvaloniaFactAttribute"/> skipped off Windows, for UI over behaviour that
/// only exists there, such as the registry user PATH and Windows path comparison.</summary>
[AttributeUsage(AttributeTargets.Method)]
[Xunit.Sdk.XunitTestCaseDiscoverer("Avalonia.Headless.XUnit.AvaloniaUIFactDiscoverer", "Avalonia.Headless.XUnit")]
public sealed class WindowsAvaloniaFactAttribute : FactAttribute
{
    public WindowsAvaloniaFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows only.";
    }
}
