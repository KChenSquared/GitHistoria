namespace Historia.Tests;

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
            Directory.Delete(dir);
            if (Directory.Exists(custom)) Directory.Delete(custom);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", original);
        }
    }
}
