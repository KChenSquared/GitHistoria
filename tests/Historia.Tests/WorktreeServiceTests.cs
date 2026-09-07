using Historia.Services.Git;
using Historia.Services.PowerTools;
using Xunit;

namespace Historia.Tests;

public class WorktreeServiceTests
{
    private static WorktreeService Make(MiniHost host)
        => new((IGitCliService)host.Provider.GetService(typeof(IGitCliService))!);

    private static TestRepositoryBuilder Seeded()
    {
        var b = TestRepositoryBuilder.Create();
        b.WriteFile("f.txt", "x\n").Stage().Commit("c1");
        return b;
    }

    [Fact]
    public async Task List_returns_the_main_worktree()
    {
        await using var host = new MiniHost();
        using var b = Seeded();
        await using var ctx = host.Open(b.Path);

        var list = await Make(host).ListAsync(ctx);
        var main = Assert.Single(list);
        Assert.True(main.IsMainWorktree);
        Assert.Equal("main", main.Branch);
    }

    [Fact]
    public async Task Add_new_branch_worktree_then_remove_it()
    {
        await using var host = new MiniHost();
        using var b = Seeded();
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        var wtPath = Path.Combine(Path.GetDirectoryName(b.Path)!, Path.GetFileName(b.Path) + "-wt");
        await svc.AddAsync(ctx, new WorktreeAddRequest { Path = wtPath, NewBranch = "feature-wt" });

        var list = await svc.ListAsync(ctx);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, w => w.Branch == "feature-wt" && !w.IsMainWorktree);
        Assert.True(Directory.Exists(wtPath));

        await svc.RemoveAsync(ctx, wtPath, force: false);
        Assert.Single(await svc.ListAsync(ctx));
    }

    [Fact]
    public async Task Lock_and_unlock_are_reflected_in_the_listing()
    {
        await using var host = new MiniHost();
        using var b = Seeded();
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        var wtPath = Path.Combine(Path.GetDirectoryName(b.Path)!, Path.GetFileName(b.Path) + "-lk");
        await svc.AddAsync(ctx, new WorktreeAddRequest { Path = wtPath, NewBranch = "lk" });

        await svc.LockAsync(ctx, wtPath, "testing");
        var locked = (await svc.ListAsync(ctx)).Single(w => w.Path.Replace('\\', '/').EndsWith("-lk"));
        Assert.True(locked.IsLocked);

        await svc.UnlockAsync(ctx, wtPath);
        var unlocked = (await svc.ListAsync(ctx)).Single(w => w.Path.Replace('\\', '/').EndsWith("-lk"));
        Assert.False(unlocked.IsLocked);

        await svc.RemoveAsync(ctx, wtPath, force: false);
    }

    [Fact]
    public async Task Prune_runs_without_error()
    {
        await using var host = new MiniHost();
        using var b = Seeded();
        await using var ctx = host.Open(b.Path);
        await Make(host).PruneAsync(ctx); // no throw
    }
}
