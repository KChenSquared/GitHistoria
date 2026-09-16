namespace Historia.ScenarioLab.Services;

public sealed record ScriptRunResult(int ExitCode, TimeSpan Duration, bool WasStopped);

/// <summary>
/// Executes bash scenario scripts inside Git Bash with configured SCENARIO_* environment variables
/// and streams output live.
/// </summary>
public sealed class ScriptRunner
{
    private Process? _currentProcess;
    private readonly object _lock = new();

    public static string? FindBash()
    {
        if (!OperatingSystem.IsWindows())
        {
            if (File.Exists("/bin/bash")) return "/bin/bash";
            if (File.Exists("/usr/bin/bash")) return "/usr/bin/bash";
            return null;
        }

        // 1. Check PATH for git.exe -> <Git>\bin\bash.exe
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var parts = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            try
            {
                var gitCandidate = Path.Combine(part, "git.exe");
                if (File.Exists(gitCandidate))
                {
                    var parent = Directory.GetParent(part)?.FullName;
                    if (!string.IsNullOrEmpty(parent))
                    {
                        var bashCandidate = Path.Combine(parent, "bin", "bash.exe");
                        if (File.Exists(bashCandidate))
                            return bashCandidate;

                        var usrBinBash = Path.Combine(parent, "usr", "bin", "bash.exe");
                        if (File.Exists(usrBinBash))
                            return usrBinBash;
                    }
                }
            }
            catch { }
        }

        // 2. Check standard Program Files locations
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var stdBash = Path.Combine(pf, "Git", "bin", "bash.exe");
        if (File.Exists(stdBash))
            return stdBash;

        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var stdBashX86 = Path.Combine(pfx86, "Git", "bin", "bash.exe");
        if (File.Exists(stdBashX86))
            return stdBashX86;

        return null;
    }

    public static string ToForwardSlash(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    public async Task<ScriptRunResult> RunAsync(
        string scriptPath,
        LabSettings settings,
        Action<string> onLine,
        CancellationToken ct)
    {
        var bash = FindBash();
        if (string.IsNullOrEmpty(bash))
        {
            onLine("[ERROR] Could not locate bash.exe. Please ensure Git for Windows is installed.");
            return new ScriptRunResult(-1, TimeSpan.Zero, false);
        }

        var tempRunDir = Path.Combine(Path.GetTempPath(), "Historia.ScenarioLab", $"run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRunDir);

        var wasStopped = false;
        var sw = Stopwatch.StartNew();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = bash,
                WorkingDirectory = tempRunDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            psi.ArgumentList.Add("--noprofile");
            psi.ArgumentList.Add("--norc");
            psi.ArgumentList.Add(ToForwardSlash(scriptPath));

            psi.Environment["SCENARIO_LOCAL1"] = ToForwardSlash(settings.Local1);
            psi.Environment["SCENARIO_LOCAL2"] = ToForwardSlash(settings.Local2);
            psi.Environment["SCENARIO_REMOTES"] = ToForwardSlash(settings.Remotes);
            psi.Environment["SCENARIO_TEMPLATES"] = ToForwardSlash(LabPaths.Templates);
            psi.Environment["SCENARIO_LIB"] = ToForwardSlash(LabPaths.LibScript);
            psi.Environment["SCENARIO_ROOT"] = ToForwardSlash(LabPaths.Root);
            psi.Environment["SCENARIO_NAME"] = Path.GetFileNameWithoutExtension(scriptPath);

            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["GIT_EDITOR"] = "true";
            psi.Environment["GIT_SEQUENCE_EDITOR"] = "true";
            psi.Environment["GIT_PAGER"] = "cat";
            psi.Environment["PAGER"] = "cat";

            var process = new Process { StartInfo = psi };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    onLine(e.Data);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    onLine(e.Data);
            };

            lock (_lock)
            {
                _currentProcess = process;
            }

            using var reg = ct.Register(() =>
            {
                wasStopped = true;
                Stop();
            });

            process.Start();
            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(CancellationToken.None);
            sw.Stop();

            return new ScriptRunResult(process.ExitCode, sw.Elapsed, wasStopped);
        }
        finally
        {
            lock (_lock)
            {
                _currentProcess = null;
            }

            try
            {
                if (Directory.Exists(tempRunDir))
                    Directory.Delete(tempRunDir, recursive: true);
            }
            catch { }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_currentProcess is not null && !_currentProcess.HasExited)
            {
                try
                {
                    _currentProcess.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
            }
        }
    }
}
