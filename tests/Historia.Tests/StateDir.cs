namespace Historia.Tests;

/// <summary>Redirects <see cref="AppPaths"/> to a throwaway directory for the life of a test.</summary>
public sealed class StateDir : IDisposable
{
    public string Path { get; }

    public StateDir()
    {
        Path = TestScratch.NewDirectory("historia-state-");
        AppPaths.OverrideForTesting(Path);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
