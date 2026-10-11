namespace Historia.Tests;

/// <summary>Skips a test off Windows, for behaviour that only exists there: the registry PATH,
/// Windows PowerShell and <c>cmd.exe</c> as merge tools, mandatory file locks and Windows path
/// characters.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows only.";
    }
}
