namespace Historia.Services.Git;

/// <summary>
/// R8: staging (or unstaging) a renamed/copied file must move both halves of the rename
/// together — otherwise a single "add renamed path" + "leave old path unstaged" produces a
/// staged <c>Added</c> entry and an unstaged <c>Deleted</c> entry instead of one staged
/// <c>Renamed</c> entry. Pure, no git, no Avalonia — see the plan's Decisions for why this lives
/// in Core.
/// </summary>
public static class StagePathExpansion
{
    /// <summary>Expands <paramref name="selectedPaths"/> to also include the <see cref="FileStatusEntry.OldPath"/>
    /// of any selected <see cref="FileChangeKind.Renamed"/> or <see cref="FileChangeKind.Copied"/>
    /// entry whose old path is known. De-duplicated, selection order preserved.</summary>
    public static IReadOnlyList<string> ForStaging(
        IReadOnlyList<FileStatusEntry> entries, IEnumerable<string> selectedPaths)
    {
        var byPath = entries.ToDictionary(e => e.Path, e => e, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        foreach (var path in selectedPaths)
        {
            if (seen.Add(path))
                result.Add(path);

            if (byPath.TryGetValue(path, out var entry)
                && entry.Kind is FileChangeKind.Renamed or FileChangeKind.Copied
                && entry.OldPath is { } oldPath
                && seen.Add(oldPath))
            {
                result.Add(oldPath);
            }
        }

        return result;
    }
}
