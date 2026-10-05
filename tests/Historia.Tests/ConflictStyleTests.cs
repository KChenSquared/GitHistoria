namespace Historia.Tests;

/// <summary>
/// Conflicts Historia creates carry a zdiff3 base section, even when the repository asks for the
/// plain two-way style — the resolver's "Use base" depends on it and nothing errors without it.
/// </summary>
[Trait("tier", "git")]
public class ConflictStyleTests
{
    private static TestRepositoryBuilder DivergedRepo()
    {
        var repo = TestRepositoryBuilder.Create();
        repo.WriteFile("f.txt", "line1\nshared\nline3\n").Stage().Commit("base");
        repo.Checkout("feature", create: true).WriteFile("f.txt", "line1\nTHEIRS\nline3\n").Stage().Commit("theirs");
        repo.Checkout("main").WriteFile("f.txt", "line1\nOURS\nline3\n").Stage().Commit("ours");
        return repo;
    }

    private static async Task<string> MergeThroughCliAsync(MiniHost host, TestRepositoryBuilder repo)
    {
        var cli = host.Provider.GetRequiredService<IGitCliService>();
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = ["merge", "--no-edit", "feature"],
            WorkingDirectory = repo.Path,
        });
        Assert.False(result.IsSuccess, "the merge must stop on a conflict");
        return await File.ReadAllTextAsync(Path.Combine(repo.Path, "f.txt"));
    }

    [Fact]
    public void DeterministicPrefix_sets_zdiff3_conflict_style()
    {
        var prefix = GitCliProcessSetup.DeterministicPrefix;
        int at = Array.IndexOf(prefix, "merge.conflictStyle=zdiff3");
        Assert.True(at > 0, "the prefix must set merge.conflictStyle=zdiff3");
        Assert.Equal("-c", prefix[at - 1]);
    }

    [Fact]
    public async Task Merge_run_through_cli_service_writes_base_section()
    {
        await using var host = new MiniHost();
        using var repo = DivergedRepo();

        var text = await MergeThroughCliAsync(host, repo);

        Assert.Contains("|||||||", text);
        Assert.Contains("shared", text);
    }

    [Fact]
    public async Task User_conflict_style_merge_is_overridden()
    {
        await using var host = new MiniHost();
        using var repo = DivergedRepo();
        repo.Git("config", "merge.conflictStyle", "merge");

        var text = await MergeThroughCliAsync(host, repo);

        Assert.Contains("|||||||", text);
        Assert.Contains("shared", text);
    }
}
