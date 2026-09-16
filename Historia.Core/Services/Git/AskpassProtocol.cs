namespace Historia.Services.Git;

/// <summary>
/// The environment-variable contract between <c>GitGateway</c> (which composes the credential
/// environment for a network operation) and the re-invoked-self <c>GIT_ASKPASS</c> /
/// <c>SSH_ASKPASS</c> helper in <c>Historia.Program.Main</c> (which answers Git's prompt). Kept
/// here rather than in Historia.App so the host-matching rule is unit-testable.
/// <para>
/// Git names the host it is actually asking about inside the prompt text itself — e.g.
/// <c>"Password for 'https://user@host/path': "</c> — so the helper must extract that host and
/// compare it against the one the credentials were resolved for, rather than trusting that Git
/// ends up contacting the same host the caller looked up (a redirected <c>pushurl</c>, an
/// <c>insteadOf</c> rewrite, or a submodule URL can all differ).
/// </para>
/// </summary>
public static class AskpassProtocol
{
    /// <summary>True when <paramref name="prompt"/> names the same host as
    /// <paramref name="expectedHost"/>. False (never guess) if either is missing or the prompt's
    /// quoted text does not parse as an absolute URL.</summary>
    public static bool PromptHostMatches(string prompt, string? expectedHost)
    {
        if (string.IsNullOrEmpty(expectedHost) || string.IsNullOrEmpty(prompt))
            return false;

        var start = prompt.IndexOf('\'');
        var end = prompt.LastIndexOf('\'');
        if (start < 0 || end <= start)
            return false;

        var urlText = prompt[(start + 1)..end];
        return Uri.TryCreate(urlText, UriKind.Absolute, out var uri)
            && string.Equals(uri.Host, expectedHost, StringComparison.OrdinalIgnoreCase);
    }
}
