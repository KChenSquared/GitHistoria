namespace Historia.Tests;

public class ShellResolverTests
{
    private static readonly IReadOnlyList<string> NoArgs = [];

    private static ResolvedShell Resolve(
        string? configured = null,
        IReadOnlyList<string>? args = null,
        OSPlatformKind platform = OSPlatformKind.Linux,
        Func<string, bool>? fileExists = null,
        Func<string, string?>? env = null,
        Func<string, string?>? onPath = null,
        Func<string?>? passwd = null)
        => ShellResolver.Resolve(
            configured, args ?? NoArgs, platform,
            fileExists ?? (_ => false),
            env ?? (_ => null),
            onPath ?? (_ => null),
            passwd ?? (() => null));

    [Fact]
    public void ConfiguredPathWins()
    {
        var r = Resolve(configured: "/opt/fish", fileExists: p => p == "/opt/fish");
        Assert.Equal("/opt/fish", r.Path);
    }

    [Fact]
    public void MissingConfiguredPathThrows_DoesNotFallThrough()
    {
        var ex = Assert.Throws<TerminalStartException>(() =>
            Resolve(configured: "/opt/nope", platform: OSPlatformKind.Linux,
                fileExists: p => p == "/bin/bash")); // bash exists, but must NOT be chosen
        Assert.Equal(TerminalErrorKind.ShellNotFound, ex.Kind);
        Assert.Contains("/opt/nope", ex.Message);
    }

    [Fact]
    public void WindowsChain_PrefersPwsh()
    {
        var r = Resolve(platform: OSPlatformKind.Windows,
            fileExists: p => p is @"C:\pwsh\pwsh.exe" or @"C:\Windows\System32\cmd.exe",
            onPath: name => name == "pwsh.exe" ? @"C:\pwsh\pwsh.exe" : null,
            env: n => n == "ComSpec" ? @"C:\Windows\System32\cmd.exe" : n == "SystemRoot" ? @"C:\Windows" : null);
        Assert.Equal(@"C:\pwsh\pwsh.exe", r.Path);
    }

    [Fact]
    public void WindowsChain_FallsBackToWindowsPowerShell()
    {
        var wp = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        var r = Resolve(platform: OSPlatformKind.Windows,
            fileExists: p => p == wp,
            env: n => n == "SystemRoot" ? @"C:\Windows" : null);
        Assert.Equal(wp, r.Path);
    }

    [Fact]
    public void WindowsChain_FallsBackToComSpec()
    {
        var r = Resolve(platform: OSPlatformKind.Windows,
            fileExists: p => p == @"X:\cmd.exe",
            env: n => n == "ComSpec" ? @"X:\cmd.exe" : n == "SystemRoot" ? @"C:\Windows" : null);
        Assert.Equal(@"X:\cmd.exe", r.Path);
    }

    [Fact]
    public void MacChain_PrefersShellEnv()
    {
        var r = Resolve(platform: OSPlatformKind.MacOS,
            fileExists: p => p is "/usr/local/bin/fish" or "/bin/zsh" or "/bin/sh",
            env: n => n == "SHELL" ? "/usr/local/bin/fish" : null);
        Assert.Equal("/usr/local/bin/fish", r.Path);
    }

    [Fact]
    public void MacChain_FallsBackToZsh()
    {
        var r = Resolve(platform: OSPlatformKind.MacOS, fileExists: p => p is "/bin/zsh" or "/bin/sh");
        Assert.Equal("/bin/zsh", r.Path);
    }

    [Fact]
    public void LinuxChain_UsesPasswdShell()
    {
        var r = Resolve(platform: OSPlatformKind.Linux,
            fileExists: p => p is "/usr/bin/zsh" or "/bin/bash" or "/bin/sh",
            passwd: () => "/usr/bin/zsh");
        Assert.Equal("/usr/bin/zsh", r.Path);
    }

    [Fact]
    public void LinuxChain_FallsBackToBashThenSh()
    {
        Assert.Equal("/bin/bash", Resolve(platform: OSPlatformKind.Linux, fileExists: p => p is "/bin/bash" or "/bin/sh").Path);
        Assert.Equal("/bin/sh", Resolve(platform: OSPlatformKind.Linux, fileExists: p => p == "/bin/sh").Path);
    }

    [Fact]
    public void ArgumentsPassThroughVerbatim()
    {
        var r = Resolve(configured: "/bin/bash", args: ["--norc", "-i"], fileExists: _ => true);
        Assert.Equal(["--norc", "-i"], r.Arguments);
    }

    [Fact]
    public void NoLoginFlagByDefault()
    {
        var r = Resolve(platform: OSPlatformKind.Linux, fileExists: p => p == "/bin/bash");
        Assert.Empty(r.Arguments);
    }
}
