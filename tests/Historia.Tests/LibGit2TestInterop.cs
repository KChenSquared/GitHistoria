namespace Historia.Tests;

public static class LibGit2TestInterop
{
    public static GitObjectId ToGitObjectId(this ObjectId id) =>
        GitObjectId.FromBytes(id.RawId);

    public static ObjectId ToLibGit2(this GitObjectId id)
    {
        Span<byte> bytes = stackalloc byte[id.HashSize];
        id.CopyTo(bytes);
        return new ObjectId(bytes.ToArray());
    }

    public static ObjectId? ToLibGit2(this GitObjectId? id) =>
        id is null ? null : id.Value.ToLibGit2();

    public static FileDiff ProjectDiff(this Repository repo, DiffRequest request)
    {
        const long maxBytes = 5 * 1024 * 1024;
        const int maxLines = 50_000;

        if (string.IsNullOrWhiteSpace(request.Path))
            throw new HistoriaGitException(GitErrorKind.Unknown, "A file path is required for a diff.");

        var path = request.Path!;
        var paths = new[] { path };
        var compareOptions = new LibGit2Sharp.CompareOptions
        {
            ContextLines = request.ContextLines,
            Similarity = SimilarityOptions.Renames,
        };
        ApplyWhitespace(compareOptions, request.Whitespace);

        Patch patch = request.Source switch
        {
            DiffSource.UnstagedChanges =>
                repo.Diff.Compare<Patch>(paths, includeUntracked: true, explicitPathsOptions: null, compareOptions: compareOptions),

            DiffSource.StagedChanges =>
                repo.Diff.Compare<Patch>(repo.Head.Tip?.Tree, DiffTargets.Index, paths, explicitPathsOptions: null, compareOptions: compareOptions),

            DiffSource.Commit => DiffCommit(repo, request, paths, compareOptions),

            DiffSource.Comparison => DiffComparison(repo, request, paths, compareOptions),

            _ => throw new HistoriaGitException(GitErrorKind.Unknown, $"Unsupported diff source '{request.Source}'."),
        };

        var entry = patch[path];
        if (entry is null)
            return FileDiff.EmptyFor(path, DiffKind.Modified);

        var kind = MapDiffKind(entry.Status);
        if (entry.IsBinaryComparison)
        {
            return FileDiff.EmptyFor(path, kind) with
            {
                OldPath = entry.OldPath == path ? null : entry.OldPath,
                IsBinary = true,
            };
        }

        var patchText = entry.Patch ?? string.Empty;
        var lineCount = CountLines(patchText);
        if (patchText.Length > maxBytes || lineCount > maxLines)
        {
            return FileDiff.EmptyFor(path, kind) with
            {
                OldPath = entry.OldPath == path ? null : entry.OldPath,
                IsTruncated = true,
                AddedLines = entry.LinesAdded,
                DeletedLines = entry.LinesDeleted,
            };
        }

        var hunks = UnifiedDiffParser.Parse(patchText);
        return new FileDiff
        {
            Path = path,
            OldPath = entry.OldPath == path ? null : entry.OldPath,
            Kind = kind,
            IsBinary = false,
            IsTruncated = false,
            Hunks = hunks,
            AddedLines = entry.LinesAdded,
            DeletedLines = entry.LinesDeleted,
            RawPatch = patchText,
        };
    }

    private static void ApplyWhitespace(LibGit2Sharp.CompareOptions options, Historia.Models.WhitespaceMode mode)
    {
        options.Algorithm = DiffAlgorithm.Myers;
        _ = mode;
    }

    private static Patch DiffCommit(Repository repo, DiffRequest request, string[] paths, LibGit2Sharp.CompareOptions options)
    {
        var commit = repo.Lookup<Commit>(request.CommitId?.ToLibGit2()
            ?? throw new HistoriaGitException(GitErrorKind.RefNotFound, "No commit id supplied."));
        if (commit is null)
            throw new HistoriaGitException(GitErrorKind.RefNotFound, "Commit not found.");

        var parent = commit.Parents.ElementAtOrDefault(Math.Max(0, request.ParentIndex - 1));
        return repo.Diff.Compare<Patch>(parent?.Tree, commit.Tree, paths, explicitPathsOptions: null, compareOptions: options);
    }

    private static Patch DiffComparison(Repository repo, DiffRequest request, string[] paths, LibGit2Sharp.CompareOptions options)
    {
        var left = request.CommitId is { } l ? repo.Lookup<Commit>(l.ToLibGit2()) : null;
        var right = request.OtherCommitId is { } r ? repo.Lookup<Commit>(r.ToLibGit2()) : null;
        return repo.Diff.Compare<Patch>(left?.Tree, right?.Tree, paths, explicitPathsOptions: null, compareOptions: options);
    }

    private static DiffKind MapDiffKind(ChangeKind status) => status switch
    {
        ChangeKind.Added => DiffKind.Added,
        ChangeKind.Deleted => DiffKind.Deleted,
        ChangeKind.Renamed => DiffKind.Renamed,
        ChangeKind.Copied => DiffKind.Copied,
        ChangeKind.TypeChanged => DiffKind.TypeChanged,
        _ => DiffKind.Modified,
    };

    private static int CountLines(string text)
    {
        var count = 1;
        foreach (var ch in text)
            if (ch == '\n')
                count++;
        return count;
    }
}
