using System.IO;

namespace Historia.Services.Diff;

/// <summary>
/// Maps a file path to a syntax-highlighting language id (§6.10.1). Highlighting itself is a
/// JS highlighter, incremental and visible-lines-only (§17 Q2); this only picks the grammar.
/// </summary>
public static class LanguageMap
{
    private static readonly IReadOnlyDictionary<string, string> ByExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp",
        [".razor"] = "razor",
        [".xaml"] = "xml",
        [".xml"] = "xml",
        [".json"] = "json",
        [".js"] = "javascript",
        [".ts"] = "typescript",
        [".tsx"] = "tsx",
        [".jsx"] = "jsx",
        [".css"] = "css",
        [".scss"] = "scss",
        [".html"] = "html",
        [".md"] = "markdown",
        [".py"] = "python",
        [".go"] = "go",
        [".rs"] = "rust",
        [".java"] = "java",
        [".kt"] = "kotlin",
        [".c"] = "c",
        [".h"] = "c",
        [".cpp"] = "cpp",
        [".hpp"] = "cpp",
        [".sh"] = "bash",
        [".ps1"] = "powershell",
        [".yml"] = "yaml",
        [".yaml"] = "yaml",
        [".toml"] = "toml",
        [".sql"] = "sql",
    };

    public static string? FromPath(string path)
    {
        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? null : ByExtension.GetValueOrDefault(ext);
    }
}
