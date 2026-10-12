namespace Historia.Services.UI;

/// <summary>
/// Holds each repository's unsent commit message for the life of the app session, so switching
/// tabs or views and coming back finds what was typed. Never persisted. Safe to call from any thread.
/// </summary>
public interface ICommitDraftStore
{
    /// <summary>The draft saved for <paramref name="repoKey"/>, or an empty string when there is none.</summary>
    string Get(string repoKey);

    /// <summary>Saves <paramref name="draft"/> for <paramref name="repoKey"/>; null or empty forgets it.</summary>
    void Set(string repoKey, string? draft);
}

/// <inheritdoc cref="ICommitDraftStore"/>
public sealed class CommitDraftStore : ICommitDraftStore
{
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public string Get(string repoKey)
    {
        ArgumentNullException.ThrowIfNull(repoKey);
        lock (_sync)
            return _drafts.GetValueOrDefault(repoKey, "");
    }

    public void Set(string repoKey, string? draft)
    {
        ArgumentNullException.ThrowIfNull(repoKey);
        lock (_sync)
        {
            if (string.IsNullOrEmpty(draft))
                _drafts.Remove(repoKey);
            else
                _drafts[repoKey] = draft;
        }
    }
}
