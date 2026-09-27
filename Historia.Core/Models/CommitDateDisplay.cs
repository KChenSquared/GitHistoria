namespace Historia.Models;

/// <summary>
/// Chooses between a commit's author date and commit (committer) date for UI display.
/// The graph walk always orders by commit date; this only governs what is displayed in the Date
/// column and the commit detail panel.
/// </summary>
public static class CommitDateDisplay
{
    public static DateTimeOffset Pick(DateTimeOffset authorWhen, DateTimeOffset commitWhen, bool showAuthorDate) =>
        showAuthorDate ? authorWhen : commitWhen;
}
