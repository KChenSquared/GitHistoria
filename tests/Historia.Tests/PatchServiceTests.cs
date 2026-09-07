using LibGit2Sharp;
using Historia.Models;
using Historia.Services.Git;
using Historia.Services.PowerTools;
using Xunit;

namespace Historia.Tests;

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
        using var b = TestRepositoryBuilder.Create();
        // 12 numbered lines; change line 2 (hunk A) and line 11 (hunk B).
        b.WriteFile("f.txt", string.Join("\n", Enumerable.Range(1, 12).Select(i => $"line {i}")) + "\n").Stage().Commit("base");
        var lines = Enumerable.Range(1, 12).Select(i => $"line {i}").ToArray();
        lines[1] = "line 2 CHANGED";
        lines[10] = "line 11 CHANGED";
        b.WriteFile("f.txt", string.Join("\n", lines) + "\n");
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
        using var b = TestRepositoryBuilder.Create();
        b.WriteFile("f.txt", "a\nb\nc\n").Stage().Commit("base");
        b.WriteFile("f.txt", "a\nADD1\nb\nADD2\nc\n"); // two separate additions in one hunk
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
        using var b = TestRepositoryBuilder.Create();
        b.WriteFile("f.txt", string.Join("\n", Enumerable.Range(1, 12).Select(i => $"L{i}")) + "\n").Stage().Commit("base");
        var lines = Enumerable.Range(1, 12).Select(i => $"L{i}").ToArray();
        lines[1] = "L2 X";
        lines[10] = "L11 Y";
        b.WriteFile("f.txt", string.Join("\n", lines) + "\n");
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
        using var b = TestRepositoryBuilder.Create();
        b.WriteFile("f.txt", "one\ntwo\nthree").Stage().Commit("base"); // no trailing newline
        b.WriteFile("f.txt", "one\ntwoX\nthree");
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        var diff = UnstagedDiff(b.Path, "f.txt");
        var patch = svc.GeneratePatch(diff, PatchScope.SingleHunk, new HashSet<DiffLineRef> { new(0, 0) });
        var check = await svc.CheckAsync(ctx, patch, PatchTarget.StagingArea, reverse: false);
        Assert.True(check.CanApply, check.FailureReason);

        await svc.ApplyPatchAsync(ctx, patch, PatchTarget.StagingArea, reverse: false);
        Assert.Contains("twoX", b.Git("diff", "--cached"));
    }
}
