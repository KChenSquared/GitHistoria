using Historia.Services.Storage;

namespace Historia.Tests;

/// <summary>Redirects <see cref="AppPaths"/> to a throwaway directory for the life of a test.</summary>
public sealed class StateDir : IDisposable
{
    public string Path { get; }

    public StateDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
        AppPaths.OverrideForTesting(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
    }
}
