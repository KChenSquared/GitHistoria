namespace Historia.Tests;

[Trait("tier", "unit")]
public sealed class LogTrimFailureStoreTests : IDisposable
{
    private readonly string _dir;

    public LogTrimFailureStoreTests()
    {
        _dir = TestScratch.NewDirectory("trim-failure-store-");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch { }
    }

    [Fact]
    public void Missing_file_returns_zero()
    {
        var file = Path.Combine(_dir, "missing.json");
        var store = new LogTrimFailureStore(file);
        Assert.Equal(0, store.Load());
    }

    [Fact]
    public void Corrupt_json_returns_zero()
    {
        var file = Path.Combine(_dir, "corrupt.json");
        File.WriteAllText(file, "{ bad-json");
        var store = new LogTrimFailureStore(file);
        Assert.Equal(0, store.Load());
    }

    [Fact]
    public void Negative_count_returns_zero()
    {
        var file = Path.Combine(_dir, "negative.json");
        File.WriteAllText(file, @"{ ""consecutiveFailures"": -5 }");
        var store = new LogTrimFailureStore(file);
        Assert.Equal(0, store.Load());
    }

    [Fact]
    public void Round_trip_persists_count()
    {
        var file = Path.Combine(_dir, "state.json");
        var store = new LogTrimFailureStore(file);
        store.Save(2);
        Assert.Equal(2, store.Load());

        store.Save(3);
        Assert.Equal(3, store.Load());

        store.Save(0);
        Assert.Equal(0, store.Load());
    }

    [Fact]
    public void Save_to_path_whose_directory_is_a_file_does_not_throw()
    {
        var dummyFile = Path.Combine(_dir, "as-file");
        File.WriteAllText(dummyFile, "file content");

        var badStatePath = Path.Combine(dummyFile, "nested", "state.json");
        var store = new LogTrimFailureStore(badStatePath);
        store.Save(1);
    }
}
