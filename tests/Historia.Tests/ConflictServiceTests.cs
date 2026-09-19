namespace Historia.Tests;

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
    public async Task Lists_and_loads_a_both_modified_conflict_from_index_stages()
    {
        await using var host = new MiniHost();
        using var repoDir = ConflictedRepo();
        await using var ctx = host.Open(repoDir.Path);
        var svc = Make(host);

        var conflicts = await svc.GetConflictsAsync(ctx);
        var file = Assert.Single(conflicts);
        Assert.Equal("f.txt", file.FilePath);
        Assert.Equal(ConflictStatus.BothModified, file.Status);

        var loaded = await svc.LoadConflictContentAsync(ctx, "f.txt");
        Assert.Contains("shared", loaded.BaseContent);
        Assert.Contains("OURS", loaded.OursContent);
        Assert.Contains("THEIRS", loaded.TheirsContent);
    }

    [Fact]
    public void ParseMarkerRegions_finds_the_conflict_region()
    {
        var svc = new ConflictService(new NoGitCli(), null!);
        var wt = "a\n<<<<<<< HEAD\nours line\n=======\ntheirs line\n>>>>>>> feature\nb\n";
        var region = Assert.Single(svc.ParseMarkerRegions(wt));
        Assert.Equal("ours line", region.OursText);
        Assert.Equal("theirs line", region.TheirsText);
        Assert.Null(region.BaseText);
    }

    [Fact]
    public void ParseMarkerRegions_reads_diff3_base_section()
    {
        var svc = new ConflictService(new NoGitCli(), null!);
        var wt = "<<<<<<< HEAD\nours\n||||||| base\nancestor\n=======\ntheirs\n>>>>>>> x\n";
        var region = Assert.Single(svc.ParseMarkerRegions(wt));
        Assert.Equal("ancestor", region.BaseText);
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

        // Merge commit created, working tree clean, "OURS" kept.
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

    // A conflicted path containing
    // shell metacharacters must reach the external merge tool as one literal argument, never as
    // something a shell re-parses into extra commands.
    [Fact]
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
        var script = Path.Combine(Path.GetTempPath(), $"historia-mergetool-test-{Guid.NewGuid():N}.ps1");
        var marker = Path.Combine(Path.GetTempPath(), $"historia-mergetool-test-{Guid.NewGuid():N}.txt");
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
            try { File.Delete(script); } catch { /* best effort */ }
            try { File.Delete(marker); } catch { /* best effort */ }
        }
    }

    // ── external merge tool command-line handling (§findings: cmd.exe /c double-escaping) ──

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

    [Fact]
    public async Task LaunchExternalMergeToolAsync_runs_the_configured_tool_directly_not_via_a_wrapping_shell()
    {
        // "cmd.exe" here plays the role of a user-configured external tool: each substituted
        // token reaches it as its own argv entry via ArgumentList, exactly as production code
        // launches whatever the user configures — not as one shell command string this service
        // builds and hands to a nested "cmd.exe /c" (the old, double-escaping path).
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
}
