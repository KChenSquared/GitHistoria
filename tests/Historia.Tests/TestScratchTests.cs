namespace Historia.Tests;
[Trait("tier", "unit")]
[Collection("serial")]
public sealed class TestScratchTests
{
    [Fact]
    public void Root_honours_the_environment_variable_and_falls_back()
    {
        var original = Environment.GetEnvironmentVariable("HISTORIA_TEST_TMP");
        try
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", null);
            Assert.Equal(Path.GetTempPath(), TestScratch.Root);

            var custom = Path.Combine(Path.GetTempPath(), "custom-scratch-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", custom);
            Assert.Equal(custom, TestScratch.Root);

            var dir = TestScratch.NewDirectory("unit-test-");
            Assert.StartsWith(custom, dir, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(dir));
            if (Directory.Exists(custom)) Directory.Delete(custom, recursive: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", original);
        }
    }

    [Fact]
    public void PruneStale_removes_only_directories_older_than_the_threshold()
    {
        var original = Environment.GetEnvironmentVariable("HISTORIA_TEST_TMP");
        var custom = Path.Combine(Path.GetTempPath(), "custom-prune-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", custom);
            Directory.CreateDirectory(custom);

            var oldDir = Path.Combine(custom, "historia-tpl-old-" + Guid.NewGuid().ToString("N"));
            var freshDir = Path.Combine(custom, "historia-tpl-fresh-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(oldDir);
            Directory.CreateDirectory(freshDir);

            // Back-date oldDir past 24 hours
            Directory.SetLastWriteTimeUtc(oldDir, DateTime.UtcNow.AddHours(-25));
            Directory.SetLastWriteTimeUtc(freshDir, DateTime.UtcNow);

            TestScratch.PruneStale();

            Assert.False(Directory.Exists(oldDir));
            Assert.True(Directory.Exists(freshDir));

            Directory.Delete(freshDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", original);
            if (Directory.Exists(custom))
                Directory.Delete(custom, recursive: true);
        }
    }
}
