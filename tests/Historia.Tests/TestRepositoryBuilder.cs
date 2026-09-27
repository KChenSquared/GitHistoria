namespace Historia.Tests;

/// <summary>
/// Generates fixture repositories programmatically with the real <c>git</c> CLI
/// or high-speed template copying and fast-import streams. Each instance owns a
/// temp directory that is deleted on <see cref="Dispose"/>.
/// </summary>
public sealed class TestRepositoryBuilder : IDisposable
{
    private static readonly ConcurrentDictionary<string, Lazy<string>> Templates = new();
    private static readonly ConcurrentBag<string> CreatedTemplates = new();
    private static readonly Lazy<bool> UnloadHook = new(() =>
    {
        AssemblyLoadContext.Default.Unloading += _ => CleanupTemplates();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupTemplates();
        return true;
    });

    private static void CleanupTemplates()
    {
        foreach (var dir in CreatedTemplates)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    ClearReadOnly(new DirectoryInfo(dir));
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup on shutdown
            }
        }
    }

    private static void ClearReadOnly(DirectoryInfo dir)
    {
        try
        {
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if ((file.Attributes & FileAttributes.ReadOnly) != 0)
                    file.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
        catch { }
    }

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
        _ = UnloadHook.Value;
        var templateDir = Templates.GetOrAdd(key, k => new Lazy<string>(() =>
        {
            var tplDir = System.IO.Path.Combine(TestScratch.Root, "historia-tpl-" + Sanitize(k) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tplDir);
            CreatedTemplates.Add(tplDir);
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
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("fast-import");
        psi.ArgumentList.Add("--quiet");
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        using (var writer = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)))
        {
            writer.Write(streamText);
        }

        p.WaitForExit(30_000);
        Task.WaitAll(stdoutTask, stderrTask);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git fast-import failed ({p.ExitCode}): {stderrTask.Result}");
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

    public string Git(IReadOnlyDictionary<string, string>? extraEnvironment, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (extraEnvironment is not null)
        {
            foreach (var (k, v) in extraEnvironment)
                psi.Environment[k] = v;
        }

        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit(30_000);
        Task.WaitAll(stdoutTask, stderrTask);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderrTask.Result}");
        return stdoutTask.Result;
    }

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
