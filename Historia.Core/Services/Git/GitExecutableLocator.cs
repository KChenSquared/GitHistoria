namespace Historia.Services.Git;

/// <inheritdoc cref="IGitExecutableLocator"/>
public sealed partial class GitExecutableLocator : IGitExecutableLocator
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource<GitExecutableInfo> _resolvedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GitExecutableInfo _current = GitExecutableInfo.NotFound;

    public GitExecutableInfo Current
    {
        get { lock (_sync) return _current; }
    }

    public Task<GitExecutableInfo> WhenResolvedAsync(CancellationToken ct = default)
    {
        return ct.CanBeCanceled ? WaitWithCancellationAsync(ct) : _resolvedTcs.Task;

        async Task<GitExecutableInfo> WaitWithCancellationAsync(CancellationToken token)
        {
            return await _resolvedTcs.Task.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public async Task<GitExecutableInfo> ResolveAsync(string? userOverridePath, CancellationToken ct = default)
    {
        try
        {
            return await ResolveCoreAsync(userOverridePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Every CLI call awaits the first resolution; a failed one must still release them
            // (they then fail classified as "git not found") rather than wait forever.
            _resolvedTcs.TrySetResult(GitExecutableInfo.NotFound with { NotFoundReason = $"Git resolution failed: {ex.Message}" });
            throw;
        }
    }

    private async Task<GitExecutableInfo> ResolveCoreAsync(string? userOverridePath, CancellationToken ct)
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
            if (version is null || version < GitExecutableInfo.MinimumVersion)
            {
                var reason = version is null
                    ? $"Git at '{path}' did not report a valid version."
                    : $"Git {version} was found at '{path}', but Git {GitExecutableInfo.MinimumVersion} or newer is required.";
                info = new GitExecutableInfo
                {
                    Found = false,
                    Path = path,
                    Version = version,
                    NotFoundReason = reason,
                };
            }
            else
            {
                info = new GitExecutableInfo { Found = true, Path = path, Version = version };
            }
        }

        lock (_sync)
            _current = info;

        _resolvedTcs.TrySetResult(info);
        return info;
    }

    private static string? Discover(string? userOverridePath)
    {
        // 1. Explicit user override.
        if (!string.IsNullOrWhiteSpace(userOverridePath) && File.Exists(userOverridePath))
            return userOverridePath;

        // 2. PATH.
        var onPath = FindOnPath(Environment.GetEnvironmentVariable("PATH"));
        if (onPath is not null)
            return onPath;

        // 3. Well-known install locations.
        foreach (var candidate in WellKnownLocations())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // 4. Git for Windows registry key.
        if (OperatingSystem.IsWindows())
            return DiscoverFromWindowsRegistry();

        return null;
    }

    /// <summary>The git binary's file name: <c>git.exe</c> on Windows, <c>git</c> elsewhere.</summary>
    internal static string ExecutableName => OperatingSystem.IsWindows() ? "git.exe" : "git";

    /// <summary>The first <see cref="ExecutableName"/> in the directories of a PATH-style value.</summary>
    internal static string? FindOnPath(string? pathVar)
    {
        foreach (var dir in (pathVar ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, ExecutableName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH segment; skip.
            }
        }

        return null;
    }

    private static IEnumerable<string> WellKnownLocations()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                if (!string.IsNullOrEmpty(root))
                    yield return Path.Combine(root, "Git", "cmd", "git.exe");
            }

            yield break;
        }

        // A desktop launch on macOS does not inherit the shell's PATH, so Homebrew and MacPorts
        // installs are not on it.
        yield return "/usr/bin/git";
        yield return "/usr/local/bin/git";
        yield return "/opt/homebrew/bin/git";
        yield return "/opt/local/bin/git";
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
            return ParseVersion(output);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static Version? ParseVersion(string output)
    {
        // "git version 2.51.0.windows.2"
        var match = GitVersionRegex().Match(output);
        if (!match.Success)
            return null;

        var major = int.Parse(match.Groups["major"].Value);
        var minor = int.Parse(match.Groups["minor"].Value);
        var patch = match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0;
        return new Version(major, minor, patch);
    }

    [GeneratedRegex(@"git version (?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex GitVersionRegex();
}
