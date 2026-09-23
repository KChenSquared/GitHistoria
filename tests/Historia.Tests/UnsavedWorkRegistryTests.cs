namespace Historia.Tests;

public class UnsavedWorkRegistryTests
{
    private sealed class StubUnsavedWork(RepositoryContext context, string description) : IUnsavedWork
    {
        public RepositoryContext Context => context;
        public string Description => description;
        public bool DiscardCalled { get; private set; }
        public bool SaveResult { get; set; } = true;

        public Task<bool> SaveAsync() => Task.FromResult(SaveResult);
        public void Discard() => DiscardCalled = true;
    }

    [Fact]
    public async Task Register_and_dispose_removes_from_registry()
    {
        var registry = new UnsavedWorkRegistry();
        using var b = TestRepositoryBuilder.FromTemplate("unsaved-reg-seed", r => r.WriteFile("init.txt", "1").Stage().Commit("base"));
        await using var host = new MiniHost();
        await using var ctx = host.Open(b.Path);

        var work1 = new StubUnsavedWork(ctx, "file1.cs");
        var work2 = new StubUnsavedWork(ctx, "file2.cs");

        var reg1 = registry.Register(work1);
        var reg2 = registry.Register(work2);

        var all = registry.For(null);
        Assert.Equal(2, all.Count);

        reg1.Dispose();
        all = registry.For(null);
        Assert.Single(all);
        Assert.Same(work2, all[0]);

        // Disposing again is idempotent
        reg1.Dispose();
        all = registry.For(null);
        Assert.Single(all);

        reg2.Dispose();
        Assert.Empty(registry.For(null));
    }

    [Fact]
    public async Task For_filters_by_context_and_null_returns_all()
    {
        var registry = new UnsavedWorkRegistry();
        using var b1 = TestRepositoryBuilder.FromTemplate("unsaved-filter-seed1", r => r.WriteFile("init.txt", "1").Stage().Commit("base"));
        using var b2 = TestRepositoryBuilder.FromTemplate("unsaved-filter-seed2", r => r.WriteFile("init.txt", "1").Stage().Commit("base"));
        await using var host = new MiniHost();
        await using var ctx1 = host.Open(b1.Path);
        await using var ctx2 = host.Open(b2.Path);

        var work1 = new StubUnsavedWork(ctx1, "repo1_file.cs");
        var work2 = new StubUnsavedWork(ctx2, "repo2_file.cs");

        using var r1 = registry.Register(work1);
        using var r2 = registry.Register(work2);

        Assert.Equal(2, registry.For(null).Count);
        var for1 = registry.For(ctx1);
        Assert.Single(for1);
        Assert.Same(work1, for1[0]);

        var for2 = registry.For(ctx2);
        Assert.Single(for2);
        Assert.Same(work2, for2[0]);
    }

    [Fact]
    public async Task Concurrent_register_and_dispose_is_thread_safe()
    {
        var registry = new UnsavedWorkRegistry();
        using var b = TestRepositoryBuilder.FromTemplate("unsaved-concurrent-seed", r => r.WriteFile("init.txt", "1").Stage().Commit("base"));
        await using var host = new MiniHost();
        await using var ctx = host.Open(b.Path);

        const int iterations = 1000;
        Parallel.For(0, iterations, i =>
        {
            var work = new StubUnsavedWork(ctx, $"file_{i}.cs");
            using var reg = registry.Register(work);
            var list = registry.For(ctx);
            Assert.NotNull(list);
        });

        Assert.Empty(registry.For(null));
    }
}
