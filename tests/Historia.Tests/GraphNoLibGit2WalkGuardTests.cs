namespace Historia.Tests;

/// <summary>
/// Regression guard: the commit graph is walked only by <c>git log</c>, and reads refs only from
/// the <c>for-each-ref</c> ref snapshot. A libgit2 revwalk is hundreds of times slower to its
/// first row and gigabytes heavier on a large repo; a libgit2 ref enumeration holds the repo gate
/// ~6 s at chromium's 39k refs, on every walk step. Either would regress without any error — so
/// no file under <c>Historia.Core/Services/Graph/</c> may use LibGit2Sharp's revwalk API or
/// enumerate refs or tags through it. The whole folder is scanned, not a file list, so a new file
/// can't slip past.
/// </summary>
[Trait("tier", "unit")]
public class GraphNoLibGit2WalkGuardTests
{
    private static readonly string[] Forbidden =
        ["CommitSortStrategies", "Commits.QueryBy(", "CommitFilter", "repo.Refs", ".Refs.ToList(", "repo.Tags"];

    [Fact]
    public void No_graph_source_file_uses_a_libgit2_revwalk()
    {
        var graphDir = Path.Combine(RepoRoot(), "Historia.Core", "Services", "Graph");
        var files = Directory.GetFiles(graphDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var hits = (
            from file in files
            let lines = File.ReadAllLines(file)
            from i in Enumerable.Range(0, lines.Length)
            from token in Forbidden
            where lines[i].Contains(token, StringComparison.Ordinal)
            select $"{Path.GetRelativePath(graphDir, file)}:{i + 1}: {token}").ToList();

        Assert.True(hits.Count == 0, "libgit2 revwalk or ref enumeration in the graph:\n" + string.Join('\n', hits));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Historia.Core", "Services", "Graph")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
