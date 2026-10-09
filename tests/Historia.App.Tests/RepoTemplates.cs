namespace HistoriaApp.Tests;

/// <summary>
/// Test repositories built once per test run and copied for each test, so a fixture costs a folder
/// copy instead of a run of git processes. Built repositories live in the run's state directory,
/// which is deleted when the run ends.
/// </summary>
internal static class RepoTemplates
{
    private static readonly ConcurrentDictionary<string, Lazy<string>> Built = new();

    /// <summary>A repository on <c>main</c> with one commit adding <c>f.txt</c> ("init").</summary>
    public static void Initial(string targetDir) => CopyTo("initial", targetDir, dir =>
    {
        Git(dir, "init", "-b", "main");
        Git(dir, "config", "user.name", "Test");
        Git(dir, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(dir, "f.txt"), "init\n");
        Git(dir, "add", ".");
        Git(dir, "commit", "-m", "initial commit", "--no-gpg-sign");
    });

    /// <summary><see cref="Initial"/>, then different edits to <c>f.txt</c> on <c>branch1</c> and on
    /// <c>main</c>, with <c>main</c>'s merge of <c>branch1</c> stopped on the conflict.</summary>
    public static void MergeConflict(string targetDir) => CopyTo("merge-conflict", targetDir, dir =>
    {
        Initial(dir);
        CreateMergeConflict(dir);
    });

    /// <summary>The git commands behind <see cref="MergeConflict"/>, for a repository a test already
    /// has open.</summary>
    public static void CreateMergeConflict(string dir)
    {
        Git(dir, "checkout", "-b", "branch1");
        File.WriteAllText(Path.Combine(dir, "f.txt"), "branch1 line\n");
        Git(dir, "commit", "-am", "branch1 commit", "--no-gpg-sign");

        Git(dir, "checkout", "main");
        File.WriteAllText(Path.Combine(dir, "f.txt"), "main line\n");
        Git(dir, "commit", "-am", "main commit", "--no-gpg-sign");

        Git(dir, "merge", "branch1");
    }

    /// <summary>Copies the template named <paramref name="key"/> into <paramref name="targetDir"/>,
    /// building it first with <paramref name="build"/> if this run hasn't; <paramref name="build"/>
    /// gets an empty directory. Copies are writable, so they delete cleanly.</summary>
    public static void CopyTo(string key, string targetDir, Action<string> build)
    {
        var source = Built.GetOrAdd(key, k => new Lazy<string>(() =>
        {
            var dir = Path.Combine(TestStateIsolation.StateDirectory, "repo-templates", k);
            Directory.CreateDirectory(dir);
            build(dir);
            return dir;
        })).Value;

        Directory.CreateDirectory(targetDir);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(targetDir, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(targetDir, Path.GetRelativePath(source, file));
            File.Copy(file, dest, overwrite: true);
            File.SetAttributes(dest, File.GetAttributes(dest) & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>Deletes <paramref name="dir"/>, including the read-only objects git writes. Best effort.</summary>
    public static void Delete(string dir) => TestScratch.Delete(dir);

    /// <summary>Runs git in <paramref name="dir"/> as the test identity and waits for it.</summary>
    public static void Git(string dir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_AUTHOR_NAME"] = "Test";
        psi.Environment["GIT_AUTHOR_EMAIL"] = "test@example.com";
        psi.Environment["GIT_COMMITTER_NAME"] = "Test";
        psi.Environment["GIT_COMMITTER_EMAIL"] = "test@example.com";
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }
}
