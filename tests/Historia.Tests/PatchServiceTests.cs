namespace Historia.Tests;
[Trait("tier", "git")]

public class PatchServiceTests
{
    private static PatchService Make(MiniHost host)
        => new((IGitCliService)host.Provider.GetService(typeof(IGitCliService))!);

    private static FileDiff UnstagedDiff(string repoPath, string file)
    {
        using var repo = new Repository(repoPath);
        return new LibGit2Service().ProjectDiff(repo, new DiffRequest { Source = DiffSource.UnstagedChanges, Path = file });
    }

    [Fact]
    public async Task Staging_one_hunk_stages_exactly_that_hunk()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("patch-12-lines-seed", r =>
        {
            // 12 numbered lines; change line 2 (hunk A) and line 11 (hunk B).
            r.WriteFile("f.txt", string.Join("\n", Enumerable.Range(1, 12).Select(i => $"line {i}")) + "\n").Stage().Commit("base");
            var lines = Enumerable.Range(1, 12).Select(i => $"line {i}").ToArray();
            lines[1] = "line 2 CHANGED";
            lines[10] = "line 11 CHANGED";
            r.WriteFile("f.txt", string.Join("\n", lines) + "\n");
        });
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        var diff = UnstagedDiff(b.Path, "f.txt");
        Assert.Equal(2, diff.Hunks.Count);

        var patch = svc.GeneratePatch(diff, PatchScope.SingleHunk, new HashSet<DiffLineRef> { new(0, 0) });
        await svc.ApplyPatchAsync(ctx, patch, PatchTarget.StagingArea, reverse: false);

        var staged = b.Git("diff", "--cached");
        Assert.Contains("line 2 CHANGED", staged);
        Assert.DoesNotContain("line 11 CHANGED", staged);

        var unstaged = b.Git("diff");
        Assert.Contains("line 11 CHANGED", unstaged);
    }

    [Fact]
    public async Task Staging_a_single_added_line_produces_a_correct_index_state()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("patch-added-lines-seed", r =>
        {
            r.WriteFile("f.txt", "a\nb\nc\n").Stage().Commit("base");
            r.WriteFile("f.txt", "a\nADD1\nb\nADD2\nc\n"); // two separate additions in one hunk
        });
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        var diff = UnstagedDiff(b.Path, "f.txt");
        var hunk = Assert.Single(diff.Hunks);
        // Find the body index of the "+ADD1" line.
        var add1 = Enumerable.Range(0, hunk.Lines.Count)
            .First(i => hunk.Lines[i].Type == DiffLineType.Addition && hunk.Lines[i].Content == "ADD1");

        var patch = svc.GeneratePatch(diff, PatchScope.SelectedLines, new HashSet<DiffLineRef> { new(0, add1) });
        await svc.ApplyPatchAsync(ctx, patch, PatchTarget.StagingArea, reverse: false);

        var staged = b.Git("diff", "--cached");
        Assert.Contains("+ADD1", staged);
        Assert.DoesNotContain("+ADD2", staged);
    }

    [Fact]
    public async Task Discard_hunk_reverts_only_that_region_of_the_working_tree()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("patch-discard-hunk-seed", r =>
        {
            r.WriteFile("f.txt", string.Join("\n", Enumerable.Range(1, 12).Select(i => $"L{i}")) + "\n").Stage().Commit("base");
            var lines = Enumerable.Range(1, 12).Select(i => $"L{i}").ToArray();
            lines[1] = "L2 X";
            lines[10] = "L11 Y";
            r.WriteFile("f.txt", string.Join("\n", lines) + "\n");
        });
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        var diff = UnstagedDiff(b.Path, "f.txt");
        var patch = svc.GeneratePatch(diff, PatchScope.SingleHunk, new HashSet<DiffLineRef> { new(0, 0) });
        await svc.ApplyPatchAsync(ctx, patch, PatchTarget.WorkingTree, reverse: true);

        var content = (await File.ReadAllTextAsync(Path.Combine(b.Path, "f.txt"))).Replace("\r\n", "\n");
        Assert.Contains("\nL2\n", content);       // first hunk reverted (no "L2 X")
        Assert.DoesNotContain("L2 X", content);
        Assert.Contains("L11 Y", content);        // second hunk untouched
    }

    [Fact]
    public void Binary_files_are_blocked_from_partial_staging()
    {
        var svc = new PatchService(new NoGitCli());
        var binary = FileDiff.EmptyFor("logo.png", DiffKind.Modified) with { IsBinary = true };
        Assert.Throws<HistoriaGitException>(() => svc.GeneratePatch(binary, PatchScope.SingleHunk));
    }

    [Fact]
    public async Task Partial_staging_is_correct_for_a_file_with_no_trailing_newline()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("patch-no-trailing-nl-seed", r =>
        {
            r.WriteFile("f.txt", "one\ntwo\nthree").Stage().Commit("base"); // no trailing newline
            r.WriteFile("f.txt", "one\ntwoX\nthree");
        });
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        var diff = UnstagedDiff(b.Path, "f.txt");
        var patch = svc.GeneratePatch(diff, PatchScope.SingleHunk, new HashSet<DiffLineRef> { new(0, 0) });
        var check = await svc.CheckAsync(ctx, patch, PatchTarget.StagingArea, reverse: false);
        Assert.True(check.CanApply, check.FailureReason);

        await svc.ApplyPatchAsync(ctx, patch, PatchTarget.StagingArea, reverse: false);
        Assert.Contains("twoX", b.Git("diff", "--cached"));
    }

    private static FileDiff StagedDiff(string repoPath, string file)
    {
        using var repo = new Repository(repoPath);
        return new LibGit2Service().ProjectDiff(repo, new DiffRequest { Source = DiffSource.StagedChanges, Path = file });
    }

    [Fact]
    public async Task Stage_added_line_then_unstage_with_reverse_restores_index()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("patch-unstage-added-seed", r =>
        {
            r.WriteFile("f.txt", "a\nb\nc\n").Stage().Commit("base");
            r.WriteFile("f.txt", "a\nADD1\nb\nADD2\nc\n");
        });
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        // Stage ADD1
        var unstaged = UnstagedDiff(b.Path, "f.txt");
        var hunk = Assert.Single(unstaged.Hunks);
        var add1 = Enumerable.Range(0, hunk.Lines.Count)
            .First(i => hunk.Lines[i].Type == DiffLineType.Addition && hunk.Lines[i].Content == "ADD1");

        var stagePatch = svc.GeneratePatch(unstaged, PatchScope.SelectedLines, new HashSet<DiffLineRef> { new(0, add1) }, reverse: false);
        await svc.ApplyPatchAsync(ctx, stagePatch, PatchTarget.StagingArea, reverse: false);

        Assert.Contains("+ADD1", b.Git("diff", "--cached"));

        // Now unstage ADD1 using StagedDiff and reverse: true
        var staged = StagedDiff(b.Path, "f.txt");
        var stagedHunk = Assert.Single(staged.Hunks);
        var stagedAdd1 = Enumerable.Range(0, stagedHunk.Lines.Count)
            .First(i => stagedHunk.Lines[i].Type == DiffLineType.Addition && stagedHunk.Lines[i].Content == "ADD1");

        var unstagePatch = svc.GeneratePatch(staged, PatchScope.SelectedLines, new HashSet<DiffLineRef> { new(0, stagedAdd1) }, reverse: true);
        await svc.ApplyPatchAsync(ctx, unstagePatch, PatchTarget.StagingArea, reverse: true);

        // Index equals HEAD -> git diff --cached is empty
        Assert.Empty(b.Git("diff", "--cached").Trim());
    }

    [Fact]
    public async Task Unstage_only_deletion_when_both_addition_and_deletion_staged()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("patch-unstage-del-seed", r =>
        {
            r.WriteFile("f.txt", "a\nDEL\nb\n").Stage().Commit("base");
            r.WriteFile("f.txt", "a\nADD\nb\n");
        });
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        // Stage both addition and deletion
        b.Git("add", "f.txt");
        var staged = StagedDiff(b.Path, "f.txt");
        var hunk = Assert.Single(staged.Hunks);
        var delIdx = Enumerable.Range(0, hunk.Lines.Count)
            .First(i => hunk.Lines[i].Type == DiffLineType.Deletion && hunk.Lines[i].Content == "DEL");

        // Unstage only the deletion
        var unstagePatch = svc.GeneratePatch(staged, PatchScope.SelectedLines, new HashSet<DiffLineRef> { new(0, delIdx) }, reverse: true);
        await svc.ApplyPatchAsync(ctx, unstagePatch, PatchTarget.StagingArea, reverse: true);

        var cached = b.Git("diff", "--cached");
        Assert.Contains("+ADD", cached);
        Assert.DoesNotContain("-DEL", cached);
    }

    [Fact]
    public void Selection_containing_only_context_returns_empty_patch()
    {
        var svc = new PatchService(new NoGitCli());
        var diff = FileDiff.EmptyFor("f.txt", DiffKind.Modified) with
        {
            RawPatch = "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1,3 +1,3 @@\n ctx1\n-del\n+add\n ctx2\n",
        };
        var patch = svc.GeneratePatch(diff, PatchScope.SelectedLines, new HashSet<DiffLineRef> { new(0, 0) });
        Assert.Equal(string.Empty, patch);
    }

    [Fact]
    public async Task CRLF_file_with_binary_attribute_preserves_crlf_through_hunk_and_selected_lines_staging()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("patch-crlf-binary-seed", r =>
        {
            r.WriteFile(".gitattributes", "* -text\n").Stage();
            r.WriteFile("f.txt", "line1\r\nline2\r\nline3\r\n").Stage().Commit("base");
            r.WriteFile("f.txt", "line1\r\nline2_MOD\r\nline3\r\nline4_ADD\r\n");
        });
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        // 1. Stage a hunk
        var diff = UnstagedDiff(b.Path, "f.txt");
        var patchHunk = svc.GeneratePatch(diff, PatchScope.SingleHunk, new HashSet<DiffLineRef> { new(0, 0) });
        await svc.ApplyPatchAsync(ctx, patchHunk, PatchTarget.StagingArea, reverse: false);

        var indexContent = b.Git("show", ":f.txt");
        Assert.Contains("\r\n", indexContent);
        Assert.Equal("line1\r\nline2_MOD\r\nline3\r\nline4_ADD\r\n", indexContent);

        // Unstage all back to HEAD
        b.Git("reset", "HEAD", "f.txt");

        // 2. Stage one selected line (line4_ADD)
        var diff2 = UnstagedDiff(b.Path, "f.txt");
        var hunk = Assert.Single(diff2.Hunks);
        var addIdx = Enumerable.Range(0, hunk.Lines.Count)
            .First(i => hunk.Lines[i].Type == DiffLineType.Addition && hunk.Lines[i].Content == "line4_ADD");

        var patchLine = svc.GeneratePatch(diff2, PatchScope.SelectedLines, new HashSet<DiffLineRef> { new(0, addIdx) }, reverse: false);
        await svc.ApplyPatchAsync(ctx, patchLine, PatchTarget.StagingArea, reverse: false);

        var indexContent2 = b.Git("show", ":f.txt");
        Assert.Equal("line1\r\nline2\r\nline3\r\nline4_ADD\r\n", indexContent2);

        // 3. Unstage it again with reverse: true
        var stagedDiff = StagedDiff(b.Path, "f.txt");
        var stagedHunk = Assert.Single(stagedDiff.Hunks);
        var stagedAddIdx = Enumerable.Range(0, stagedHunk.Lines.Count)
            .First(i => stagedHunk.Lines[i].Type == DiffLineType.Addition && stagedHunk.Lines[i].Content == "line4_ADD");

        var unstagePatch = svc.GeneratePatch(stagedDiff, PatchScope.SelectedLines, new HashSet<DiffLineRef> { new(0, stagedAddIdx) }, reverse: true);
        await svc.ApplyPatchAsync(ctx, unstagePatch, PatchTarget.StagingArea, reverse: true);

        // Index equals HEAD, and git show :f.txt matches original
        var finalContent = b.Git("show", ":f.txt");
        Assert.Equal("line1\r\nline2\r\nline3\r\n", finalContent);
    }

    [Fact]
    public async Task Line_joining_selection_forward_splits_eof_newline_throws_and_preserves_index()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("patch-b16-forward-seed", r =>
        {
            r.WriteFile("f.txt", "x\na").Stage().Commit("base"); // no trailing newline
            r.WriteFile("f.txt", "x\na\nb");                     // no trailing newline
        });
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        var diff = UnstagedDiff(b.Path, "f.txt");
        var hunk = Assert.Single(diff.Hunks);
        // Find indices of -a, +a, +b
        var minusA = Enumerable.Range(0, hunk.Lines.Count)
            .First(i => hunk.Lines[i].Type == DiffLineType.Deletion && hunk.Lines[i].Content == "a");
        var plusA = Enumerable.Range(0, hunk.Lines.Count)
            .First(i => hunk.Lines[i].Type == DiffLineType.Addition && hunk.Lines[i].Content == "a");
        var plusB = Enumerable.Range(0, hunk.Lines.Count)
            .First(i => hunk.Lines[i].Type == DiffLineType.Addition && hunk.Lines[i].Content == "b");

        // Selecting only +b must throw the B16 error
        var ex = Assert.Throws<HistoriaGitException>(() =>
            svc.GeneratePatch(diff, PatchScope.SelectedLines, new HashSet<DiffLineRef> { new(0, plusB) }, reverse: false));
        Assert.Contains("This selection splits an end-of-file newline change. Include the file's last line, or stage the whole hunk.", ex.Message);

        // Index is completely untouched
        Assert.Empty(b.Git("diff", "--cached").Trim());

        // Selecting -a, +a and +b stages cleanly
        var validPatch = svc.GeneratePatch(diff, PatchScope.SelectedLines, new HashSet<DiffLineRef> { new(0, minusA), new(0, plusA), new(0, plusB) }, reverse: false);
        await svc.ApplyPatchAsync(ctx, validPatch, PatchTarget.StagingArea, reverse: false);

        var stagedContent = b.Git("show", ":f.txt");
        Assert.Equal("x\na\nb", stagedContent);
    }

    [Fact]
    public void Line_joining_selection_reverse_splits_eof_newline_throws()
    {
        var svc = new PatchService(new NoGitCli());
        var diff = FileDiff.EmptyFor("f.txt", DiffKind.Modified) with
        {
            RawPatch = "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1,2 +1,3 @@\n x\n-a\n\\ No newline at end of file\n+a\n+b\n\\ No newline at end of file\n",
        };

        // In reverse direction, selecting only -a leaves unselected +a and +b as context after -a.
        // -a has '\ No newline', and later lines exist on the old side -> throws!
        var ex = Assert.Throws<HistoriaGitException>(() =>
            svc.GeneratePatch(diff, PatchScope.SelectedLines, new HashSet<DiffLineRef> { new(0, 1) }, reverse: true));
        Assert.Contains("This selection splits an end-of-file newline change. Include the file's last line, or stage the whole hunk.", ex.Message);
    }

    [Fact]
    public async Task Last_line_change_both_sides_no_eol_selection_behavior()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.FromTemplate("patch-b16-both-no-eol-seed", r =>
        {
            r.WriteFile("f.txt", "x\na").Stage().Commit("base");
            r.WriteFile("f.txt", "x\na2");
        });
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        var diff = UnstagedDiff(b.Path, "f.txt");
        var hunk = Assert.Single(diff.Hunks);
        var minusA = Enumerable.Range(0, hunk.Lines.Count)
            .First(i => hunk.Lines[i].Type == DiffLineType.Deletion && hunk.Lines[i].Content == "a");
        var plusA2 = Enumerable.Range(0, hunk.Lines.Count)
            .First(i => hunk.Lines[i].Type == DiffLineType.Addition && hunk.Lines[i].Content == "a2");

        // 1. +a2 alone throws (it would append to a line without newline)
        var ex = Assert.Throws<HistoriaGitException>(() =>
            svc.GeneratePatch(diff, PatchScope.SelectedLines, new HashSet<DiffLineRef> { new(0, plusA2) }, reverse: false));
        Assert.Contains("This selection splits an end-of-file newline change. Include the file's last line, or stage the whole hunk.", ex.Message);

        // 2. -a alone stages the deletion cleanly
        var patchDel = svc.GeneratePatch(diff, PatchScope.SelectedLines, new HashSet<DiffLineRef> { new(0, minusA) }, reverse: false);
        await svc.ApplyPatchAsync(ctx, patchDel, PatchTarget.StagingArea, reverse: false);
        Assert.Equal("x\n", b.Git("show", ":f.txt"));

        // Reset index
        b.Git("reset", "HEAD", "f.txt");

        // 3. Both -a and +a2 together stage a2
        var patchBoth = svc.GeneratePatch(diff, PatchScope.SelectedLines, new HashSet<DiffLineRef> { new(0, minusA), new(0, plusA2) }, reverse: false);
        await svc.ApplyPatchAsync(ctx, patchBoth, PatchTarget.StagingArea, reverse: false);
        Assert.Equal("x\na2", b.Git("show", ":f.txt"));
    }
}
