namespace Historia.Services.Ai;

/// <summary>
/// Builds the one prompt that asks a model for a commit message, and cleans up its reply.
/// Adapted from SourceGit (MIT); the staged diff is part of the prompt instead of being fetched
/// through a tool.
/// </summary>
public static class CommitMessagePrompt
{
    public const string OmittedDiffNote = "(diff omitted: size limit)";

    /// <summary>
    /// The instruction, the branch, the complete list of staged changes, then the diff of each
    /// file in path order. With a <paramref name="capBytes"/>, whole-file diffs are added until the
    /// next one would exceed it, or was never read because it lay past the cap; the rest are listed
    /// as omitted. Status letters are <c>A M D T R C</c>.
    /// </summary>
    public static string Build(
        string branch,
        IReadOnlyList<(string Status, string Path, string? OldPath)> files,
        IReadOnlyList<(string Path, string? Patch)> diffs,
        long? capBytes,
        string additionalPrompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Generate a commit message (follow the rule of conventional commit message) for the staged changes of a git repository.")
          .AppendLine("- The complete list of changed files follows, then the staged diff of each file. Binary files have no diff and can be skipped.")
          .AppendLine("- Output the conventional commit message (with detail changes in list) directly. Do not explain your output nor introduce your answer.");
        if (!string.IsNullOrWhiteSpace(additionalPrompt))
            sb.AppendLine(additionalPrompt.Trim());
        sb.Append("Current branch: \"").Append(branch).AppendLine("\"")
          .AppendLine("Changed files ('A' means added, 'M' means modified, 'D' means deleted, 'T' means type changed, 'R' means renamed, 'C' means copied):");

        foreach (var (status, path, oldPath) in files)
        {
            sb.Append(status).Append('\t');
            if (status is "R" or "C" && !string.IsNullOrEmpty(oldPath))
                sb.Append(oldPath).Append(" -> ");
            sb.AppendLine(path);
        }

        sb.AppendLine().AppendLine("Staged diff:");

        long used = 0;
        var capped = false;
        foreach (var (path, patch) in diffs.OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            sb.Append("### ").AppendLine(path);
            if (!capped && capBytes is { } cap)
            {
                var size = patch is null ? long.MaxValue : Encoding.UTF8.GetByteCount(patch);
                if (patch is null || used + size > cap)
                    capped = true;
                else
                    used += size;
            }

            if (capped || patch is null)
            {
                sb.AppendLine(OmittedDiffNote);
                continue;
            }
            sb.AppendLine(patch.TrimEnd('\r', '\n'));
        }

        return sb.ToString();
    }

    /// <summary>Strips a surrounding code fence and whitespace. An empty result is
    /// <see cref="AiErrorKind.EmptyResponse"/>.</summary>
    public static string Clean(string? text)
    {
        var s = (text ?? "").ReplaceLineEndings("\n").Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = s.IndexOf('\n');
            s = newline < 0 ? "" : s[(newline + 1)..];
        }
        if (s.EndsWith("\n```", StringComparison.Ordinal))
            s = s[..^4];
        else if (s == "```")
            s = "";
        s = s.Trim();

        if (s.Length == 0)
            throw new AiException(AiErrorKind.EmptyResponse, "The AI service returned an empty commit message.");
        return s;
    }
}
