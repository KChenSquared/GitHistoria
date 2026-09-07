using Historia.Models;
using Historia.Services.Terminal;
using Xunit;

namespace Historia.Tests;

public class TerminalSessionServiceTests
{
    private static TerminalHostEnvironment Host() => new()
    {
        Platform = OSPlatformKind.Linux,
        FileExists = p => p == "/bin/bash",
        DirectoryExists = _ => true,
        GetEnvironmentVariable = _ => null,
        FindOnPath = _ => null,
        GetPasswdShell = () => "/bin/bash",
        GetInheritedEnvironment = () => new Dictionary<string, string> { ["PATH"] = "/usr/bin" },
        HomeDirectory = "/home/me",
        CurrentDirectory = "/tmp",
        ExecutablePath = "/opt/historia/Historia",
        Version = "5.1.0",
    };

    private static (TerminalSessionService svc, FakeTerminalBackend backend) NewService(FakeTerminalBackend? backend = null)
    {
        backend ??= new FakeTerminalBackend();
        var svc = new TerminalSessionService(backend, new FakeSettingsService(), new ImmediateUiDispatcher(), Host());
        return (svc, backend);
    }

    private static TerminalOrigin Repo(string name = "alpha")
        => TerminalOrigin.ForRepository(RepoIdentityKey.FromExplicitId(Guid.Parse($"00000000-0000-0000-0000-0000000000{(name == "alpha" ? "01" : "02")}")), $"/src/{name}", name);

    [Fact]
    public async Task OpenOrFocus_ReusesSessionForSameOrigin()
    {
        var (svc, backend) = NewService();
        var a = await svc.OpenOrFocusAsync(Repo());
        var b = await svc.OpenOrFocusAsync(Repo());
        Assert.Same(a, b);
        Assert.Single(backend.Starts);
        Assert.Single(svc.Sessions);
    }

    [Fact]
    public async Task OpenOrFocus_IsReentrantSafe()
    {
        var backend = new FakeTerminalBackend { StartDelayMs = 40 };
        var (svc, _) = NewService(backend);

        var t1 = svc.OpenOrFocusAsync(Repo());
        var t2 = svc.OpenOrFocusAsync(Repo());
        var s1 = await t1;
        var s2 = await t2;

        Assert.Same(s1, s2);
        Assert.Single(backend.Starts);
    }

    [Fact]
    public async Task OriginEquality_IgnoresWorkingDirectory()
    {
        var (svc, backend) = NewService();
        var key = RepoIdentityKey.FromExplicitId(Guid.NewGuid());

        var first = await svc.OpenOrFocusAsync(TerminalOrigin.ForRepository(key, "/src/before-cd", "r"));
        var second = await svc.OpenOrFocusAsync(TerminalOrigin.ForRepository(key, "/somewhere/else", "r"));

        Assert.Same(first, second);
        Assert.Single(backend.Starts);
    }

    [Fact]
    public async Task HomeAndRepository_AreDistinctOrigins()
    {
        var (svc, backend) = NewService();
        await svc.OpenOrFocusAsync(TerminalOrigin.Home("/home/me"));
        await svc.OpenOrFocusAsync(Repo());
        Assert.Equal(2, backend.Starts.Count);
        Assert.Equal(2, svc.Sessions.Count);
    }

    [Fact]
    public async Task CloseForRepository_ClosesOnlyThatSession()
    {
        var (svc, _) = NewService();
        var repoKey = RepoIdentityKey.FromExplicitId(Guid.NewGuid());
        var repoSession = await svc.OpenOrFocusAsync(TerminalOrigin.ForRepository(repoKey, "/src/x", "x"));
        var homeSession = await svc.OpenOrFocusAsync(TerminalOrigin.Home("/home/me"));

        await svc.CloseForRepositoryAsync(repoKey);

        Assert.DoesNotContain(repoSession, svc.Sessions);
        Assert.Contains(homeSession, svc.Sessions);
    }

    [Fact]
    public async Task SessionExited_RaisedOnce()
    {
        var (svc, backend) = NewService();
        var count = 0;
        svc.SessionExited += (_, _) => count++;

        await svc.OpenOrFocusAsync(Repo());
        backend.Connections[0].EmitExit(0);
        backend.Connections[0].EmitExit(0); // idempotent

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task StartInfo_CarriesResolvedShellDirAndScrubbedEnv()
    {
        var (svc, backend) = NewService();
        await svc.OpenOrFocusAsync(Repo("alpha"));

        var info = backend.Starts[0];
        Assert.Equal("/bin/bash", info.ShellPath);
        Assert.Equal("/src/alpha", info.WorkingDirectory);
        Assert.Equal("xterm-256color", info.Environment["TERM"]);
        Assert.False(info.Environment.ContainsKey("HISTORIA_PERF"));
    }

    [Fact]
    public async Task Close_KillsAndDisposesThePty()
    {
        var (svc, backend) = NewService();
        var s = await svc.OpenOrFocusAsync(Repo());
        await svc.CloseAsync(s.Id);

        Assert.True(backend.Connections[0].KillCount >= 1);
        Assert.True(backend.Connections[0].Disposed);
    }

    [Fact]
    public async Task DisposeIsClean()
    {
        var (svc, backend) = NewService();
        await svc.OpenOrFocusAsync(Repo("alpha"));
        await svc.OpenOrFocusAsync(Repo("beta"));
        svc.Dispose();

        Assert.All(backend.Connections, c => Assert.True(c.Disposed));
        Assert.Empty(svc.Sessions);
    }

    [Fact]
    public async Task FailedSpawn_DoesNotPoisonTheOriginKey()
    {
        var backend = new FakeTerminalBackend { FailWith = _ => new InvalidOperationException("boom") };
        var (svc, _) = NewService(backend);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.OpenOrFocusAsync(Repo()));
        Assert.Empty(svc.Sessions);

        // A later click must be able to retry rather than get the cached faulted task back.
        backend.FailWith = null;
        var s = await svc.OpenOrFocusAsync(Repo());
        Assert.Single(svc.Sessions);
        Assert.Contains(s, svc.Sessions);
    }

    [Fact]
    public async Task CancelledSpawn_SurfacesAsCancellation_NotTimeout()
    {
        // A spawn that never returns on its own, abandoned by the caller cancelling its token.
        var backend = new FakeTerminalBackend { StartDelayMs = 60_000 };
        var (svc, _) = NewService(backend);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Must come back as OperationCanceledException (silent), never TimeoutException (which
        // the service turns into a "timed out starting a shell" error toast).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.OpenOrFocusAsync(Repo(), cts.Token));
        Assert.Empty(svc.Sessions);

        // The origin key is freed, so a later attempt can still open.
        backend.StartDelayMs = 0;
        var s = await svc.OpenOrFocusAsync(Repo());
        Assert.Contains(s, svc.Sessions);
    }

    [Fact]
    public async Task MissingStartDirectory_StillOpens_WithNotice()
    {
        var backend = new FakeTerminalBackend();
        var host = Host() with { DirectoryExists = p => p == "/home/me" };
        var svc = new TerminalSessionService(backend, new FakeSettingsService(), new ImmediateUiDispatcher(), host);

        var s = await svc.OpenOrFocusAsync(TerminalOrigin.ForRepository(RepoIdentityKey.FromExplicitId(Guid.NewGuid()), "/gone", "g"));

        Assert.Equal("/home/me", backend.Starts[0].WorkingDirectory);
        Assert.NotNull(s.StartupNotice);
    }
}
