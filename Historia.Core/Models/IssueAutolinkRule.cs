namespace Historia.Models;

/// <summary>
/// One issue-autolink rule: a regex applied to commit-message text, with a
/// URL template whose <c>$1</c>, <c>$2</c>… are replaced by the match's capture groups (and
/// <c>$0</c> by the whole match). Account-free — pure text substitution plus a shell open.
/// </summary>
public sealed record IssueAutolinkRule
{
    public required string Pattern { get; init; }
    public required string UrlTemplate { get; init; }
}
