using System.Text.RegularExpressions;
using Xunit;

namespace Historia.Tests;

/// <summary>
/// Rule 0 guard (constitution §0.6, §V2-10.4, §V3-18.2 Q8): Historia must start and render from
/// what ships in the package, so no UI asset may reference an <c>http(s)</c> URL. This scans
/// every text file under <c>wwwroot/</c> plus every <c>.razor</c>, <c>.css</c> <b>and .cs</b>
/// in the tree and fails on any outbound URL outside a comment. Allowed matches are
/// non-dereferenced XML namespace identifiers (XAML and SVG) and an explicit allowlist of
/// consented URLs — the WebView2 bootstrapper fwlink approved in §V2-10.3.
/// </summary>
public class OfflineAssetTests
{
    private static readonly string[] AllowedNamespacePrefixes =
    {
        "http://schemas.microsoft.com/",
        "http://www.w3.org/",
    };

    /// <summary>Consented URLs (§V3-18.2 Q8) — a real allowlist, not a blind spot.</summary>
    private static readonly string[] ConsentedUrls =
    {
        "https://go.microsoft.com/fwlink/p/?LinkId=2124703",       // WebView2 bootstrapper (§V2-10.3)
        "https://developer.microsoft.com/microsoft-edge/webview2/", // its manual-install fallback
        "https://github.com/owner/repo.git",                        // clone-dialog input watermark (not fetched)
        "https://github.com/settings/tokens/new?scopes=repo&description=Historia", // v8 M11 "Sign in with GitHub" — user-initiated browser open
    };

    private static readonly string[] BinaryExtensions =
    {
        ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp", ".woff", ".woff2", ".ttf", ".otf", ".eot",
    };

    // Stops at { so an interpolated URL-building template (e.g. $"https://{host}/{path}") — code
    // that constructs a URL, not a package asset fetch — is not a match.
    private static readonly Regex UrlPattern =
        new(@"https?://[^\s""'(){}<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void NoOutboundUrlsInShippedAssets()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var file in ScannedFiles(root))
        {
            var text = StripComments(File.ReadAllText(file), Path.GetExtension(file));
            foreach (Match m in UrlPattern.Matches(text))
            {
                var url = m.Value.TrimEnd('.', ',', ';', ')');
                if (AllowedNamespacePrefixes.Any(p => url.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (ConsentedUrls.Any(u => string.Equals(u, url, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                offenders.Add($"{rel}: {url}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Outbound http(s) URL(s) in shipped assets — Rule 0 violation:\n  " +
            string.Join("\n  ", offenders));
    }

    private static IEnumerable<string> ScannedFiles(string root)
    {
        var wwwroot = Path.Combine(root, "wwwroot");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(wwwroot))
        {
            foreach (var f in Directory.EnumerateFiles(wwwroot, "*", SearchOption.AllDirectories))
                if (!BinaryExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && seen.Add(f))
                    yield return f;
        }

        foreach (var pattern in new[] { "*.razor", "*.css", "*.cs" })
        {
            foreach (var f in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                if (IsUnderBuildDir(root, f))
                    continue;
                var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                if (rel.Contains("/tests/") || rel.StartsWith("tests/", StringComparison.Ordinal))
                    continue; // the test tree names URLs on purpose
                if (seen.Add(f))
                    yield return f;
            }
        }
    }

    private static bool IsUnderBuildDir(string root, string file)
    {
        var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
        return rel.Contains("/bin/") || rel.Contains("/obj/") ||
               rel.StartsWith("bin/") || rel.StartsWith("obj/");
    }

    /// <summary>Removes comments so a URL parked in a comment does not fail the scan.</summary>
    private static string StripComments(string text, string ext)
    {
        // CSS/JS block comments.
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        // HTML / Razor comments.
        text = Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"@\*.*?\*@", " ", RegexOptions.Singleline);
        // Line comments for .cs / .js — but not the // inside a :// scheme.
        if (ext.Equals(".js", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            text = Regex.Replace(text, @"(?<!:)//[^\n]*", " ");
        return text;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
