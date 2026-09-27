namespace Historia.Models;

/// <summary>An entry in the recent-repositories list. Validated lazily on display.</summary>
public sealed record RecentRepository
{
    public required string Path { get; init; }
    public required string DisplayName { get; init; }
    public required DateTimeOffset LastOpenedAt { get; init; }

    /// <summary>Set at display time when <see cref="Path"/> no longer resolves — never auto-removed.</summary>
    public bool IsStale { get; init; }
}
