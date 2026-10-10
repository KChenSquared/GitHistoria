namespace Historia.Controls;

/// <summary>
/// Helper for incremental text document updates that avoid full re-tokenization.
/// Adapted from SourceGit (MIT) — see docs/third-party-licenses.md.
/// </summary>
public static class ConflictPaneUpdateHelper
{
    public static void UpdateDocumentIncremental(TextDocument document, IReadOnlyList<ConflictPaneLine> newLines)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(newLines);

        var newText = string.Join('\n', newLines.Select(l => l.Text));
        var oldText = document.Text;

        if (oldText == newText)
        {
            return;
        }

        if (string.IsNullOrEmpty(oldText))
        {
            document.Text = newText;
            return;
        }

        // Find common prefix length
        int prefixLen = 0;
        int minLen = Math.Min(oldText.Length, newText.Length);
        while (prefixLen < minLen && oldText[prefixLen] == newText[prefixLen])
        {
            prefixLen++;
        }

        // Find common suffix length
        int suffixLen = 0;
        while (suffixLen < (minLen - prefixLen) &&
               oldText[oldText.Length - 1 - suffixLen] == newText[newText.Length - 1 - suffixLen])
        {
            suffixLen++;
        }

        int replaceStart = prefixLen;
        int replaceLength = oldText.Length - prefixLen - suffixLen;
        string replacement = newText.Substring(prefixLen, newText.Length - prefixLen - suffixLen);

        document.Replace(replaceStart, replaceLength, replacement);
    }
}
