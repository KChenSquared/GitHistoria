namespace Historia.Tests;

[Trait("tier", "git")]
public class PatchServiceFuzzTests
{
    private static readonly string[] Alpha = ["a", "b", "c", "", "}", "x = 1;", "x = 2;"];
    private static readonly int[] ContextCycle = [0, 0, 1, 3];

    private static int GetCaseBudget()
    {
        var env = Environment.GetEnvironmentVariable("HISTORIA_PATCH_FUZZ_CASES");
        return int.TryParse(env, out var n) && n > 0 ? n : 40;
    }

    private static PatchService Make(MiniHost host)
        => new((IGitCliService)host.Provider.GetService(typeof(IGitCliService))!);

    private static List<string> ComputeOracle(List<string> baseLines, IReadOnlyList<DiffHunk> hunks, IReadOnlySet<DiffLineRef> selected, bool reverse)
    {
        var exp = new List<string>();
        int ptr = 0;
        for (int h = 0; h < hunks.Count; h++)
        {
            var hk = hunks[h];
            int start = reverse
                ? (hk.NewLineCount == 0 ? hk.NewStart : hk.NewStart - 1)
                : (hk.OldLineCount == 0 ? hk.OldStart : hk.OldStart - 1);

            while (ptr < start && ptr < baseLines.Count)
            {
                exp.Add(baseLines[ptr++]);
            }

            for (int li = 0; li < hk.Lines.Count; li++)
            {
                var l = hk.Lines[li];
                bool s = selected.Contains(new DiffLineRef(h, li));
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
                        else
                        {
                            if (ptr < baseLines.Count) exp.Add(baseLines[ptr++]);
                        }
                    }
                    else
                    {
                        if (s) exp.Add(l.Content);
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
                        else
                        {
                            if (ptr < baseLines.Count) exp.Add(baseLines[ptr++]);
                        }
                    }
                    else
                    {
                        if (s) exp.Add(l.Content);
                    }
                }
            }
        }

        while (ptr < baseLines.Count)
        {
            exp.Add(baseLines[ptr++]);
        }

        return exp;
    }

    [Fact]
    public async Task Random_line_selections_stage_the_intended_lines()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.Create();
        b.Git("config", "core.autocrlf", "false");
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        File.WriteAllText(Path.Combine(b.Path, "f.txt"), "init\n");
        b.Git("add", "f.txt");
        b.Git("commit", "-m", "init", "--no-gpg-sign");

        var rnd = new Random(101);
        int totalCases = GetCaseBudget();
        int tested = 0;

        for (int caseIdx = 0; tested < totalCases; caseIdx++)
        {
            int n = rnd.Next(10, 60);
            var oldL = Enumerable.Range(0, n).Select(_ => Alpha[rnd.Next(Alpha.Length)]).ToList();
            var newL = new List<string>(oldL);
            int edits = rnd.Next(1, 8);
            for (int k = 0; k < edits; k++)
            {
                int pos = rnd.Next(0, newL.Count + 1);
                switch (rnd.Next(3))
                {
                    case 0: if (pos < newL.Count) newL.RemoveAt(pos); break;
                    case 1: newL.Insert(pos, Alpha[rnd.Next(Alpha.Length)]); break;
                    default: if (pos < newL.Count) newL[pos] = Alpha[rnd.Next(Alpha.Length)] + "!"; break;
                }
            }

            bool crlf = rnd.Next(4) == 0;
            string eol = crlf ? "\r\n" : "\n";
            string Join(List<string> l) => string.Join(eol, l) + eol;

            var oldText = Join(oldL);
            var newText = Join(newL);

            File.WriteAllText(Path.Combine(b.Path, "f.txt"), oldText);
            b.Git("add", "f.txt");
            File.WriteAllText(Path.Combine(b.Path, "f.txt"), newText);

            int ctxLines = ContextCycle[tested % ContextCycle.Length];
            FileDiff diff;
            using (var repo = new Repository(b.Path))
            {
                diff = new LibGit2Service().ProjectDiff(repo, new DiffRequest
                {
                    Source = DiffSource.UnstagedChanges,
                    Path = "f.txt",
                    ContextLines = ctxLines,
                });
            }

            if (diff.Hunks.Count == 0)
                continue;

            tested++;

            var hunks = diff.Hunks;
            var sel = new HashSet<DiffLineRef>();
            var oracleSel = new HashSet<DiffLineRef>();
            PatchScope scope;

            if (rnd.Next(2) == 0)
            {
                scope = PatchScope.SingleHunk;
                int hIdx = rnd.Next(hunks.Count);
                sel.Add(new DiffLineRef(hIdx, 0));
                for (int li = 0; li < hunks[hIdx].Lines.Count; li++)
                {
                    if (hunks[hIdx].Lines[li].Type != DiffLineType.Context)
                        oracleSel.Add(new DiffLineRef(hIdx, li));
                }
            }
            else
            {
                scope = PatchScope.SelectedLines;
                for (int h = 0; h < hunks.Count; h++)
                {
                    for (int li = 0; li < hunks[h].Lines.Count; li++)
                    {
                        if (hunks[h].Lines[li].Type != DiffLineType.Context && rnd.Next(2) == 0)
                        {
                            sel.Add(new DiffLineRef(h, li));
                            oracleSel.Add(new DiffLineRef(h, li));
                        }
                    }
                }
                if (sel.Count == 0)
                {
                    for (int h = 0; h < hunks.Count && sel.Count == 0; h++)
                    {
                        for (int li = 0; li < hunks[h].Lines.Count; li++)
                        {
                            if (hunks[h].Lines[li].Type != DiffLineType.Context)
                            {
                                sel.Add(new DiffLineRef(h, li));
                                oracleSel.Add(new DiffLineRef(h, li));
                                break;
                            }
                        }
                    }
                }
            }

            if (sel.Count == 0)
                continue;

            var exp = ComputeOracle(oldL, hunks, oracleSel, reverse: false);
            string expected = string.Join(eol, exp) + eol;

            string patch;
            try
            {
                patch = svc.GeneratePatch(diff, scope, sel, reverse: false);
            }
            catch (HistoriaGitException)
            {
                continue;
            }

            if (string.IsNullOrEmpty(patch))
                continue;

            await svc.ApplyPatchAsync(ctx, patch, PatchTarget.StagingArea, reverse: false);

            var actual = b.Git("cat-file", "-p", ":f.txt");
            if (actual != expected)
            {
                Assert.Fail($"Seed: 101, Case: {caseIdx}, Dir: forward, Ctx: {ctxLines}, Scope: {scope}\nPatch:\n{patch}\nOld:\n{string.Join("|", oldL)}\nExpected:\n{string.Join("|", exp)}\nActual:\n{string.Join("|", actual.Split('\n'))}");
            }
        }
    }

    [Fact]
    public async Task Random_line_selections_unstage_the_intended_lines()
    {
        await using var host = new MiniHost();
        using var b = TestRepositoryBuilder.Create();
        b.Git("config", "core.autocrlf", "false");
        await using var ctx = host.Open(b.Path);
        var svc = Make(host);

        var rnd = new Random(202);
        int totalCases = GetCaseBudget();
        int tested = 0;

        for (int caseIdx = 0; tested < totalCases; caseIdx++)
        {
            int n = rnd.Next(10, 60);
            var oldL = Enumerable.Range(0, n).Select(_ => Alpha[rnd.Next(Alpha.Length)]).ToList();
            var newL = new List<string>(oldL);
            int edits = rnd.Next(1, 8);
            for (int k = 0; k < edits; k++)
            {
                int pos = rnd.Next(0, newL.Count + 1);
                switch (rnd.Next(3))
                {
                    case 0: if (pos < newL.Count) newL.RemoveAt(pos); break;
                    case 1: newL.Insert(pos, Alpha[rnd.Next(Alpha.Length)]); break;
                    default: if (pos < newL.Count) newL[pos] = Alpha[rnd.Next(Alpha.Length)] + "!"; break;
                }
            }

            bool crlf = rnd.Next(4) == 0;
            string eol = crlf ? "\r\n" : "\n";
            string Join(List<string> l) => string.Join(eol, l) + eol;

            var oldText = Join(oldL);
            var newText = Join(newL);

            using (var repo = new Repository(b.Path))
            {
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(oldText));
                var blob = repo.ObjectDatabase.CreateBlob(ms);
                var treeDef = new TreeDefinition();
                treeDef.Add("f.txt", blob, Mode.NonExecutableFile);
                var tree = repo.ObjectDatabase.CreateTree(treeDef);
                var sig = new Signature("Test", "test@example.com", DateTimeOffset.UnixEpoch);
                var parent = repo.Head.Tip;
                var commit = repo.ObjectDatabase.CreateCommit(sig, sig, $"base-{caseIdx}", tree, parent != null ? [parent] : [], false);
                repo.Refs.UpdateTarget(repo.Refs.Head, commit.Id);
            }

            // Stage new content
            File.WriteAllText(Path.Combine(b.Path, "f.txt"), newText);
            b.Git("add", "f.txt");

            int ctxLines = ContextCycle[tested % ContextCycle.Length];
            FileDiff diff;
            using (var repo = new Repository(b.Path))
            {
                diff = new LibGit2Service().ProjectDiff(repo, new DiffRequest
                {
                    Source = DiffSource.StagedChanges,
                    Path = "f.txt",
                    ContextLines = ctxLines,
                });
            }

            if (diff.Hunks.Count == 0)
                continue;

            tested++;

            var hunks = diff.Hunks;
            var sel = new HashSet<DiffLineRef>();
            var oracleSel = new HashSet<DiffLineRef>();
            PatchScope scope;

            if (rnd.Next(2) == 0)
            {
                scope = PatchScope.SingleHunk;
                int hIdx = rnd.Next(hunks.Count);
                sel.Add(new DiffLineRef(hIdx, 0));
                for (int li = 0; li < hunks[hIdx].Lines.Count; li++)
                {
                    if (hunks[hIdx].Lines[li].Type != DiffLineType.Context)
                        oracleSel.Add(new DiffLineRef(hIdx, li));
                }
            }
            else
            {
                scope = PatchScope.SelectedLines;
                for (int h = 0; h < hunks.Count; h++)
                {
                    for (int li = 0; li < hunks[h].Lines.Count; li++)
                    {
                        if (hunks[h].Lines[li].Type != DiffLineType.Context && rnd.Next(2) == 0)
                        {
                            sel.Add(new DiffLineRef(h, li));
                            oracleSel.Add(new DiffLineRef(h, li));
                        }
                    }
                }
                if (sel.Count == 0)
                {
                    for (int h = 0; h < hunks.Count && sel.Count == 0; h++)
                    {
                        for (int li = 0; li < hunks[h].Lines.Count; li++)
                        {
                            if (hunks[h].Lines[li].Type != DiffLineType.Context)
                            {
                                sel.Add(new DiffLineRef(h, li));
                                oracleSel.Add(new DiffLineRef(h, li));
                                break;
                            }
                        }
                    }
                }
            }

            if (sel.Count == 0)
                continue;

            var exp = ComputeOracle(newL, hunks, oracleSel, reverse: true);
            string expected = string.Join(eol, exp) + eol;

            string patch;
            try
            {
                patch = svc.GeneratePatch(diff, scope, sel, reverse: true);
            }
            catch (HistoriaGitException)
            {
                continue;
            }

            if (string.IsNullOrEmpty(patch))
                continue;

            await svc.ApplyPatchAsync(ctx, patch, PatchTarget.StagingArea, reverse: true);

            var actual = b.Git("cat-file", "-p", ":f.txt");
            if (actual != expected)
            {
                Assert.Fail($"Seed: 202, Case: {caseIdx}, Dir: reverse, Ctx: {ctxLines}, Scope: {scope}\nPatch:\n{patch}\nOld:\n{string.Join("|", newL)}\nExpected:\n{string.Join("|", exp)}\nActual:\n{string.Join("|", actual.Split('\n'))}");
            }
        }
    }

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

        var rnd = new Random(303);
        int totalCases = Environment.GetEnvironmentVariable("HISTORIA_PATCH_FUZZ_CASES") != null ? GetCaseBudget() : 10;
        int tested = 0;

        for (int caseIdx = 0; tested < totalCases; caseIdx++)
        {
            int n = rnd.Next(10, 60);
            var oldL = Enumerable.Range(0, n).Select(_ => Alpha[rnd.Next(Alpha.Length)]).ToList();
            var newL = new List<string>(oldL);
            int edits = rnd.Next(1, 8);
            for (int k = 0; k < edits; k++)
            {
                int pos = rnd.Next(0, newL.Count + 1);
                switch (rnd.Next(3))
                {
                    case 0: if (pos < newL.Count) newL.RemoveAt(pos); break;
                    case 1: newL.Insert(pos, Alpha[rnd.Next(Alpha.Length)]); break;
                    default: if (pos < newL.Count) newL[pos] = Alpha[rnd.Next(Alpha.Length)] + "!"; break;
                }
            }

            bool crlf = rnd.Next(4) == 0;
            string eol = crlf ? "\r\n" : "\n";
            bool noOldNl = rnd.Next(2) == 0;
            bool noNewNl = rnd.Next(2) == 0;
            if (!noOldNl && !noNewNl) noOldNl = true;

            string oldText = string.Join(eol, oldL) + (noOldNl ? "" : eol);
            string newText = string.Join(eol, newL) + (noNewNl ? "" : eol);

            File.WriteAllText(Path.Combine(b.Path, "f.txt"), oldText);
            b.Git("add", "f.txt");
            File.WriteAllText(Path.Combine(b.Path, "f.txt"), newText);

            int ctxLines = ContextCycle[tested % ContextCycle.Length];
            FileDiff diff;
            using (var repo = new Repository(b.Path))
            {
                diff = new LibGit2Service().ProjectDiff(repo, new DiffRequest
                {
                    Source = DiffSource.UnstagedChanges,
                    Path = "f.txt",
                    ContextLines = ctxLines,
                });
            }

            if (diff.Hunks.Count == 0)
                continue;

            tested++;

            var hunks = diff.Hunks;
            var sel = new HashSet<DiffLineRef>();
            var oracleSel = new HashSet<DiffLineRef>();
            PatchScope scope;

            if (rnd.Next(2) == 0)
            {
                scope = PatchScope.SingleHunk;
                int hIdx = rnd.Next(hunks.Count);
                sel.Add(new DiffLineRef(hIdx, 0));
                for (int li = 0; li < hunks[hIdx].Lines.Count; li++)
                {
                    if (hunks[hIdx].Lines[li].Type != DiffLineType.Context)
                        oracleSel.Add(new DiffLineRef(hIdx, li));
                }
            }
            else
            {
                scope = PatchScope.SelectedLines;
                for (int h = 0; h < hunks.Count; h++)
                {
                    for (int li = 0; li < hunks[h].Lines.Count; li++)
                    {
                        if (hunks[h].Lines[li].Type != DiffLineType.Context && rnd.Next(2) == 0)
                        {
                            sel.Add(new DiffLineRef(h, li));
                            oracleSel.Add(new DiffLineRef(h, li));
                        }
                    }
                }
                if (sel.Count == 0)
                {
                    for (int h = 0; h < hunks.Count && sel.Count == 0; h++)
                    {
                        for (int li = 0; li < hunks[h].Lines.Count; li++)
                        {
                            if (hunks[h].Lines[li].Type != DiffLineType.Context)
                            {
                                sel.Add(new DiffLineRef(h, li));
                                oracleSel.Add(new DiffLineRef(h, li));
                                break;
                            }
                        }
                    }
                }
            }

            if (sel.Count == 0)
                continue;

            string patch;
            try
            {
                patch = svc.GeneratePatch(diff, scope, sel, reverse: false);
            }
            catch (HistoriaGitException)
            {
                // B16 refusal is a valid pass
                continue;
            }

            if (string.IsNullOrEmpty(patch))
                continue;

            var check = await svc.CheckAsync(ctx, patch, PatchTarget.StagingArea, reverse: false);
            if (!check.CanApply)
                continue;

            await svc.ApplyPatchAsync(ctx, patch, PatchTarget.StagingArea, reverse: false);

            var actual = b.Git("cat-file", "-p", ":f.txt");
            var exp = ComputeOracle(oldL, hunks, oracleSel, reverse: false);
            var expectedTrimmed = string.Join(eol, exp).TrimEnd('\r', '\n');
            var actualTrimmed = actual.TrimEnd('\r', '\n');
            if (actualTrimmed != expectedTrimmed)
            {
                Assert.Fail($"Seed: 303, Case: {caseIdx}, EOF variant\nPatch:\n{patch}\nOld:\n{string.Join("|", oldL)}\nExpected:\n{expectedTrimmed}\nActual:\n{actualTrimmed}");
            }
        }
    }
}
