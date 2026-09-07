namespace Historia.Services.Diff;

public enum TokenKind { Plain, Keyword, String, Comment, Number, Punct }

/// <summary>One coloured span of a source line: <c>[Start, Start+Length)</c>.</summary>
public readonly record struct SyntaxToken(int Start, int Length, TokenKind Kind);

/// <summary>
/// A small, dependency-free, line-local tokenizer for the diff viewer (§V2-17.4, D12: bundled,
/// no CDN). It is deliberately approximate — it does not carry block-comment state across lines
/// — because the diff viewer only ever renders single lines and highlighting must arrive
/// <i>after</i> first paint, never before it. Language comes from <c>FileDiff.LanguageId</c>.
/// </summary>
public static class SyntaxHighlighter
{
    private static readonly FrozenSet<string> CFamily = new[]
    {
        "abstract","as","async","await","base","bool","break","byte","case","catch","char","class","const",
        "continue","default","delegate","do","double","else","enum","export","extends","false","final","finally",
        "float","for","foreach","from","function","get","goto","if","implements","import","in","instanceof","int",
        "interface","internal","is","let","lock","long","namespace","new","null","object","operator","override",
        "package","params","private","protected","public","readonly","record","ref","return","sealed","set","short",
        "static","string","struct","super","switch","this","throw","throws","true","try","typeof","using","var",
        "virtual","void","volatile","while","yield",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> Python = new[]
    {
        "and","as","assert","async","await","break","class","continue","def","del","elif","else","except","False",
        "finally","for","from","global","if","import","in","is","lambda","None","nonlocal","not","or","pass",
        "raise","return","True","try","while","with","yield",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> None = FrozenSet<string>.Empty;

    public static IReadOnlyList<SyntaxToken> Tokenize(string line, string? languageId)
    {
        if (string.IsNullOrEmpty(line))
            return [];

        var (keywords, hashComment) = Profile(languageId);
        var tokens = new List<SyntaxToken>();
        var i = 0;

        while (i < line.Length)
        {
            var c = line[i];

            // Whitespace
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // Line comments
            if ((c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                || (hashComment && c == '#')
                || (c == '-' && i + 1 < line.Length && line[i + 1] == '-'))
            {
                tokens.Add(new SyntaxToken(i, line.Length - i, TokenKind.Comment));
                break;
            }

            // Block comment (line-local: to end of line if unterminated)
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                var end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var len = end < 0 ? line.Length - i : end + 2 - i;
                tokens.Add(new SyntaxToken(i, len, TokenKind.Comment));
                i += len;
                continue;
            }

            // Strings
            if (c is '"' or '\'' or '`')
            {
                var j = i + 1;
                while (j < line.Length && line[j] != c)
                    j += line[j] == '\\' ? 2 : 1;
                var len = Math.Min(line.Length, j + 1) - i;
                tokens.Add(new SyntaxToken(i, len, TokenKind.String));
                i += len;
                continue;
            }

            // Numbers
            if (char.IsDigit(c) || (c == '.' && i + 1 < line.Length && char.IsDigit(line[i + 1])))
            {
                var j = i;
                while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] is '.' or '_' or 'x' or 'X'))
                    j++;
                tokens.Add(new SyntaxToken(i, j - i, TokenKind.Number));
                i = j;
                continue;
            }

            // Identifiers / keywords
            if (char.IsLetter(c) || c == '_' || c == '$')
            {
                var j = i;
                while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] is '_' or '$'))
                    j++;
                var word = line[i..j];
                if (keywords.Contains(word))
                    tokens.Add(new SyntaxToken(i, j - i, TokenKind.Keyword));
                i = j;
                continue;
            }

            // Punctuation
            if (!char.IsLetterOrDigit(c))
                tokens.Add(new SyntaxToken(i, 1, TokenKind.Punct));
            i++;
        }

        return tokens;
    }

    private static (FrozenSet<string> Keywords, bool HashComment) Profile(string? languageId) =>
        (languageId?.ToLowerInvariant()) switch
        {
            "python" or "py" => (Python, true),
            "ruby" or "rb" or "shell" or "bash" or "sh" or "yaml" or "yml" or "toml" => (None, true),
            null or "" or "text" or "plaintext" or "markdown" or "md" => (None, false),
            _ => (CFamily, false),
        };
}
