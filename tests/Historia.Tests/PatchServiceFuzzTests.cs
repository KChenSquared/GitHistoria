namespace Historia.Tests;

/// <summary>
/// Partial staging and unstaging through the real path (<c>ProjectDiff</c> → <c>GeneratePatch</c> →
/// <c>git apply</c>) against random files, random line selections and random context sizes. The
/// expected index content comes from an oracle that reasons over the parsed hunks, never over the
/// generated patch text. Seeds are fixed; <c>HISTORIA_PATCH_FUZZ_CASES</c> raises the case count
/// for a local soak.
/// </summary>
[Trait("tier", "git")]
public class PatchServiceFuzzTests
{
    private const int StageSeed = 101;
    private const int UnstageSeed = 202;
    private const int EofSeed = 303;
    private const int DefaultEofCases = 10;

    private static readonly string[] Alpha = ["a", "b", "c", "", "}", "x = 1;", "x = 2;"];
    private static readonly int[] ContextCycle = [0, 0, 1, 3];

    private sealed record FuzzCase(List<string> OldLines, List<string> NewLines, string Eol);

    private sealed record FuzzSelection(PatchScope Scope, HashSet<DiffLineRef> Selected, HashSet<DiffLineRef> Oracle);

    private static int? CaseBudgetOverride()
    {
        var env = Environment.GetEnvironmentVariable("HISTORIA_PATCH_FUZZ_CASES");
        return int.TryParse(env, out var n) && n > 0 ? n : null;
    }

    private static int CaseBudget(int fallback) => CaseBudgetOverride() ?? fallback;

    private static PatchService Make(MiniHost host)
        => new((IGitCliService)host.Provider.GetService(typeof(IGitCliService))!);

    private static FuzzCase NextCase(Random rnd)
    {
        var n = rnd.Next(10, 60);
        var oldLines = Enumerable.Range(0, n).Select(_ => Alpha[rnd.Next(Alpha.Length)]).ToList();
        var newLines = new List<string>(oldLines);
        var edits = rnd.Next(1, 8);
        for (var k = 0; k < edits; k++)
        {
            var pos = rnd.Next(0, newLines.Count + 1);
            switch (rnd.Next(3))
            {
                case 0: if (pos < newLines.Count) newLines.RemoveAt(pos); break;
                case 1: newLines.Insert(pos, Alpha[rnd.Next(Alpha.Length)]); break;
                default: if (pos < newLines.Count) newLines[pos] = Alpha[rnd.Next(Alpha.Length)] + "!"; break;
            }
        }

        return new FuzzCase(oldLines, newLines, rnd.Next(4) == 0 ? "\r\n" : "\n");
    }

    private static FileDiff ProjectDiff(string repoPath, DiffSource source, int contextLines)
    {
        using var repo = new Repository(repoPath);
        return repo.ProjectDiff(new DiffRequest
        {
            Source = source,
            Path = "f.txt",
            ContextLines = contextLines,
        });
    }

    private static FuzzSelection PickSelection(Random rnd, IReadOnlyList<DiffHunk> hunks)
    {
        var selected = new HashSet<DiffLineRef>();
        var oracle = new HashSet<DiffLineRef>();

        if (rnd.Next(2) == 0)
        {
            var hunkIndex = rnd.Next(hunks.Count);
            selected.Add(new DiffLineRef(hunkIndex, 0));
            for (var li = 0; li < hunks[hunkIndex].Lines.Count; li++)
            {
                if (hunks[hunkIndex].Lines[li].Type != DiffLineType.Context)
                    oracle.Add(new DiffLineRef(hunkIndex, li));
            }

            return new FuzzSelection(PatchScope.SingleHunk, selected, oracle);
        }

        for (var h = 0; h < hunks.Count; h++)
        {
            for (var li = 0; li < hunks[h].Lines.Count; li++)
            {
                if (hunks[h].Lines[li].Type != DiffLineType.Context && rnd.Next(2) == 0)
                {
                    selected.Add(new DiffLineRef(h, li));
                    oracle.Add(new DiffLineRef(h, li));
                }
            }
        }

        if (selected.Count == 0)
        {
            for (var h = 0; h < hunks.Count && selected.Count == 0; h++)
            {
                for (var li = 0; li < hunks[h].Lines.Count; li++)
                {
                    if (hunks[h].Lines[li].Type != DiffLineType.Context)
                    {
                        selected.Add(new DiffLineRef(h, li));
                        oracle.Add(new DiffLineRef(h, li));
                        break;
                    }
                }
            }
        }

        return new FuzzSelection(PatchScope.SelectedLines, selected, oracle);
    }

    private static List<string> ComputeOracle(List<string> baseLines, IReadOnlyList<DiffHunk> hunks, IReadOnlySet<DiffLineRef> selected, bool reverse)
    {
        var exp = new List<string>();
        var ptr = 0;
        for (var h = 0; h < hunks.Count; h++)
        {
            var hk = hunks[h];
            var start = reverse
                ? (hk.NewLineCount == 0 ? hk.NewStart : hk.NewStart - 1)
                : (hk.OldLineCount == 0 ? hk.OldStart : hk.OldStart - 1);

            while (ptr < start && ptr < baseLines.Count)
                exp.Add(baseLines[ptr++]);

            for (var li = 0; li < hk.Lines.Count; li++)
            {
                var l = hk.Lines[li];
                var s = selected.Contains(new DiffLineRef(h, li));
                if (l.Type == DiffLineType.Context)
                {
                    if (ptr < baseLines.Count)
                        exp.Add(baseLines[ptr++]);
                }
                else if (!reverse)
                {
                    if (l.Type == DiffLineType.Deletion)
                    {
                        if (s)
                        {
                            if (ptr < baseLines.Count) ptr++;
                        }
                        else if (ptr < baseLines.Count)
                        {
                            exp.Add(baseLines[ptr++]);
                        }
                    }
                    else if (s)
                    {
                        exp.Add(l.Content);
                    }
                }
                else
                {
                    if (l.Type == DiffLineType.Addition)
                    {
                        if (s)
                        {
                            if (ptr < baseLines.Count) ptr++;
                        }
                        else if (ptr < baseLines.Count)
                        {
                            exp.Add(baseLines[ptr++]);
                        }
                    }
                    else if (s)
                    {
                        exp.Add(l.Content);
                    }
                }
            }
        }

        while (ptr < baseLines.Count)
            exp.Add(baseLines[ptr++]);

        return exp;
    }

    private static string Describe(int seed, int caseIdx, string direction, int ctxLines, PatchScope scope, string patch, IEnumerable<string> baseLines, string expected, string actual)
        => $"Seed: {seed}, Case: {caseIdx}, Dir: {direction}, Ctx: {ctxLines}, Scope: {scope}\nPatch:\n{patch}\nBase:\n{string.Join("|", baseLines)}\nExpected:\n{expected}\nActual:\n{actual}";

    private static void PrepareStage(TestRepositoryBuilder b, FuzzCase c)
    {
        File.WriteAllText(Path.Combine(b.Path, "f.txt"), string.Join(c.Eol, c.OldLines) + c.Eol);
        b.Git("add", "f.txt");
        File.WriteAllText(Path.Combine(b.Path, "f.txt"), string.Join(c.Eol, c.NewLines) + c.Eol);
    }

    private static void PrepareUnstage(TestRepositoryBuilder b, FuzzCase c, int caseIdx)
    {
        using (var repo = new Repository(b.Path))
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(string.Join(c.Eol, c.OldLines) + c.Eol));
            var blob = repo.ObjectDatabase.CreateBlob(ms);
            var treeDef = new TreeDefinition();
            treeDef.Add("f.txt", blob, Mode.NonExecutableFile);
            var tree = repo.ObjectDatabase.CreateTree(treeDef);
            var sig = new Signature("Test", "test@example.com", DateTimeOffset.UnixEpoch);
            var parent = repo.Head.Tip;
            var commit = repo.ObjectDatabase.CreateCommit(sig, sig, $"base-{caseIdx}", tree, parent != null ? [parent] : [], false);
            repo.Refs.UpdateTarget(repo.Refs.Head, commit.Id);
        }

        File.WriteAllText(Path.Combine(b.Path, "f.txt"), string.Join(c.Eol, c.NewLines) + c.Eol);
        b.Git("add", "f.txt");
    }

    private static async Task RunAsync(int seed, bool reverse)
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.Create();
        b.Git("config", "core.autocrlf", "false");
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);
        var direction = reverse ? "reverse" : "forward";

        if (!reverse)
        {
            File.WriteAllText(Path.Combine(b.Path, "f.txt"), "init\n");
            b.Git("add", "f.txt");
            b.Git("commit", "-m", "init", "--no-gpg-sign");
        }

        var rnd = new Random(seed);
        var totalCases = CaseBudget(40);
        var tested = 0;

        for (var caseIdx = 0; tested < totalCases; caseIdx++)
        {
            var c = NextCase(rnd);
            if (reverse)
                PrepareUnstage(b, c, caseIdx);
            else
                PrepareStage(b, c);

            var ctxLines = ContextCycle[tested % ContextCycle.Length];
            var diff = ProjectDiff(b.Path, reverse ? DiffSource.StagedChanges : DiffSource.UnstagedChanges, ctxLines);
            if (diff.Hunks.Count == 0)
                continue;

            tested++;

            var pick = PickSelection(rnd, diff.Hunks);
            if (pick.Selected.Count == 0)
                Assert.Fail($"Seed: {seed}, Case: {caseIdx}: a non-empty diff produced no selectable line.");

            var baseLines = reverse ? c.NewLines : c.OldLines;
            var expectedLines = ComputeOracle(baseLines, diff.Hunks, pick.Oracle, reverse);
            var expected = string.Join(c.Eol, expectedLines) + c.Eol;

            string patch;
            try
            {
                patch = svc.GeneratePatch(diff, pick.Scope, pick.Selected, reverse);
            }
            catch (HistoriaGitException ex)
            {
                patch = string.Empty;
                Assert.Fail($"Seed: {seed}, Case: {caseIdx}, Dir: {direction}, Ctx: {ctxLines}, Scope: {pick.Scope}: GeneratePatch refused a file that ends in a newline: {ex.Message}");
            }

            if (string.IsNullOrEmpty(patch))
                Assert.Fail($"Seed: {seed}, Case: {caseIdx}, Dir: {direction}, Ctx: {ctxLines}, Scope: {pick.Scope}: GeneratePatch returned an empty patch for a non-empty selection.");

            try
            {
                await svc.ApplyPatchAsync(ctx, patch, PatchTarget.StagingArea, reverse);
            }
            catch (HistoriaGitException ex)
            {
                Assert.Fail(Describe(seed, caseIdx, direction, ctxLines, pick.Scope, patch, baseLines, expected, $"apply failed: {ex.Message}"));
            }

            var actual = b.Git("cat-file", "-p", ":f.txt");
            if (actual != expected)
                Assert.Fail(Describe(seed, caseIdx, direction, ctxLines, pick.Scope, patch, baseLines, string.Join("|", expectedLines), string.Join("|", actual.Split('\n'))));
        }
    }

    [Fact]
    public Task Random_line_selections_stage_the_intended_lines() => RunAsync(StageSeed, reverse: false);

    [Fact]
    public Task Random_line_selections_unstage_the_intended_lines() => RunAsync(UnstageSeed, reverse: true);

    [Fact]
    public async Task Random_selections_without_a_final_newline_stay_correct_or_are_refused()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.Create();
        b.Git("config", "core.autocrlf", "false");
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        File.WriteAllText(Path.Combine(b.Path, "f.txt"), "init\n");
        b.Git("add", "f.txt");
        b.Git("commit", "-m", "init", "--no-gpg-sign");

        var rnd = new Random(EofSeed);
        var totalCases = CaseBudget(DefaultEofCases);
        var tested = 0;

        for (var caseIdx = 0; tested < totalCases; caseIdx++)
        {
            var c = NextCase(rnd);
            var noOldNl = rnd.Next(2) == 0;
            var noNewNl = rnd.Next(2) == 0;
            if (!noOldNl && !noNewNl)
                noOldNl = true;

            File.WriteAllText(Path.Combine(b.Path, "f.txt"), string.Join(c.Eol, c.OldLines) + (noOldNl ? "" : c.Eol));
            b.Git("add", "f.txt");
            File.WriteAllText(Path.Combine(b.Path, "f.txt"), string.Join(c.Eol, c.NewLines) + (noNewNl ? "" : c.Eol));

            var ctxLines = ContextCycle[tested % ContextCycle.Length];
            var diff = ProjectDiff(b.Path, DiffSource.UnstagedChanges, ctxLines);
            if (diff.Hunks.Count == 0)
                continue;

            tested++;

            var pick = PickSelection(rnd, diff.Hunks);

            string patch;
            try
            {
                patch = svc.GeneratePatch(diff, pick.Scope, pick.Selected, reverse: false);
            }
            catch (HistoriaGitException)
            {
                // The end-of-file newline refusal is a valid outcome for this variant.
                continue;
            }

            if (string.IsNullOrEmpty(patch))
                continue;

            var check = await svc.CheckAsync(ctx, patch, PatchTarget.StagingArea, reverse: false);
            if (!check.CanApply)
                continue;

            await svc.ApplyPatchAsync(ctx, patch, PatchTarget.StagingArea, reverse: false);

            var actual = b.Git("cat-file", "-p", ":f.txt");
            var expectedTrimmed = string.Join(c.Eol, ComputeOracle(c.OldLines, diff.Hunks, pick.Oracle, reverse: false)).TrimEnd('\r', '\n');
            var actualTrimmed = actual.TrimEnd('\r', '\n');
            if (actualTrimmed != expectedTrimmed)
                Assert.Fail($"Seed: {EofSeed}, Case: {caseIdx}, EOF variant, Ctx: {ctxLines}, Scope: {pick.Scope}\nPatch:\n{patch}\nBase:\n{string.Join("|", c.OldLines)}\nExpected:\n{expectedTrimmed}\nActual:\n{actualTrimmed}");
        }
    }
}
