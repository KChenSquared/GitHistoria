namespace Historia.Services.Git;

/// <inheritdoc cref="IGitExecutableLocator"/>
public sealed partial class GitExecutableLocator : IGitExecutableLocator
{
    private readonly object _sync = new();
    private GitExecutableInfo _current = GitExecutableInfo.NotFound;

    public GitExecutableInfo Current
    {
        get { lock (_sync) return _current; }
    }

    public async Task<GitExecutableInfo> ResolveAsync(string? userOverridePath, CancellationToken ct = default)
    {
        var path = await Task.Run(() => Discover(userOverridePath), ct).ConfigureAwait(false);
        GitExecutableInfo info;

        if (path is null)
        {
            info = GitExecutableInfo.NotFound;
        }
        else
        {
            var version = await Task.Run(() => CaptureVersion(path), ct).ConfigureAwait(false);
            info = new GitExecutableInfo { Found = true, Path = path, Version = version };
        }

        lock (_sync)
            _current = info;
        return info;
    }

    private static string? Discover(string? userOverridePath)
    {
        // 1. Explicit user override.
        if (!string.IsNullOrWhiteSpace(userOverridePath) && File.Exists(userOverridePath))
            return userOverridePath;

        // 2. PATH.
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, "git.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH segment; skip.
            }
        }

        // 3. Well-known install locations.
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(root))
                continue;
            var candidate = Path.Combine(root, "Git", "cmd", "git.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        // 4. Git for Windows registry key.
        if (OperatingSystem.IsWindows())
            return DiscoverFromWindowsRegistry();

        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? DiscoverFromWindowsRegistry()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(@"SOFTWARE\GitForWindows");
                if (key?.GetValue("InstallPath") is string installPath)
                {
                    var candidate = Path.Combine(installPath, "cmd", "git.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Registry unavailable; fall through.
            }
        }

        return null;
    }

    private static Version? CaptureVersion(string gitPath)
    {
        try
        {
            var psi = new ProcessStartInfo(gitPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--version");

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            // "git version 2.51.0.windows.2"
            var match = GitVersionRegex().Match(output);
            if (!match.Success)
                return null;

            var major = int.Parse(match.Groups["major"].Value);
            var minor = int.Parse(match.Groups["minor"].Value);
            var patch = match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0;
            return new Version(major, minor, patch);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"git version (?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex GitVersionRegex();
}
