namespace Historia.Tests;

[Trait("tier", "git")]

public class ConflictServiceTests
{
    private static ConflictService Make(MiniHost host)
        => new((IGitCliService)host.Provider.GetService(typeof(IGitCliService))!, host.Settings);

    private static TestRepositoryBuilder ConflictedRepo() =>
        TestRepositoryBuilder.FromTemplate("conflicted-repo", repo =>
        {
            repo.WriteFile("f.txt", "line1\nshared\nline3\n").Stage().Commit("base");
            repo.Checkout("feature", create: true).WriteFile("f.txt", "line1\nTHEIRS\nline3\n").Stage().Commit("theirs");
            repo.Checkout("main").WriteFile("f.txt", "line1\nOURS\nline3\n").Stage().Commit("ours");
            try { repo.Git("merge", "feature"); } catch { /* expected to conflict */ }
        });

    [Fact]
    public async Task Lists_conflicts_from_index_stages()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var conflicts = await svc.GetConflictsAsync(ctx);
        var file = Assert.Single(conflicts);
        Assert.Equal("f.txt", file.FilePath);
        Assert.Equal(ConflictStatus.BothModified, file.Status);
    }

    [Fact]
    public async Task Load_working_copy_returns_markers_not_empty()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var copy = await svc.LoadWorkingCopyAsync(ctx, "f.txt");
        Assert.NotNull(copy.Text);
        Assert.Contains("<<<<<<<", copy.Text);
        Assert.Contains("OURS", copy.Text);
        Assert.Contains("THEIRS", copy.Text);
        Assert.False(copy.IsBinary);
        Assert.False(copy.ExceedsBudget);
        Assert.Equal(7, copy.MarkerSize);
        Assert.False(string.IsNullOrEmpty(copy.DiskHash));
    }

    [Fact]
    public async Task Load_working_copy_utf16_bom_is_text_not_binary()
    {
        await using var host = new MiniHost();
        using var repoDir = TestRepositoryBuilder.Create();
        repoDir.WriteFile(".gitattributes", "*.txt text working-tree-encoding=UTF-16LE-BOM\n");
        repoDir.Stage(".gitattributes").Commit("attributes");

        var preamble = new byte[] { 0xFF, 0xFE };
        byte[] Utf16(string s) => preamble.Concat(Encoding.Unicode.GetBytes(s)).ToArray();

        File.WriteAllBytes(Path.Combine(repoDir.Path, "f.txt"), Utf16("base\n"));
        repoDir.Stage("f.txt").Commit("base");

        repoDir.Checkout("feature", create: true);
        File.WriteAllBytes(Path.Combine(repoDir.Path, "f.txt"), Utf16("theirs\n"));
        repoDir.Stage("f.txt").Commit("theirs");

        repoDir.Checkout("main");
        File.WriteAllBytes(Path.Combine(repoDir.Path, "f.txt"), Utf16("ours\n"));
        repoDir.Stage("f.txt").Commit("ours");

        try { repoDir.Git("merge", "feature"); } catch { }

        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var copy = await svc.LoadWorkingCopyAsync(ctx, "f.txt");
        Assert.False(copy.IsBinary);
        Assert.NotNull(copy.Text);
        Assert.NotNull(copy.Format);
        Assert.True(copy.Format.Value.HasBom);
        Assert.Contains("<<<<<<<", copy.Text);
    }

    [Fact]
    public async Task Load_working_copy_utf8_bom_region_on_first_line()
    {
        await using var host = new MiniHost();
        using var repoDir = TestRepositoryBuilder.Create();

        var preamble = new byte[] { 0xEF, 0xBB, 0xBF };
        byte[] Utf8Bom(string s) => preamble.Concat(Encoding.UTF8.GetBytes(s)).ToArray();

        // Write a conflict marker starting on byte 3 (line 1 of decoded text)
        File.WriteAllBytes(Path.Combine(repoDir.Path, "f.txt"), Utf8Bom("<<<<<<< HEAD\nOURS\n=======\nTHEIRS\n>>>>>>> feature\n"));

        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var copy = await svc.LoadWorkingCopyAsync(ctx, "f.txt");
        Assert.NotNull(copy.Text);
        Assert.False(copy.Text.StartsWith('\uFEFF'));
        Assert.StartsWith("<<<<<<<", copy.Text);
        Assert.True(copy.Format?.HasBom);
    }

    [Fact]
    public async Task Take_theirs_preserves_bom_and_crlf()
    {
        await using var host = new MiniHost();
        using var repoDir = TestRepositoryBuilder.Create();
        repoDir.Git("config", "core.autocrlf", "true");

        var preamble = new byte[] { 0xEF, 0xBB, 0xBF };
        byte[] Utf8Bom(string s) => preamble.Concat(Encoding.UTF8.GetBytes(s)).ToArray();

        File.WriteAllBytes(Path.Combine(repoDir.Path, "f.txt"), Utf8Bom("base\r\n"));
        repoDir.Stage("f.txt").Commit("base");

        repoDir.Checkout("feature", create: true);
        File.WriteAllBytes(Path.Combine(repoDir.Path, "f.txt"), Utf8Bom("theirs\r\n"));
        repoDir.Stage("f.txt").Commit("theirs");

        repoDir.Checkout("main");
        File.WriteAllBytes(Path.Combine(repoDir.Path, "f.txt"), Utf8Bom("ours\r\n"));
        repoDir.Stage("f.txt").Commit("ours");

        try { repoDir.Git("merge", "feature"); } catch { }

        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        await svc.ResolveWithSideAsync(ctx, "f.txt", ConflictSide.Theirs);

        var onDisk = await File.ReadAllBytesAsync(Path.Combine(repoDir.Path, "f.txt"));
        Assert.Equal(preamble, onDisk[..preamble.Length]);
        var text = Encoding.UTF8.GetString(onDisk[3..]);
        Assert.Equal("theirs\r\n", text);
    }

    [Fact]
    public async Task Save_resolution_preserves_bom_and_stages()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var copy = await svc.LoadWorkingCopyAsync(ctx, "f.txt");
        var bomFormat = new TextFileFormat(new UTF8Encoding(true), HasBom: true);
        var res = await svc.SaveResolutionAsync(ctx, "f.txt", "resolved content\r\n", bomFormat, copy.DiskHash, stage: true);

        Assert.True(res.Saved);
        var onDisk = await File.ReadAllBytesAsync(Path.Combine(repoDir.Path, "f.txt"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, onDisk[..3]);

        var conflicts = await svc.GetConflictsAsync(ctx);
        Assert.Empty(conflicts);
    }

    [Fact]
    public async Task Save_resolution_refuses_when_file_changed_on_disk()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var copy = await svc.LoadWorkingCopyAsync(ctx, "f.txt");
        var full = Path.Combine(repoDir.Path, "f.txt");

        await File.WriteAllTextAsync(full, "external change\n");

        var res = await svc.SaveResolutionAsync(ctx, "f.txt", "my edits", copy.Format ?? default, copy.DiskHash, stage: false);

        Assert.False(res.Saved);
        var currentText = await File.ReadAllTextAsync(full);
        Assert.Equal("external change\n", currentText);
        var externalHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("external change\n")));
        Assert.Equal(externalHash, res.DiskHash);
    }

    [Fact]
    public async Task Save_resolution_leaves_no_temp_file_behind()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var copy = await svc.LoadWorkingCopyAsync(ctx, "f.txt");
        var res = await svc.SaveResolutionAsync(ctx, "f.txt", "resolved\n", copy.Format ?? default, copy.DiskHash, stage: false);

        Assert.True(res.Saved);
        Assert.Equal("resolved\n", await File.ReadAllTextAsync(Path.Combine(repoDir.Path, "f.txt")));
        Assert.Empty(Directory.GetFiles(repoDir.Path, ".historia-save-*"));
    }

    [Fact]
    public async Task Save_resolution_keeps_the_files_attributes()
    {
        if (!OperatingSystem.IsWindows())
            return; // Windows file attributes

        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);
        var full = Path.Combine(repoDir.Path, "f.txt");

        var copy = await svc.LoadWorkingCopyAsync(ctx, "f.txt");
        File.SetAttributes(full, File.GetAttributes(full) | FileAttributes.Hidden);
        var res = await svc.SaveResolutionAsync(ctx, "f.txt", "resolved\n", copy.Format ?? default, copy.DiskHash, stage: false);

        Assert.True(res.Saved);
        Assert.Equal("resolved\n", await File.ReadAllTextAsync(full));
        Assert.True(File.GetAttributes(full).HasFlag(FileAttributes.Hidden), "the saved file lost its Hidden attribute");
        Assert.Empty(Directory.GetFiles(repoDir.Path, ".historia-save-*"));
    }

    [Fact]
    public async Task Disk_hash_is_readable_while_another_process_holds_the_file_open()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var full = Path.Combine(repoDir.Path, "f.txt");
        var expected = await svc.GetDiskHashAsync(ctx, "f.txt");
        await using var holder = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(expected, await svc.GetDiskHashAsync(ctx, "f.txt"));
    }

    [Fact]
    public async Task Recreate_conflict_writes_zdiff3_base_section()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        await svc.RecreateConflictAsync(ctx, "f.txt");

        var text = await File.ReadAllTextAsync(Path.Combine(repoDir.Path, "f.txt"));
        Assert.Contains("|||||||", text);
        Assert.Contains("shared", text);
    }

    [Fact]
    public async Task Side_labels_merge_and_rebase()
    {
        // 1. Merge labels
        {
            await using var host = new MiniHost();
            using var repoDir = ConflictedRepo();
            await using var ctx = host.Open(repoDir.Path);
            var svc = Make(host);

            var labels = await svc.GetSideLabelsAsync(ctx);
            Assert.Equal(OperationState.Merging, labels.Operation);
            Assert.Equal("main", labels.OursTitle);
            Assert.Equal("feature", labels.TheirsTitle);
        }

        // 2. Rebase labels
        {
            await using var host = new MiniHost();
            using var repoDir = TestRepositoryBuilder.Create();
            repoDir.WriteFile("f.txt", "base\n").Stage().Commit("base");
            repoDir.Checkout("feature", create: true).WriteFile("f.txt", "feature\n").Stage().Commit("commit on feature");
            repoDir.Checkout("main").WriteFile("f.txt", "main\n").Stage().Commit("commit on main");
            repoDir.Checkout("feature");
            try { repoDir.Git("rebase", "main"); } catch { }

            await using var ctx = host.Open(repoDir.Path);
            var svc = Make(host);

            var labels = await svc.GetSideLabelsAsync(ctx);
            Assert.Equal(OperationState.Rebasing, labels.Operation);
            Assert.Contains("onto", labels.OursTitle);
            Assert.Contains("feature", labels.TheirsTitle);
        }
    }

    [Fact]
    public async Task Merge_by_commit_sha_theirs_title_is_short_sha()
    {
        await using var host = new MiniHost();
        using var repoDir = TestRepositoryBuilder.Create();
        repoDir.WriteFile("f.txt", "base\n").Stage().Commit("base");
        repoDir.Checkout("feature", create: true).WriteFile("f.txt", "feature\n").Stage().Commit("theirs");
        var sha = repoDir.Git("rev-parse", "HEAD").Trim();
        repoDir.Checkout("main").WriteFile("f.txt", "main\n").Stage().Commit("ours");
        try { repoDir.Git("merge", sha); } catch { /* expected to conflict */ }

        await using var ctx = host.Open(repoDir.Path);
        var labels = await Make(host).GetSideLabelsAsync(ctx);

        Assert.Equal(OperationState.Merging, labels.Operation);
        Assert.Equal(sha[..7], labels.TheirsTitle);
    }

    [Theory]
    [InlineData("Merge branch 'feature'", "feature")]
    [InlineData("Merge branch 'feature' into main", "feature")]
    [InlineData("Merge remote-tracking branch 'origin/feature'", "origin/feature")]
    [InlineData("Merge tag 'v1.0'", "v1.0")]
    [InlineData("Merge commit '0123456789abcdef0123456789abcdef01234567'", "0123456")]
    [InlineData("Merge commit '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'", "0123456")]
    [InlineData("Merge branches 'a' and 'b'", "Merge branches 'a' and 'b'")]
    [InlineData("Custom merge message", "Custom merge message")]
    [InlineData("", "")]
    public void MergeSourceName_parses_merge_messages(string firstLine, string expected)
        => Assert.Equal(expected, ConflictService.MergeSourceName(firstLine));

    [Fact]
    public async Task Stop_id_is_stable_across_staging_and_changes_at_next_stop()
    {
        await using var host = new MiniHost();
        using var repoDir = TestRepositoryBuilder.Create();
        repoDir.WriteFile("f1.txt", "base1\n").WriteFile("f2.txt", "base2\n").Stage().Commit("base");

        repoDir.Checkout("feature", create: true);
        repoDir.WriteFile("f1.txt", "feature1\n").Stage().Commit("conflict1");
        repoDir.WriteFile("f2.txt", "feature2\n").Stage().Commit("conflict2");

        repoDir.Checkout("main");
        repoDir.WriteFile("f1.txt", "main1\n").WriteFile("f2.txt", "main2\n").Stage().Commit("main changes");

        repoDir.Checkout("feature");
        try { repoDir.Git("rebase", "main"); } catch { }

        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var labelsStop1 = await svc.GetSideLabelsAsync(ctx);
        var stop1Id = labelsStop1.StopId;

        // Stage the first conflict
        repoDir.WriteFile("f1.txt", "resolved1\n").Stage("f1.txt");

        var labelsStop1AfterStage = await svc.GetSideLabelsAsync(ctx);
        Assert.Equal(stop1Id, labelsStop1AfterStage.StopId);

        // Continue rebase to reach second conflict
        var env = new Dictionary<string, string> { ["GIT_EDITOR"] = "true" };
        try { repoDir.Git(env, "rebase", "--continue"); } catch { }

        var labelsStop2 = await svc.GetSideLabelsAsync(ctx);
        Assert.NotEqual(stop1Id, labelsStop2.StopId);
    }

    [Fact]
    public async Task Take_deleted_side_removes_path()
    {
        await using var host = new MiniHost();
        using var repoDir = TestRepositoryBuilder.Create();
        repoDir.WriteFile("f.txt", "base\n").Stage().Commit("base");

        repoDir.Checkout("feature", create: true);
        repoDir.Git("rm", "f.txt");
        repoDir.Commit("deleted on feature");

        repoDir.Checkout("main");
        repoDir.WriteFile("f.txt", "modified on main\n").Stage().Commit("modified");

        try { repoDir.Git("merge", "feature"); } catch { }

        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var conflicts = await svc.GetConflictsAsync(ctx);
        var c = Assert.Single(conflicts);
        Assert.Equal(ConflictStatus.DeletedByThem, c.Status);

        await svc.ResolveWithSideAsync(ctx, "f.txt", ConflictSide.Theirs);

        Assert.False(File.Exists(Path.Combine(repoDir.Path, "f.txt")));
        Assert.Empty(await svc.GetConflictsAsync(ctx));
    }

    [Fact]
    public async Task Take_side_on_binary_conflict_keeps_file()
    {
        await using var host = new MiniHost();
        using var repoDir = TestRepositoryBuilder.Create();

        var baseBytes = new byte[] { 0x00, 0x01, 0x02 };
        var theirsBytes = new byte[] { 0x00, 0x01, 0xAA };
        var oursBytes = new byte[] { 0x00, 0x01, 0xBB };

        File.WriteAllBytes(Path.Combine(repoDir.Path, "bin.dat"), baseBytes);
        repoDir.Stage("bin.dat").Commit("base");

        repoDir.Checkout("feature", create: true);
        File.WriteAllBytes(Path.Combine(repoDir.Path, "bin.dat"), theirsBytes);
        repoDir.Stage("bin.dat").Commit("theirs");

        repoDir.Checkout("main");
        File.WriteAllBytes(Path.Combine(repoDir.Path, "bin.dat"), oursBytes);
        repoDir.Stage("bin.dat").Commit("ours");

        try { repoDir.Git("merge", "feature"); } catch { }

        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var conflicts = await svc.GetConflictsAsync(ctx);
        var c = Assert.Single(conflicts);
        Assert.True(c.IsBinary);

        await svc.ResolveWithSideAsync(ctx, "bin.dat", ConflictSide.Theirs);

        Assert.True(File.Exists(Path.Combine(repoDir.Path, "bin.dat")));
        var diskBytes = await File.ReadAllBytesAsync(Path.Combine(repoDir.Path, "bin.dat"));
        Assert.Equal(theirsBytes, diskBytes);
        Assert.Empty(await svc.GetConflictsAsync(ctx));
    }

    [Fact]
    public async Task Take_absent_side_of_add_removes_path()
    {
        await using var host = new MiniHost();
        using var repoDir = TestRepositoryBuilder.Create();
        repoDir.WriteFile("init.txt", "init\n").Stage().Commit("init");

        // Set up index conflict: AddedByUs (stage 2 present, stage 3 absent)
        repoDir.WriteFile("added.txt", "content\n");
        var sha = repoDir.Git("hash-object", "-w", "added.txt").Trim();
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoDir.Path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("update-index");
        psi.ArgumentList.Add("--index-info");
        using (var p = Process.Start(psi)!)
        {
            using (var sw = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                sw.Write($"100644 {sha} 2\tadded.txt\n");
            }
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException($"update-index failed ({p.ExitCode}): {err}");
        }

        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var conflicts = await svc.GetConflictsAsync(ctx);
        var c = Assert.Single(conflicts);
        Assert.Equal(ConflictStatus.AddedByUs, c.Status);

        // Taking theirs on AddedByUs should remove the file
        await svc.ResolveWithSideAsync(ctx, "added.txt", ConflictSide.Theirs);

        Assert.False(File.Exists(Path.Combine(repoDir.Path, "added.txt")));
        Assert.Empty(await svc.GetConflictsAsync(ctx));
    }

    [Fact]
    public async Task Take_side_on_submodule_conflict_stages_that_commit()
    {
        await using var host = new MiniHost();

        // 1. Create a child repo that will be used as a submodule
        using var subRepo = TestRepositoryBuilder.Create();
        subRepo.WriteFile("sub.txt", "v1\n").Stage().Commit("sub v1");
        var subSha1 = subRepo.HeadSha;

        // 2. Main repo
        using var mainRepo = TestRepositoryBuilder.Create();
        mainRepo.Git("-c", "protocol.file.allow=always", "submodule", "add", subRepo.Path, "mysub");
        mainRepo.Commit("add submodule");

        // Create two new commits in subRepo
        subRepo.WriteFile("sub.txt", "v2\n").Stage().Commit("sub v2");
        var subSha2 = subRepo.HeadSha;
        subRepo.WriteFile("sub.txt", "v3\n").Stage().Commit("sub v3");
        var subSha3 = subRepo.HeadSha;

        // Feature branch points submodule to subSha2
        mainRepo.Checkout("feature", create: true);
        mainRepo.Git("update-index", "--cacheinfo", $"160000,{subSha2},mysub");
        mainRepo.Commit("submodule sha2 on feature");

        // Main branch points submodule to subSha3
        mainRepo.Checkout("main");
        mainRepo.Git("update-index", "--cacheinfo", $"160000,{subSha3},mysub");
        mainRepo.Commit("submodule sha3 on main");

        try { mainRepo.Git("merge", "feature"); } catch { }

        await using var ctx = host.Open(mainRepo.Path);
        var svc = Make(host);

        var conflicts = await svc.GetConflictsAsync(ctx);
        var c = Assert.Single(conflicts);
        Assert.True(c.IsSubmodule);

        // Take theirs (subSha2)
        await svc.ResolveWithSideAsync(ctx, "mysub", ConflictSide.Theirs);

        // Verify submodule folder still exists
        Assert.True(Directory.Exists(Path.Combine(mainRepo.Path, "mysub")));

        // Verify git ls-files -s shows stage 0 with subSha2
        var ls = mainRepo.Git("ls-files", "-s", "mysub").Trim();
        Assert.Contains(subSha2, ls);
        Assert.StartsWith("160000", ls);
        Assert.Empty(await svc.GetConflictsAsync(ctx));
    }

    [WindowsFact]
    public async Task External_tool_named_with_extension_preserves_bom_and_leaves_no_artifacts()
    {
        await using var host = new MiniHost();
        using var repoDir = TestRepositoryBuilder.Create();

        var preamble = new byte[] { 0xEF, 0xBB, 0xBF };
        byte[] Utf8Bom(string s) => preamble.Concat(Encoding.UTF8.GetBytes(s)).ToArray();

        Directory.CreateDirectory(Path.Combine(repoDir.Path, "sub"));
        File.WriteAllBytes(Path.Combine(repoDir.Path, "sub", "f.cs"), Utf8Bom("// base\n"));
        repoDir.Stage().Commit("base");

        repoDir.Checkout("feature", create: true);
        File.WriteAllBytes(Path.Combine(repoDir.Path, "sub", "f.cs"), Utf8Bom("// theirs\n"));
        repoDir.Stage().Commit("theirs");

        repoDir.Checkout("main");
        File.WriteAllBytes(Path.Combine(repoDir.Path, "sub", "f.cs"), Utf8Bom("// ours\n"));
        repoDir.Stage().Commit("ours");

        try { repoDir.Git("merge", "feature"); } catch { }

        await using var ctx = host.Open(repoDir.Path);

        var script = TestScratch.NewPath("historia-ext-tool-", ".ps1");
        var marker = TestScratch.NewPath("historia-ext-marker-", ".txt");
        var copiedLocal = TestScratch.NewPath("historia-ext-local-", ".cs");

        await File.WriteAllTextAsync(script,
            "param([string]$Local, [string]$Merged)\n" +
            "Set-Content -LiteralPath '" + marker.Replace("'", "''") + "' -Value $Local -Encoding utf8\n" +
            "Copy-Item -LiteralPath $Local -Destination '" + copiedLocal.Replace("'", "''") + "'\n" +
            "Copy-Item -LiteralPath $Local -Destination $Merged\n");

        try
        {
            await host.Settings.UpdateAsync(s => s with
            {
                ExternalMergeTool = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\" $LOCAL $MERGED",
            });

            var svc = Make(host);
            var ok = await svc.LaunchExternalMergeToolAsync(ctx, "sub/f.cs");

            Assert.True(ok);
            Assert.True(File.Exists(marker));
            var localPath = (await File.ReadAllTextAsync(marker)).Trim();
            Assert.EndsWith(".cs", localPath, StringComparison.OrdinalIgnoreCase);

            var copiedBytes = await File.ReadAllBytesAsync(copiedLocal);
            Assert.Equal(preamble, copiedBytes[..preamble.Length]);

            var status = repoDir.Git("status", "--porcelain", "--ignored");
            // No extra untracked/ignored files created in the working tree
            Assert.DoesNotContain(".merge_file", status);
        }
        finally
        {
            TestScratch.Delete(script);
            TestScratch.Delete(marker);
            TestScratch.Delete(copiedLocal);
        }
    }

    [Fact]
    public async Task Resolve_with_ours_then_continue_completes_the_merge()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        await svc.ResolveWithSideAsync(ctx, "f.txt", ConflictSide.Ours);
        Assert.Empty(await svc.GetConflictsAsync(ctx));

        await svc.ContinueOperationAsync(ctx);

        var log = repoDir.Git("log", "--oneline", "-1");
        Assert.Contains("Merge", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OURS", await File.ReadAllTextAsync(Path.Combine(repoDir.Path, "f.txt")));
    }

    [Fact]
    public async Task Abort_restores_the_pre_merge_state()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        await svc.AbortOperationAsync(ctx);

        Assert.Empty(await svc.GetConflictsAsync(ctx));
        Assert.DoesNotContain(".git/MERGE_HEAD",
            Directory.Exists(Path.Combine(repoDir.Path, ".git")) && File.Exists(Path.Combine(repoDir.Path, ".git", "MERGE_HEAD"))
                ? ".git/MERGE_HEAD" : "");
        Assert.Contains("OURS", await File.ReadAllTextAsync(Path.Combine(repoDir.Path, "f.txt")));
    }

    [Fact]
    public void Tokenize_keeps_a_quoted_path_with_spaces_as_one_token()
    {
        var tokens = ConflictService.Tokenize(
            "\"C:\\Program Files\\Beyond Compare 4\\BComp.exe\" $LOCAL $REMOTE $BASE $MERGED");

        Assert.Equal(
            [@"C:\Program Files\Beyond Compare 4\BComp.exe", "$LOCAL", "$REMOTE", "$BASE", "$MERGED"],
            tokens);
    }

    [Fact]
    public void Tokenize_handles_an_unquoted_tool_with_no_spaces_in_its_path()
        => Assert.Equal(["meld", "$LOCAL", "$REMOTE", "$MERGED"], ConflictService.Tokenize("meld $LOCAL $REMOTE $MERGED"));

    [Fact]
    public void Tokenize_collapses_repeated_whitespace_between_tokens()
        => Assert.Equal(["a", "b"], ConflictService.Tokenize("  a    b  "));

    [Fact]
    public void Tokenize_of_blank_input_yields_no_tokens()
        => Assert.Empty(ConflictService.Tokenize("   "));

    [WindowsFact]
    public async Task LaunchExternalMergeTool_passes_a_metacharacter_laden_path_as_one_literal_argument()
    {
        const string conflictedName = "pwn&calc&file.txt";

        await using var host = new MiniHost();
        using var repoDir = TestRepositoryBuilder.FromTemplate("conflicted-metachar-path-repo", repo =>
        {
            repo.WriteFile(conflictedName, "line1\nshared\nline3\n").Stage().Commit("base");
            repo.Checkout("feature", create: true).WriteFile(conflictedName, "line1\nTHEIRS\nline3\n").Stage().Commit("theirs");
            repo.Checkout("main").WriteFile(conflictedName, "line1\nOURS\nline3\n").Stage().Commit("ours");
            try { repo.Git("merge", "feature"); } catch { /* expected to conflict */ }
        });

        await using var ctx = host.Open(repoDir.Path);

        // A "merge tool" that records the argv it actually received, one line per argument.
        var script = TestScratch.NewPath("historia-mergetool-test-", ".ps1");
        var marker = TestScratch.NewPath("historia-mergetool-test-", ".txt");
        await File.WriteAllTextAsync(script,
            "param([string]$Merged)\n" +
            "Set-Content -LiteralPath '" + marker.Replace("'", "''") + "' -Value $Merged -Encoding utf8\n");

        try
        {
            await host.Settings.UpdateAsync(s => s with
            {
                ExternalMergeTool = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{script}\" $MERGED",
            });

            var svc = Make(host);
            await svc.LaunchExternalMergeToolAsync(ctx, conflictedName);

            Assert.True(File.Exists(marker), "the configured merge tool never ran");
            var received = (await File.ReadAllTextAsync(marker)).Trim();
            // The whole "&calc&"-laden path arrived as PowerShell's single -Merged argument
            // (i.e. it was never split or re-parsed by a shell along the way).
            Assert.EndsWith(conflictedName, received, StringComparison.Ordinal);
        }
        finally
        {
            TestScratch.Delete(script);
            TestScratch.Delete(marker);
        }
    }

    [WindowsFact]
    public async Task LaunchExternalMergeToolAsync_runs_the_configured_tool_directly_not_via_a_wrapping_shell()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        await host.Settings.UpdateAsync(s => s with { ExternalMergeTool = "cmd.exe /c copy /Y $LOCAL $MERGED" });

        var ok = await svc.LaunchExternalMergeToolAsync(ctx, "f.txt");

        Assert.True(ok);
        var merged = await File.ReadAllTextAsync(Path.Combine(repoDir.Path, "f.txt"));
        Assert.Contains("OURS", merged);
        Assert.DoesNotContain("<<<<<<<", merged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_with_side_sees_index_changed_by_cli_since_last_status()
    {
        await using var host = new MiniHost();
        using var repoDir = TestRepositoryBuilder.Create();
        repoDir.WriteFile("init.txt", "init\n").Stage().Commit("init");

        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        // Pre-query conflicts so the LibGit2 repo inside ctx caches its initial 0-conflict status.
        var initial = await svc.GetConflictsAsync(ctx);
        Assert.Empty(initial);

        // Now CLI updates the index directly, introducing a conflict stage without notifying LibGit2.
        repoDir.WriteFile("added.txt", "content\n");
        var sha = repoDir.Git("hash-object", "-w", "added.txt").Trim();
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoDir.Path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("update-index");
        psi.ArgumentList.Add("--index-info");
        using (var p = Process.Start(psi)!)
        {
            using (var sw = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                sw.Write($"100644 {sha} 2\tadded.txt\n");
            }
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }

        // ResolveWithSideAsync must reload status via RetrieveStatus() rather than reading stale cached conflicts.
        await svc.ResolveWithSideAsync(ctx, "added.txt", ConflictSide.Theirs);

        Assert.False(File.Exists(Path.Combine(repoDir.Path, "added.txt")));
        Assert.Empty(await svc.GetConflictsAsync(ctx));
    }

    private sealed class FailingCheckoutCli(IGitCliService inner) : IGitCliService
    {
        public bool IsGitAvailable => inner.IsGitAvailable;

        public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
        {
            if (request.Arguments.Contains("checkout-index"))
            {
                return Task.FromResult(new GitCliResult(
                    1,
                    string.Empty,
                    "fatal: unable to checkout file",
                    GitErrorKind.Unknown,
                    TimeSpan.Zero));
            }
            return inner.RunAsync(request, ct);
        }

        public Task<GitCliResult> RunAsync(
            GitCliRequest request,
            IProgress<GitProgress>? progress,
            Action<string>? onStdoutLine,
            CancellationToken ct = default)
            => inner.RunAsync(request, progress, onStdoutLine, ct);
    }

    [Fact]
    public async Task External_tool_stage_extract_failure_throws_classified_error()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);

        var realCli = (IGitCliService)host.Provider.GetService(typeof(IGitCliService))!;
        var failingCli = new FailingCheckoutCli(realCli);
        var svc = new ConflictService(failingCli, host.Settings);

        await host.Settings.UpdateAsync(s => s with { ExternalMergeTool = "dummy $LOCAL $REMOTE $BASE $MERGED" });

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() => svc.LaunchExternalMergeToolAsync(ctx, "f.txt"));
        Assert.Equal(GitErrorKind.Unknown, ex.Kind);
    }

    [Fact]
    public async Task Oversize_working_copy_hash_matches_file_hash()
    {
        await using var host = new MiniHost();
        using var repoDir = TestRepositoryBuilder.Create();
        repoDir.WriteFile("init.txt", "init\n").Stage().Commit("init");

        // Create an oversize file (> 5 MB: 5 * 1024 * 1024 + 1024 bytes)
        int size = (5 * 1024 * 1024) + 1024;
        var bytes = new byte[size];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }
        string filePath = Path.Combine(repoDir.Path, "large.txt");
        await File.WriteAllBytesAsync(filePath, bytes);

        var expectedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var copy = await svc.LoadWorkingCopyAsync(ctx, "large.txt");
        Assert.True(copy.ExceedsBudget);
        Assert.Equal(expectedHash, copy.DiskHash);
    }

    [Fact]
    public async Task SaveResolution_recreates_missing_file_when_expected_hash_is_empty()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var fullPath = Path.Combine(repoDir.Path, "f.txt");
        File.Delete(fullPath);

        var copy = await svc.LoadWorkingCopyAsync(ctx, "f.txt");
        Assert.Equal(string.Empty, copy.DiskHash);

        var res = await svc.SaveResolutionAsync(ctx, "f.txt", "recreated content\n", new TextFileFormat(Encoding.UTF8, false), expectedDiskHash: "", stage: false);
        Assert.True(res.Saved);
        Assert.True(File.Exists(fullPath));
        Assert.Equal("recreated content\n", await File.ReadAllTextAsync(fullPath));
    }

    [Fact]
    public async Task SaveResolution_refuses_when_file_appeared_after_load_as_missing()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var fullPath = Path.Combine(repoDir.Path, "f.txt");
        File.Delete(fullPath);

        var copy = await svc.LoadWorkingCopyAsync(ctx, "f.txt");
        Assert.Equal(string.Empty, copy.DiskHash);

        await File.WriteAllTextAsync(fullPath, "appeared externally\n");

        var res = await svc.SaveResolutionAsync(ctx, "f.txt", "resolution\n", new TextFileFormat(Encoding.UTF8, false), expectedDiskHash: "", stage: false);
        Assert.False(res.Saved);
        Assert.NotEqual(string.Empty, res.DiskHash);
    }

    [Fact]
    public async Task SaveResolution_refuses_directory_path()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var subDir = Path.Combine(repoDir.Path, "sub");
        Directory.CreateDirectory(subDir);

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() =>
            svc.SaveResolutionAsync(ctx, "sub", "content", new TextFileFormat(Encoding.UTF8, false), "", stage: false));
        Assert.Equal(GitErrorKind.Unknown, ex.Kind);
        Assert.Contains("is a directory (submodule) and cannot be saved as text.", ex.Message);
    }

    [Fact]
    public async Task GetDiskHash_matches_LoadWorkingCopy_DiskHash()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        // 1. Existing file
        var existingCopy = await svc.LoadWorkingCopyAsync(ctx, "f.txt");
        var existingHash = await svc.GetDiskHashAsync(ctx, "f.txt");
        Assert.False(string.IsNullOrEmpty(existingHash));
        Assert.Equal(existingCopy.DiskHash, existingHash);

        // 2. Missing file
        var missingCopy = await svc.LoadWorkingCopyAsync(ctx, "nonexistent.txt");
        var missingHash = await svc.GetDiskHashAsync(ctx, "nonexistent.txt");
        Assert.Equal(string.Empty, missingCopy.DiskHash);
        Assert.Equal(string.Empty, missingHash);

        // 3. Over-budget file (> 5MB)
        int size = (5 * 1024 * 1024) + 1024;
        var bytes = new byte[size];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)(i % 251);
        await File.WriteAllBytesAsync(Path.Combine(repoDir.Path, "large.txt"), bytes);

        var largeCopy = await svc.LoadWorkingCopyAsync(ctx, "large.txt");
        var largeHash = await svc.GetDiskHashAsync(ctx, "large.txt");
        Assert.True(largeCopy.ExceedsBudget);
        Assert.Equal(largeCopy.DiskHash, largeHash);
    }

    [Fact]
    public async Task Continue_Skip_Abort_refresh_state_themselves()
    {
        await using var host = new MiniHost();
        var svc = Make(host);

        // 1. Rebase fixture for Skip and Continue
        {
            using var repoDir = TestRepositoryBuilder.Create();
            repoDir.WriteFile("f1.txt", "base1\n").WriteFile("f2.txt", "base2\n").Stage().Commit("base");

            repoDir.Checkout("feature", create: true);
            repoDir.WriteFile("f1.txt", "feature1\n").Stage().Commit("conflict1");
            repoDir.WriteFile("f2.txt", "feature2\n").Stage().Commit("conflict2");

            repoDir.Checkout("main");
            repoDir.WriteFile("f1.txt", "main1\n").WriteFile("f2.txt", "main2\n").Stage().Commit("main changes");

            repoDir.Checkout("feature");
            try { repoDir.Git("rebase", "main"); } catch { }

            await using var ctx = host.Open(repoDir.Path);
            ctx.MarkFirstGraphPaintDone();
            ctx.MarkFirstFullLoadDone();
            await ctx.State.RefreshAllAsync(ctx.Lifetime);

            Assert.Equal(OperationState.Rebasing, ctx.State.PendingOperation);
            Assert.Contains(ctx.State.Status.Conflicted, c => c.Path == "f1.txt");

            // Skip current commit (conflict1). Must refresh state automatically.
            await svc.SkipCurrentAsync(ctx);

            Assert.Equal(OperationState.Rebasing, ctx.State.PendingOperation);
            Assert.Contains(ctx.State.Status.Conflicted, c => c.Path == "f2.txt");

            // Resolve f2.txt and Continue. Must refresh state automatically.
            var copy = await svc.LoadWorkingCopyAsync(ctx, "f2.txt");
            await svc.SaveResolutionAsync(ctx, "f2.txt", "resolved f2\n", copy.Format ?? default, copy.DiskHash, stage: true);

            await svc.ContinueOperationAsync(ctx);

            Assert.Equal(OperationState.None, ctx.State.PendingOperation);
            Assert.Empty(ctx.State.Status.Conflicted);
        }

        // 2. Merge fixture for Abort
        {
            using var repoDir = ConflictedRepo();
            await using var ctx = host.Open(repoDir.Path);
            ctx.MarkFirstGraphPaintDone();
            ctx.MarkFirstFullLoadDone();
            await ctx.State.RefreshAllAsync(ctx.Lifetime);

            Assert.Equal(OperationState.Merging, ctx.State.PendingOperation);
            Assert.NotEmpty(ctx.State.Status.Conflicted);

            // Abort. Must refresh state automatically.
            await svc.AbortOperationAsync(ctx);

            Assert.Equal(OperationState.None, ctx.State.PendingOperation);
            Assert.Empty(ctx.State.Status.Conflicted);
        }
    }

    [Fact]
    public async Task Abort_failure_with_conflicts_present_throws()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        await ctx.State.RefreshAllAsync(ctx.Lifetime);
        var svc = Make(host);

        var lockPath = Path.Combine(ctx.GitDirectory, "index.lock");
        await File.WriteAllBytesAsync(lockPath, []);
        try
        {
            await Assert.ThrowsAsync<HistoriaGitException>(() => svc.AbortOperationAsync(ctx));
            Assert.True(File.Exists(Path.Combine(ctx.GitDirectory, "MERGE_HEAD")));
            var conflicts = await svc.GetConflictsAsync(ctx);
            Assert.NotEmpty(conflicts);
        }
        finally
        {
            if (File.Exists(lockPath))
                File.Delete(lockPath);
        }
    }

    [Fact]
    public async Task Skip_failure_with_conflicts_present_throws()
    {
        await using var host = new MiniHost();
        using var repoDir = TestRepositoryBuilder.Create();
        repoDir.WriteFile("f1.txt", "base1\n").Stage().Commit("base");
        repoDir.Checkout("feature", create: true).WriteFile("f1.txt", "feature1\n").Stage().Commit("c1");
        repoDir.Checkout("main").WriteFile("f1.txt", "main1\n").Stage().Commit("m1");
        repoDir.Checkout("feature");
        try { repoDir.Git("rebase", "main"); } catch { }

        await using var ctx = host.Open(repoDir.Path);
        ctx.MarkFirstGraphPaintDone();
        ctx.MarkFirstFullLoadDone();
        await ctx.State.RefreshAllAsync(ctx.Lifetime);
        var svc = Make(host);

        Assert.Equal(OperationState.Rebasing, ctx.State.PendingOperation);

        var lockPath = Path.Combine(ctx.GitDirectory, "index.lock");
        await File.WriteAllBytesAsync(lockPath, []);
        try
        {
            await Assert.ThrowsAsync<HistoriaGitException>(() => svc.SkipCurrentAsync(ctx));
            Assert.Equal(OperationState.Rebasing, Historia.Services.State.RepositoryStateService.DetectOperationState(ctx.GitDirectory));
        }
        finally
        {
            if (File.Exists(lockPath))
                File.Delete(lockPath);
        }
    }

    [Fact]
    public async Task SaveResolution_stage_failure_returns_new_hash_and_stage_error()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var copy = await svc.LoadWorkingCopyAsync(ctx, "f.txt");
        var resolvedText = "line1\nRESOLVED\nline3\n";
        var lockPath = Path.Combine(ctx.GitDirectory, "index.lock");
        await File.WriteAllBytesAsync(lockPath, []);
        try
        {
            var res = await svc.SaveResolutionAsync(ctx, "f.txt", resolvedText, copy.Format!.Value, copy.DiskHash, stage: true);
            Assert.True(res.Saved);
            Assert.NotNull(res.StageError);

            var expectedHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(copy.Format.Value.Encode(resolvedText)));
            Assert.Equal(expectedHash, res.DiskHash);

            var diskHash = await svc.GetDiskHashAsync(ctx, "f.txt");
            Assert.Equal(expectedHash, diskHash);

            var conflicts = await svc.GetConflictsAsync(ctx);
            Assert.Contains(conflicts, c => c.FilePath == "f.txt");
        }
        finally
        {
            if (File.Exists(lockPath))
                File.Delete(lockPath);
        }
    }

    [Fact]
    public async Task SaveResolution_refuses_default_format_without_writing()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var fullPath = Path.Combine(ctx.RepositoryPath, "f.txt");
        var originalBytes = await File.ReadAllBytesAsync(fullPath);
        var originalMtime = File.GetLastWriteTimeUtc(fullPath);
        var diskHash = await svc.GetDiskHashAsync(ctx, "f.txt");

        var ex = await Assert.ThrowsAsync<HistoriaGitException>(() =>
            svc.SaveResolutionAsync(ctx, "f.txt", "new text", default, diskHash, stage: false));

        Assert.Contains("no detected text format", ex.Message);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(fullPath));
        Assert.Equal(originalMtime, File.GetLastWriteTimeUtc(fullPath));
    }
}

