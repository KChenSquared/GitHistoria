namespace Historia.Tests;

/// <summary>
/// Generates fixture repositories programmatically with the real <c>git</c> CLI
/// or high-speed template copying and fast-import streams. Each instance owns a
/// temp directory that is deleted on <see cref="Dispose"/>.
/// </summary>
public sealed class TestRepositoryBuilder : IDisposable
{
    // Templates live under the run's scratch folder, which TestScratch deletes on exit.
    private static readonly ConcurrentDictionary<string, Lazy<string>> Templates = new();

    public string Path { get; }

    private TestRepositoryBuilder(string path) => Path = path;

    public static TestRepositoryBuilder Create()
    {
        var dir = TestScratch.NewDirectory("historia-test-");
        var builder = new TestRepositoryBuilder(dir);
        builder.InitRepo();
        return builder;
    }

    /// <summary>
    /// Obtains an independent, freshly copied repository initialized from a process-cached template.
    /// The template is built once via <paramref name="build"/>; subsequent calls copy the template directory.
    /// </summary>
    public static TestRepositoryBuilder FromTemplate(string key, Action<TestRepositoryBuilder> build)
    {
        var templateDir = Templates.GetOrAdd(key, k => new Lazy<string>(() =>
        {
            var tplDir = TestScratch.NewDirectory("historia-tpl-" + Sanitize(k) + "-");
            var builder = new TestRepositoryBuilder(tplDir);
            builder.InitRepo();
            build(builder);
            return tplDir;
        })).Value;

        var testDir = TestScratch.NewDirectory("historia-test-");
        CopyDirectory(templateDir, testDir);
        return new TestRepositoryBuilder(testDir);
    }

    private void InitRepo()
    {
        Git("init", "-b", "main");
        Git("config", "user.name", "Test User");
        Git("config", "user.email", "test@example.com");
        Git("config", "commit.gpgsign", "false");
    }

    /// <summary>
    /// Generates a sequence of <paramref name="commits"/> commits using a single <c>git fast-import</c> stream,
    /// followed by <c>git reset --hard</c>. Produces deterministic timestamps and identical SHAs as equivalent
    /// sequential <see cref="CommitAt"/> operations.
    /// </summary>
    public TestRepositoryBuilder LinearHistory(
        int commits,
        string fileName = "f.txt",
        Func<int, string>? contentFactory = null,
        Func<int, string>? messageFactory = null,
        DateTimeOffset? baseDate = null)
    {
        if (commits <= 0) return this;

        contentFactory ??= i => $"{i}\n";
        messageFactory ??= i => $"commit {i:D2}";
        var baseTime = baseDate ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var sb = new StringBuilder();
        for (var i = 0; i < commits; i++)
        {
            var when = baseTime.AddMinutes(i);
            var epoch = when.ToUnixTimeSeconds();
            var offsetMinutes = (int)when.Offset.TotalMinutes;
            var tz = $"{(offsetMinutes >= 0 ? "+" : "-")}{Math.Abs(offsetMinutes) / 60:D2}{Math.Abs(offsetMinutes) % 60:D2}";

            var msg = messageFactory(i);
            if (!msg.EndsWith('\n')) msg += "\n";
            var msgBytes = Encoding.UTF8.GetByteCount(msg);

            var content = contentFactory(i);
            var contentBytes = Encoding.UTF8.GetByteCount(content);

            sb.Append("commit refs/heads/main\n");
            sb.Append($"mark :{i + 1}\n");
            sb.Append($"author Test User <test@example.com> {epoch} {tz}\n");
            sb.Append($"committer Test User <test@example.com> {epoch} {tz}\n");
            sb.Append($"data {msgBytes}\n");
            sb.Append(msg);
            sb.Append($"M 100644 inline {fileName}\n");
            sb.Append($"data {contentBytes}\n");
            sb.Append(content);
            if (!content.EndsWith('\n')) sb.Append('\n');
            sb.Append('\n');
        }

        FastImport(sb.ToString());
        Git("reset", "--hard");
        return this;
    }

    public void FastImport(string streamText)
    {
        var result = TestGit.Run(Path, ["fast-import", "--quiet"], stdin: streamText, testIdentity: false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git fast-import failed ({result.ExitCode}): {result.Stderr}");
    }

    public TestRepositoryBuilder WriteFile(string relativePath, string content)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    public TestRepositoryBuilder Stage(string spec = ".") { Git("add", spec); return this; }

    public string Commit(string message)
    {
        Git("commit", "-m", message, "--no-gpg-sign");
        return ResolveHeadSha();
    }

    public string CommitAt(string message, DateTimeOffset when) =>
        CommitAt(message, when, when);

    public string CommitAt(string message, DateTimeOffset authorWhen, DateTimeOffset commitWhen)
    {
        var env = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_DATE"] = authorWhen.ToString("O"),
            ["GIT_COMMITTER_DATE"] = commitWhen.ToString("O"),
        };
        Git(env, "commit", "-m", message, "--no-gpg-sign");
        return ResolveHeadSha();
    }

    /// <summary>Commits the index with a forged SSH <c>gpgsig</c> header and turns on
    /// <c>log.showSignature</c>. Verification fails without any signing tool, and git then
    /// prints "No signature" on stdout ahead of the commit's formatted record.</summary>
    public string CommitWithForgedSignature(string message)
    {
        var tree = Git("write-tree").Trim();
        var parent = Git("rev-parse", "--verify", "-q", "HEAD").Trim();
        var body = new StringBuilder()
            .Append("tree ").Append(tree).Append('\n')
            .Append("parent ").Append(parent).Append('\n')
            .Append("author Test User <test@example.com> 1700000000 +0000\n")
            .Append("committer Test User <test@example.com> 1700000000 +0000\n")
            .Append("gpgsig -----BEGIN SSH SIGNATURE-----\n U1NIU0lH\n -----END SSH SIGNATURE-----\n")
            .Append('\n').Append(message).Append('\n')
            .ToString();
        var objectFile = System.IO.Path.Combine(Path, ".git", "forged-commit.txt");
        File.WriteAllText(objectFile, body);
        var sha = Git("hash-object", "-t", "commit", "-w", objectFile).Trim();
        File.Delete(objectFile);
        Git("reset", "--soft", sha);
        Git("config", "gpg.format", "ssh");
        Git("config", "log.showSignature", "true");
        return sha;
    }

    public string HeadSha => ResolveHeadSha();

    private string ResolveHeadSha()
    {
        try
        {
            var gitDir = System.IO.Path.Combine(Path, ".git");
            var headFile = System.IO.Path.Combine(gitDir, "HEAD");
            if (File.Exists(headFile))
            {
                var headContent = File.ReadAllText(headFile).Trim();
                if (headContent.StartsWith("ref: ", StringComparison.Ordinal))
                {
                    var refPath = headContent.Substring(5).Trim().Replace('/', System.IO.Path.DirectorySeparatorChar);
                    var refFile = System.IO.Path.Combine(gitDir, refPath);
                    if (File.Exists(refFile))
                    {
                        var sha = File.ReadAllText(refFile).Trim();
                        if (sha.Length == 40)
                            return sha;
                    }
                }
                else if (headContent.Length == 40)
                {
                    return headContent;
                }
            }
        }
        catch
        {
            // Fall back to rev-parse.
        }

        return Git("rev-parse", "HEAD").Trim();
    }

    public TestRepositoryBuilder Checkout(string branch, bool create = false)
    {
        if (create) Git("checkout", "-b", branch);
        else Git("checkout", branch);
        return this;
    }

    public string Git(params string[] args) => Git(extraEnvironment: null, args);

    public string Git(IReadOnlyDictionary<string, string>? extraEnvironment, params string[] args) =>
        TestGit.RunChecked(Path, args, env: extraEnvironment, testIdentity: false);

    private static string Sanitize(string key) =>
        string.Concat(key.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_'));

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(System.IO.Path.Combine(targetDir, rel));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(sourceDir, file);
            var dest = System.IO.Path.Combine(targetDir, rel);
            File.Copy(file, dest, overwrite: true);
            var attrs = File.GetAttributes(dest);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(dest, attrs & ~FileAttributes.ReadOnly);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(Path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
