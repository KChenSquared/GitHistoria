namespace Historia.Tests;

/// <summary>What a <see cref="TestGit"/> command wrote and how it ended.</summary>
public sealed record TestGitResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Runs <c>git</c> for test fixtures. Every redirected stream is read while the process runs, so a
/// command that writes more than a pipe buffer (one warning per file under
/// <c>core.autocrlf=true</c>, say) cannot block on a full pipe.
/// </summary>
public static class TestGit
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Runs git in <paramref name="dir"/> and returns its exit code and output.
    /// <paramref name="testIdentity"/> sets the author and committer to the test identity.</summary>
    public static TestGitResult Run(
        string dir,
        IEnumerable<string> args,
        string? stdin = null,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        bool testIdentity = true)
    {
        var argList = args.ToList();
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in argList)
            psi.ArgumentList.Add(a);

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (testIdentity)
        {
            psi.Environment["GIT_AUTHOR_NAME"] = "Test";
            psi.Environment["GIT_AUTHOR_EMAIL"] = "test@example.com";
            psi.Environment["GIT_COMMITTER_NAME"] = "Test";
            psi.Environment["GIT_COMMITTER_EMAIL"] = "test@example.com";
        }

        if (env is not null)
        {
            foreach (var (k, v) in env)
                psi.Environment[k] = v;
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();

        if (stdin is not null)
        {
            try
            {
                using var writer = new StreamWriter(p.StandardInput.BaseStream, new System.Text.UTF8Encoding(false));
                writer.Write(stdin);
            }
            catch (IOException)
            {
                // git exited before reading all of its input; its exit code says why.
            }
        }

        var limit = timeout ?? DefaultTimeout;
        if (!p.WaitForExit(limit))
        {
            try { p.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw new TimeoutException($"git {string.Join(' ', argList)} did not finish within {limit.TotalSeconds:0} s.");
        }

        Task.WaitAll(stdout, stderr);
        return new TestGitResult(p.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>Runs git as <see cref="Run"/> does and returns its output; a non-zero exit throws
    /// with the command and its error text.</summary>
    public static string RunChecked(
        string dir,
        IEnumerable<string> args,
        string? stdin = null,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        bool testIdentity = true)
    {
        var argList = args.ToList();
        var result = Run(dir, argList, stdin, env, timeout, testIdentity);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', argList)} failed ({result.ExitCode}): {result.Stderr}");
        return result.Stdout;
    }
}
