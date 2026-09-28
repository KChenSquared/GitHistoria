namespace Historia.Tests;

[Trait("tier", "git")]
public sealed class UnmergedIndexReaderTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    private UnmergedIndexReader MakeReader()
        => new((IGitCliService)_host.Provider.GetService(typeof(IGitCliService))!);

    private ConflictService MakeService()
        => new((IGitCliService)_host.Provider.GetService(typeof(IGitCliService))!, _host.Settings);

    [Fact]
    public async Task Both_modified_conflict_detected()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("both-mod-test", b =>
        {
            b.WriteFile("f.txt", "line1\nbase\nline3\n").Stage().Commit("base");
            b.Checkout("feature", create: true).WriteFile("f.txt", "line1\ntheirs\nline3\n").Stage().Commit("theirs");
            b.Checkout("main").WriteFile("f.txt", "line1\nours\nline3\n").Stage().Commit("ours");
            try { b.Git("merge", "feature"); } catch { }
        });

        var reader = MakeReader();
        var conflicts = await reader.ReadAsync(repo.Path);

        var c = Assert.Single(conflicts);
        Assert.Equal("f.txt", c.Path);
        Assert.NotNull(c.Ancestor);
        Assert.NotNull(c.Ours);
        Assert.NotNull(c.Theirs);
        Assert.Equal(1, c.Ancestor.Stage);
        Assert.Equal(2, c.Ours.Stage);
        Assert.Equal(3, c.Theirs.Stage);
        Assert.Equal("100644", c.Ancestor.Mode);
        Assert.Equal("100644", c.Ours.Mode);
        Assert.Equal("100644", c.Theirs.Mode);

        await using var ctx = _host.Open(repo.Path);
        var svc = MakeService();
        var files = await svc.GetConflictsAsync(ctx);
        var file = Assert.Single(files);
        Assert.Equal(ConflictStatus.BothModified, file.Status);
        Assert.False(file.IsBinary);
        Assert.False(file.IsSubmodule);
    }

    [Fact]
    public async Task Add_add_conflict_detected()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("add-add-test", b =>
        {
            b.WriteFile("init.txt", "init\n").Stage().Commit("init");
            b.Checkout("feature", create: true).WriteFile("added.txt", "theirs\n").Stage().Commit("theirs");
            b.Checkout("main").WriteFile("added.txt", "ours\n").Stage().Commit("ours");
            try { b.Git("merge", "feature"); } catch { }
        });

        var reader = MakeReader();
        var conflicts = await reader.ReadAsync(repo.Path);

        var c = Assert.Single(conflicts);
        Assert.Equal("added.txt", c.Path);
        Assert.Null(c.Ancestor);
        Assert.NotNull(c.Ours);
        Assert.NotNull(c.Theirs);
        Assert.Equal(2, c.Ours.Stage);
        Assert.Equal(3, c.Theirs.Stage);

        await using var ctx = _host.Open(repo.Path);
        var svc = MakeService();
        var files = await svc.GetConflictsAsync(ctx);
        var file = Assert.Single(files);
        Assert.Equal(ConflictStatus.BothAdded, file.Status);
        Assert.False(file.IsBinary);
        Assert.False(file.IsSubmodule);
    }

    [Fact]
    public async Task Modify_delete_conflict_detected()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("mod-del-test", b =>
        {
            b.WriteFile("f.txt", "base\n").Stage().Commit("base");
            b.Checkout("feature", create: true);
            b.Git("rm", "f.txt");
            b.Commit("delete f");
            b.Checkout("main").WriteFile("f.txt", "modified\n").Stage().Commit("modify f");
            try { b.Git("merge", "feature"); } catch { }
        });

        var reader = MakeReader();
        var conflicts = await reader.ReadAsync(repo.Path);

        var c = Assert.Single(conflicts);
        Assert.Equal("f.txt", c.Path);
        Assert.NotNull(c.Ancestor);
        Assert.NotNull(c.Ours);
        Assert.Null(c.Theirs);
        Assert.Equal(1, c.Ancestor.Stage);
        Assert.Equal(2, c.Ours.Stage);

        await using var ctx = _host.Open(repo.Path);
        var svc = MakeService();
        var files = await svc.GetConflictsAsync(ctx);
        var file = Assert.Single(files);
        Assert.Equal(ConflictStatus.DeletedByThem, file.Status);
        Assert.False(file.IsBinary);
    }

    [Fact]
    public async Task Submodule_conflict_detected()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("init.txt", "init\n").Stage().Commit("init");

        var dummySha1 = "1111111111111111111111111111111111111111";
        var dummySha2 = "2222222222222222222222222222222222222222";
        var dummySha3 = "3333333333333333333333333333333333333333";

        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo.Path,
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
                sw.Write($"160000 {dummySha1} 1\tsubmod\n");
                sw.Write($"160000 {dummySha2} 2\tsubmod\n");
                sw.Write($"160000 {dummySha3} 3\tsubmod\n");
            }
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }

        var reader = MakeReader();
        var conflicts = await reader.ReadAsync(repo.Path);

        var c = Assert.Single(conflicts);
        Assert.Equal("submod", c.Path);
        Assert.Equal("160000", c.Ancestor?.Mode);
        Assert.Equal("160000", c.Ours?.Mode);
        Assert.Equal("160000", c.Theirs?.Mode);

        await using var ctx = _host.Open(repo.Path);
        var svc = MakeService();
        var files = await svc.GetConflictsAsync(ctx);
        var file = Assert.Single(files);
        Assert.Equal(ConflictStatus.BothModified, file.Status);
        Assert.True(file.IsSubmodule);
        Assert.False(file.IsBinary);
    }

    [Fact]
    public async Task Binary_side_conflict_detected()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("binary-conflict-test", b =>
        {
            var baseBytes = new byte[] { 1, 2, 3, 0, 4, 5 };
            var oursBytes = new byte[] { 1, 2, 3, 0, 99, 100 };
            var theirsBytes = new byte[] { 1, 2, 3, 0, 200, 201 };

            File.WriteAllBytes(Path.Combine(b.Path, "bin.dat"), baseBytes);
            b.Stage("bin.dat").Commit("base");

            b.Checkout("feature", create: true);
            File.WriteAllBytes(Path.Combine(b.Path, "bin.dat"), theirsBytes);
            b.Stage("bin.dat").Commit("theirs");

            b.Checkout("main");
            File.WriteAllBytes(Path.Combine(b.Path, "bin.dat"), oursBytes);
            b.Stage("bin.dat").Commit("ours");

            try { b.Git("merge", "feature"); } catch { }
        });

        var reader = MakeReader();
        var conflicts = await reader.ReadAsync(repo.Path);
        var c = Assert.Single(conflicts);

        var isBinaryOurs = await reader.IsBinaryAsync(repo.Path, c.Ours);
        var isBinaryTheirs = await reader.IsBinaryAsync(repo.Path, c.Theirs);
        var isBinaryAncestor = await reader.IsBinaryAsync(repo.Path, c.Ancestor);
        Assert.True(isBinaryOurs);
        Assert.True(isBinaryTheirs);
        Assert.True(isBinaryAncestor);

        await using var ctx = _host.Open(repo.Path);
        var svc = MakeService();
        var files = await svc.GetConflictsAsync(ctx);
        var file = Assert.Single(files);
        Assert.True(file.IsBinary);
        Assert.False(file.IsSubmodule);
    }

    [Fact]
    public async Task Path_with_newline_handled_without_truncation()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("init.txt", "init\n").Stage().Commit("init");

        repo.WriteFile("temp.txt", "content\n");
        var sha = repo.Git("hash-object", "-w", "temp.txt").Trim();
        File.Delete(Path.Combine(repo.Path, "temp.txt"));

        const string pathWithNewline = "sub/file\nwith\nnewlines.txt";

        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo.Path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.protectNTFS=false");
        psi.ArgumentList.Add("update-index");
        psi.ArgumentList.Add("-z");
        psi.ArgumentList.Add("--index-info");
        using (var p = Process.Start(psi)!)
        {
            using (var sw = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                sw.Write($"100644 {sha} 1\t{pathWithNewline}\0");
                sw.Write($"100644 {sha} 2\t{pathWithNewline}\0");
                sw.Write($"100644 {sha} 3\t{pathWithNewline}\0");
            }
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }

        var reader = MakeReader();
        var conflicts = await reader.ReadAsync(repo.Path);

        var c = Assert.Single(conflicts);
        Assert.Equal(pathWithNewline, c.Path);
        Assert.NotNull(c.Ancestor);
        Assert.NotNull(c.Ours);
        Assert.NotNull(c.Theirs);
    }

    [Fact]
    public async Task HasUnmergedPathsAsync_truthful()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("has-unmerged-test", b =>
        {
            b.WriteFile("f.txt", "base\n").Stage().Commit("base");
            b.Checkout("feature", create: true).WriteFile("f.txt", "theirs\n").Stage().Commit("theirs");
            b.Checkout("main").WriteFile("f.txt", "ours\n").Stage().Commit("ours");
            try { b.Git("merge", "feature"); } catch { }
        });

        var reader = MakeReader();
        Assert.True(await reader.HasUnmergedPathsAsync(repo.Path));

        repo.Git("checkout", "--ours", "--", "f.txt");
        repo.Git("add", "--", "f.txt");

        Assert.False(await reader.HasUnmergedPathsAsync(repo.Path));
    }

    [Fact]
    public void Parse_returns_empty_on_blank_stdout()
    {
        Assert.Empty(UnmergedIndexReader.Parse(string.Empty));
        Assert.Empty(UnmergedIndexReader.Parse(null!));
    }

    [Fact]
    public void Parse_correctly_groups_multiple_paths_and_stages()
    {
        var id1 = "1111111111111111111111111111111111111111";
        var id2 = "2222222222222222222222222222222222222222";
        var id3 = "3333333333333333333333333333333333333333";
        var id4 = "4444444444444444444444444444444444444444";

        var stdout = $"100644 {id1} 1\tfile1.txt\0"
                   + $"100644 {id2} 2\tfile1.txt\0"
                   + $"100644 {id3} 3\tfile1.txt\0"
                   + $"100755 {id4} 2\tfile2.sh\0";

        var list = UnmergedIndexReader.Parse(stdout);
        Assert.Equal(2, list.Count);

        var f1 = list.Single(c => c.Path == "file1.txt");
        Assert.NotNull(f1.Ancestor);
        Assert.NotNull(f1.Ours);
        Assert.NotNull(f1.Theirs);
        Assert.Equal("100644", f1.Ancestor.Mode);
        Assert.Equal(id1, f1.Ancestor.Id.ToString());
        Assert.Equal(id2, f1.Ours.Id.ToString());
        Assert.Equal(id3, f1.Theirs.Id.ToString());

        var f2 = list.Single(c => c.Path == "file2.sh");
        Assert.Null(f2.Ancestor);
        Assert.NotNull(f2.Ours);
        Assert.Null(f2.Theirs);
        Assert.Equal("100755", f2.Ours.Mode);
        Assert.Equal(id4, f2.Ours.Id.ToString());
    }

    [Fact]
    public void IsBinary_heuristics_unit_tests()
    {
        // 1. Plain ASCII text
        Assert.False(UnmergedIndexReader.IsBinary(Encoding.UTF8.GetBytes("Hello, world!\nLine 2\n")));

        // 2. UTF-8 with BOM
        var utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("Text with BOM\n")).ToArray();
        Assert.False(UnmergedIndexReader.IsBinary(utf8Bom));

        // 3. UTF-16 LE BOM
        var utf16Le = new byte[] { 0xFF, 0xFE }
            .Concat(Encoding.Unicode.GetBytes("Text\n")).ToArray();
        Assert.True(UnmergedIndexReader.IsBinary(utf16Le));

        // 4. UTF-16 BE BOM
        var utf16Be = new byte[] { 0xFE, 0xFF }
            .Concat(Encoding.BigEndianUnicode.GetBytes("Text\n")).ToArray();
        Assert.True(UnmergedIndexReader.IsBinary(utf16Be));

        // 5. UTF-32 LE BOM
        var utf32Le = new byte[] { 0xFF, 0xFE, 0x00, 0x00 }
            .Concat(Encoding.UTF32.GetBytes("Text\n")).ToArray();
        Assert.True(UnmergedIndexReader.IsBinary(utf32Le));

        // 6. UTF-32 BE BOM
        var utf32Be = new byte[] { 0x00, 0x00, 0xFE, 0xFF, 0, 0, 0, 65 };
        Assert.True(UnmergedIndexReader.IsBinary(utf32Be));

        // 7. Text containing NUL byte
        Assert.True(UnmergedIndexReader.IsBinary(new byte[] { (byte)'a', (byte)'b', 0, (byte)'c' }));

        // 8. Text with lots of non-printable bytes (ratio > 1/128)
        var nonPrintable = new byte[200];
        for (var i = 0; i < 100; i++)
            nonPrintable[i] = (byte)'a';
        for (var i = 100; i < 200; i++)
            nonPrintable[i] = 1; // control char (SOH), nonprintable
        Assert.True(UnmergedIndexReader.IsBinary(nonPrintable));

        // 9. Empty bytes
        Assert.False(UnmergedIndexReader.IsBinary([]));
    }

    [Fact]
    public void IsBinary_matches_LibGit2Sharp_Blob_IsBinary_oracle()
    {
        using var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("init.txt", "init\n").Stage().Commit("init");

        var cases = new (string Name, byte[] Content)[]
        {
            ("plain_ascii", Encoding.UTF8.GetBytes("Simple text\nline 2\n")),
            ("utf8_bom", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("BOM text\n")).ToArray()),
            ("utf16_le", new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("Unicode text\n")).ToArray()),
            ("nul_byte", new byte[] { (byte)'h', (byte)'i', 0, (byte)'!' }),
            ("control_chars", new byte[] { (byte)'a', 1, 2, 3, 4, 5, (byte)'b' }),
        };

        using var libgitRepo = new LibGit2Sharp.Repository(repo.Path);

        foreach (var (name, content) in cases)
        {
            var filePath = Path.Combine(repo.Path, $"{name}.dat");
            File.WriteAllBytes(filePath, content);
            var sha = repo.Git("hash-object", "-w", filePath).Trim();

            var blob = libgitRepo.Lookup<LibGit2Sharp.Blob>(sha);
            Assert.NotNull(blob);
            var oracleIsBinary = blob.IsBinary;

            var readerIsBinary = UnmergedIndexReader.IsBinary(content);
            Assert.True(oracleIsBinary == readerIsBinary,
                $"Case '{name}' mismatch: Oracle={oracleIsBinary}, Reader={readerIsBinary}");
        }
    }
}
