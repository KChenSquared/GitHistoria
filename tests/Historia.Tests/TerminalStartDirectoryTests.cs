namespace Historia.Tests;
[Trait("tier", "unit")]

public class TerminalStartDirectoryTests
{
    [Fact]
    public void RepositoryUsesWorkingDirectory()
        => Assert.Equal(@"C:\repo", TerminalStartDirectory.ForRepository(@"C:\repo", @"C:\repo\.git"));

    [Fact]
    public void BareRepositoryUsesRepositoryPath()
        => Assert.Equal(@"C:\bare.git", TerminalStartDirectory.ForRepository(null, @"C:\bare.git"));

    [Fact]
    public void LinkedWorktreeUsesOwnWorkingDirectory()
        => Assert.Equal(@"C:\wt\feature", TerminalStartDirectory.ForRepository(@"C:\wt\feature", @"C:\main\.git\worktrees\feature"));

    [Fact]
    public void PreferredUsedWhenItExists()
        => Assert.Equal("/a", TerminalStartDirectory.Resolve("/a", "/home", "/cwd", p => p == "/a"));

    [Fact]
    public void MissingDirectoryFallsBackToHome()
        => Assert.Equal("/home", TerminalStartDirectory.Resolve("/gone", "/home", "/cwd", p => p == "/home"));

    [Fact]
    public void ThenCurrent()
        => Assert.Equal("/cwd", TerminalStartDirectory.Resolve("/gone", "/gone2", "/cwd", p => p == "/cwd"));

    [Fact]
    public void ThenNull()
        => Assert.Null(TerminalStartDirectory.Resolve("/gone", "/gone2", "/gone3", _ => false));

    [Fact]
    public void NeverThrowsOnNullPreferred()
        => Assert.Equal("/home", TerminalStartDirectory.Resolve(null, "/home", "/cwd", p => p == "/home"));
}
