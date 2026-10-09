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
        TestScratch.Delete(Path);
    }
}
