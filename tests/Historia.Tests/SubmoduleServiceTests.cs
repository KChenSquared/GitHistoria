namespace Historia.Tests;

public class SubmoduleServiceTests : IDisposable
{
    private static SubmoduleService Make(MiniHost host)
        => new((IGitCliService)host.Provider.GetService(typeof(IGitCliService))!);

    public SubmoduleServiceTests()
    {
        // Allow local-path submodules for the test (blocked by default since CVE-2022-39253).
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", null);
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", null);
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", null);
    }

    [Fact]
    public async Task Add_lists_the_submodule_and_remove_deletes_it_and_its_cached_module()
    {
        await using var host = new MiniHost();

        using var inner = TestRepositoryBuilder.FromTemplate("submodule-inner-seed", r =>
            r.WriteFile("lib.txt", "lib\n").Stage().Commit("lib c1"));

        using var outerB = TestRepositoryBuilder.FromTemplate("submodule-outer-seed", r =>
        {
            r.WriteFile("app.txt", "app\n").Stage().Commit("app c1");
            // Local-path submodules need this opt-in on modern Git (CVE-2022-39253).
            r.Git("config", "protocol.file.allow", "always");
        });

        await using var ctx = host.Open(outerB.Path);
        var svc = Make(host);

        await svc.AddAsync(ctx, inner.Path.Replace('\\', '/'), "vendor/lib");

        var list = await svc.ListAsync(ctx);
        var sub = Assert.Single(list);
        Assert.Equal("vendor/lib", sub.Path);
        Assert.Equal(SubmoduleStatus.Clean, sub.Status);

        await svc.RemoveAsync(ctx, sub, default);

        Assert.Empty(await svc.ListAsync(ctx));
        Assert.False(Directory.Exists(Path.Combine(ctx.GitDirectory, "modules", "vendor/lib")));
        Assert.False(Directory.Exists(Path.Combine(outerB.Path, "vendor", "lib")));
    }

    [Fact]
    public async Task An_empty_repo_reports_no_submodules()
    {
        await using var host = new MiniHost();
        using var b = RepoTemplates.Linear(1);
        await using var ctx = host.Open(b.Path);
        Assert.Empty(await Make(host).ListAsync(ctx));
    }
}
