namespace Historia.Tests;

[Trait("tier", "unit")]
[Collection("serial")]
public sealed class TestScratchTests
{
    [Fact]
    public void Root_defaults_to_the_bin_scratch_folder()
    {
        var original = Environment.GetEnvironmentVariable("HISTORIA_TEST_TMP");
        try
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", null);
            var root = TestScratch.Root;
            Assert.False(string.IsNullOrWhiteSpace(root));
            Assert.DoesNotContain(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase);
            Assert.True(root.EndsWith(@"bin\Historia.Tests\scratch", StringComparison.OrdinalIgnoreCase) ||
                        root.EndsWith(@"bin/Historia.Tests/scratch", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", original);
        }
    }

    [Fact]
    public void Root_honours_the_environment_variable()
    {
        var original = Environment.GetEnvironmentVariable("HISTORIA_TEST_TMP");
        try
        {
            var custom = Path.Combine(Path.GetTempPath(), "custom-scratch-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", custom);
            Assert.Equal(custom, TestScratch.Root);

            var dir = TestScratch.NewDirectory("unit-test-");
            Assert.StartsWith(custom, dir, StringComparison.OrdinalIgnoreCase);
            Assert.True(Directory.Exists(dir));
            TestScratch.Delete(custom);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", original);
        }
    }

    [Fact]
    public void Root_falls_back_to_temp_when_metadata_is_missing()
    {
        var original = Environment.GetEnvironmentVariable("HISTORIA_TEST_TMP");
        try
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", null);
            var root = TestScratch.GetRoot(null);
            Assert.Equal(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                         root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", original);
        }
    }

    [Fact]
    public void PruneStale_removes_only_entries_older_than_twelve_hours()
    {
        var original = Environment.GetEnvironmentVariable("HISTORIA_TEST_TMP");
        var custom = Path.Combine(Path.GetTempPath(), "custom-prune-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", custom);
            Directory.CreateDirectory(custom);

            var oldDir = Path.Combine(custom, "old-" + Guid.NewGuid().ToString("N"));
            var freshDir = Path.Combine(custom, "fresh-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(oldDir);
            Directory.CreateDirectory(freshDir);

            Directory.SetLastWriteTimeUtc(oldDir, DateTime.UtcNow.AddHours(-13));
            Directory.SetLastWriteTimeUtc(freshDir, DateTime.UtcNow.AddHours(-11));

            var liveRunDir = Path.Combine(custom, $"historia-run-{Environment.ProcessId}-{Guid.NewGuid():N}"[..30]);
            Directory.CreateDirectory(liveRunDir);
            Directory.SetLastWriteTimeUtc(liveRunDir, DateTime.UtcNow.AddHours(-13));

            var deadPid = 999999;
            while (true)
            {
                try
                {
                    using var p = Process.GetProcessById(deadPid);
                    deadPid--;
                }
                catch (ArgumentException)
                {
                    break;
                }
            }
            var deadRunDir = Path.Combine(custom, $"historia-run-{deadPid}-{Guid.NewGuid():N}"[..30]);
            Directory.CreateDirectory(deadRunDir);
            Directory.SetLastWriteTimeUtc(deadRunDir, DateTime.UtcNow.AddHours(-13));

            TestScratch.PruneStale();

            Assert.False(Directory.Exists(oldDir));
            Assert.True(Directory.Exists(freshDir));
            Assert.True(Directory.Exists(liveRunDir));
            Assert.False(Directory.Exists(deadRunDir));

            TestScratch.Delete(freshDir);
            TestScratch.Delete(liveRunDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HISTORIA_TEST_TMP", original);
            if (Directory.Exists(custom))
                TestScratch.Delete(custom);
        }
    }

    [Fact]
    public void Delete_removes_a_tree_containing_read_only_files()
    {
        var dir = TestScratch.NewDirectory("readonly-test-");
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("init");
            using (var p = Process.Start(psi)!) p.WaitForExit();

            File.WriteAllText(Path.Combine(dir, "f.txt"), "hello");

            var psiUser = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psiUser.ArgumentList.Add("config");
            psiUser.ArgumentList.Add("user.name");
            psiUser.ArgumentList.Add("Test");
            using (var p = Process.Start(psiUser)!) p.WaitForExit();

            var psiEmail = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psiEmail.ArgumentList.Add("config");
            psiEmail.ArgumentList.Add("user.email");
            psiEmail.ArgumentList.Add("test@example.com");
            using (var p = Process.Start(psiEmail)!) p.WaitForExit();

            var psiAdd = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psiAdd.ArgumentList.Add("add");
            psiAdd.ArgumentList.Add(".");
            using (var p = Process.Start(psiAdd)!) p.WaitForExit();

            var psiCommit = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psiCommit.ArgumentList.Add("commit");
            psiCommit.ArgumentList.Add("-m");
            psiCommit.ArgumentList.Add("initial");
            psiCommit.ArgumentList.Add("--no-gpg-sign");
            using (var p = Process.Start(psiCommit)!) p.WaitForExit();

            Assert.True(Directory.Exists(Path.Combine(dir, ".git", "objects")));

            TestScratch.Delete(dir);

            Assert.False(Directory.Exists(dir));
        }
        finally
        {
            if (Directory.Exists(dir))
                TestScratch.Delete(dir);
        }
    }

    [Fact]
    public void NewPath_does_not_create_the_path()
    {
        var path = TestScratch.NewPath("path-test-", ".txt");
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(path));
        Assert.EndsWith(".txt", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NewDirectory_creates_it_under_the_run_folder()
    {
        var dir = TestScratch.NewDirectory("dir-test-");
        try
        {
            Assert.True(Directory.Exists(dir));
            var parent = Directory.GetParent(dir);
            Assert.NotNull(parent);
            Assert.StartsWith($"historia-run-{Environment.ProcessId}-", parent.Name, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestScratch.Delete(dir);
        }
    }

    [Fact]
    public void Git_does_not_discover_the_enclosing_repository_from_scratch()
    {
        if (!TestScratch.IsInsideGitWorkTree(TestScratch.Root))
            return;

        var dir = TestScratch.NewDirectory("git-ceiling-");
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--show-toplevel");
            using var process = Process.Start(psi)!;
            process.WaitForExit();
            Assert.NotEqual(0, process.ExitCode);
        }
        finally
        {
            TestScratch.Delete(dir);
        }
    }
}
