namespace Historia.Tests;

public class CloneValidationTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("ssh://git@github.com/owner/repo.git")]
    [InlineData("git@github.com:owner/repo.git")]
    public void DescribeUrlProblem_accepts_well_shaped_urls(string url)
    {
        Assert.Null(CloneValidation.DescribeUrlProblem(url));
    }

    [Fact]
    public void DescribeUrlProblem_accepts_an_existing_local_directory()
    {
        var dir = Directory.CreateTempSubdirectory("historia-clone-validation-");
        try
        {
            Assert.Null(CloneValidation.DescribeUrlProblem(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("just some plain text")]
    [InlineData("-oProxyCommand=calc.exe")]
    [InlineData("ext::sh -c calc.exe")]
    public void DescribeUrlProblem_rejects_garbage_and_security_risks(string url)
    {
        Assert.NotNull(CloneValidation.DescribeUrlProblem(url));
    }

    [Fact]
    public void DescribeDestinationProblem_accepts_a_clean_new_folder()
    {
        var root = Directory.CreateTempSubdirectory("historia-clone-validation-").FullName;
        try
        {
            Assert.Null(CloneValidation.DescribeDestinationProblem(root, $"new-{Guid.NewGuid():N}"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DescribeDestinationProblem_rejects_an_existing_non_empty_folder()
    {
        var root = Directory.CreateTempSubdirectory("historia-clone-validation-").FullName;
        var folder = "existing";
        var target = Path.Combine(root, folder);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "file.txt"), "hi");
        try
        {
            Assert.NotNull(CloneValidation.DescribeDestinationProblem(root, folder));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DescribeDestinationProblem_rejects_a_segment_with_an_invalid_path_character()
    {
        // Verified live against .NET on Windows: '"' is one of Path.GetInvalidPathChars().
        Assert.NotNull(CloneValidation.DescribeDestinationProblem(@"C:\Projects", "\"Application\"}}]"));
    }

    [Theory]
    [InlineData("", "folder")]
    [InlineData("C:\\Projects", "")]
    public void DescribeDestinationProblem_rejects_empty_segments(string root, string folder)
    {
        Assert.NotNull(CloneValidation.DescribeDestinationProblem(root, folder));
    }
}
