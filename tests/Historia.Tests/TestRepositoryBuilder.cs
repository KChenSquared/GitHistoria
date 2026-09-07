using System.Diagnostics;

namespace Historia.Tests;

/// <summary>
/// Generates fixture repositories programmatically with the real <c>git</c> CLI (§16.1). Each
/// instance owns a temp directory that is deleted on <see cref="Dispose"/>.
/// </summary>
public sealed class TestRepositoryBuilder : IDisposable
{
    public string Path { get; }

    private TestRepositoryBuilder(string path) => Path = path;

    public static TestRepositoryBuilder Create()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "historia-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var builder = new TestRepositoryBuilder(dir);
        builder.Git("init", "-b", "main");
        builder.Git("config", "user.name", "Test User");
        builder.Git("config", "user.email", "test@example.com");
        builder.Git("config", "commit.gpgsign", "false");
        return builder;
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
        return Git("rev-parse", "HEAD").Trim();
    }

    public TestRepositoryBuilder Checkout(string branch, bool create = false)
    {
        if (create) Git("checkout", "-b", branch);
        else Git("checkout", branch);
        return this;
    }

    public string Git(params string[] args)
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

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr}");
        return stdout;
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
