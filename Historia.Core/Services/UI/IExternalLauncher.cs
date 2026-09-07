using System.Diagnostics;

namespace Historia.Services.UI;

/// <summary>
/// Opens a URL in the system default browser (§I-5.4) and reveals paths in the OS file
/// manager (§15 / v8 M6). User-initiated only; every method is best-effort and never throws.
/// </summary>
public interface IExternalLauncher
{
    void OpenUrl(string url);

    /// <summary>Open the OS file manager with <paramref name="path"/> selected (a file) or
    /// showing its contents (a directory). Falls back to the parent directory when the path is
    /// gone.</summary>
    void RevealInFileManager(string path);

    /// <summary>Open the OS file manager showing <paramref name="path"/> (a directory, or the
    /// parent of a file).</summary>
    void OpenFolder(string path);
}

public sealed class ExternalLauncher : IExternalLauncher
{
    public void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;
        // Only ever hand the shell an http/https URL — never file:, never a raw command.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // No browser / blocked — nothing actionable to surface for a link click.
        }
    }

    public void RevealInFileManager(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var full = SafeFullPath(path);
        if (full is null)
            return;

        var exists = File.Exists(full) || Directory.Exists(full);
        if (!exists)
        {
            // The file was deleted since it was listed — reveal its folder instead.
            OpenFolder(full);
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // A trailing separator would make the closing \" an escaped quote to the Win32
                // command-line parser (explorer.exe "…\" → unterminated). Trim it; if that leaves
                // a bare drive ("D:") there is nothing to select — just open the folder.
                var target = TrimTrailingSeparators(full);
                if (target.Length == 2 && target[1] == ':')
                    OpenFolder(full);
                else
                    Start("explorer.exe", $"/select,\"{target}\"");
            }
            else if (OperatingSystem.IsMacOS())
                Start("open", $"-R \"{full}\"");
            else if (!TryLinuxShowItem(full))
                OpenFolder(Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? full);
        }
        catch { /* best-effort */ }
    }

    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var full = SafeFullPath(path);
        if (full is null)
            return;

        var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            dir = Path.GetDirectoryName(dir);
        if (string.IsNullOrEmpty(dir))
            return;

        try
        {
            if (OperatingSystem.IsWindows())
                // Hand the path straight to the shell — no command-line quote parsing, so a
                // trailing backslash (drive roots, "D:\Repo\") can't break the argument.
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Start("open", $"\"{dir}\"");
            else
                Start("xdg-open", $"\"{dir}\"");
        }
        catch { /* best-effort */ }
    }

    private static string TrimTrailingSeparators(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? path : trimmed;
    }

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    private static void Start(string file, string args) =>
        Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false });

    /// <summary>Ask a freedesktop file manager to highlight the item; returns false if the
    /// D-Bus call could not be launched (caller then falls back to <c>xdg-open</c>).</summary>
    private static bool TryLinuxShowItem(string full)
    {
        try
        {
            var uri = new Uri(full).AbsoluteUri;
            Start("dbus-send",
                "--session --print-reply --dest=org.freedesktop.FileManager1 " +
                "--type=method_call /org/freedesktop/FileManager1 " +
                $"org.freedesktop.FileManager1.ShowItems array:string:\"{uri}\" string:\"\"");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
