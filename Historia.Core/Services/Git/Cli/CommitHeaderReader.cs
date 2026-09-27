namespace Historia.Services.Git;

public sealed record CommitHeaderRecord
{
    public required GitObjectId Id { get; init; }
    public required IReadOnlyList<GitObjectId> ParentIds { get; init; }
    public required string AuthorName { get; init; }
    public required string AuthorEmail { get; init; }
    public required DateTimeOffset AuthorWhen { get; init; }
    public required string CommitterName { get; init; }
    public required string CommitterEmail { get; init; }
    public required DateTimeOffset CommitWhen { get; init; }
    public required string Subject { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// Reads commit headers (metadata, parents, subject, raw message) via one
/// <c>git log --no-walk=unsorted</c> invocation with NUL-separated fields.
/// </summary>
public sealed class CommitHeaderReader(IGitCliService cli, string repositoryPath)
{
    public const int FieldCount = 10;
    public const string Format = "%H%x00%P%x00%an%x00%ae%x00%aI%x00%cn%x00%ce%x00%cI%x00%s%x00%B%x00";

    internal static readonly IReadOnlyDictionary<string, string> NoReplaceObjects =
        new Dictionary<string, string> { ["GIT_NO_REPLACE_OBJECTS"] = "1" };

    public async Task<IReadOnlyList<CommitHeaderRecord>> ReadAsync(
        IReadOnlyList<GitObjectId> ids,
        CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return [];

        // log.showSignature=true would print verification text ahead of each record.
        var args = new List<string>(ids.Count + 4)
        {
            "log",
            "--no-walk=unsorted",
            "--no-show-signature",
            $"--format={Format}",
        };

        foreach (var id in ids)
            args.Add(id.ToString());

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = repositoryPath,
            RawStandardOutput = true,
            // Same objects as the graph rows (GitLogStream ignores replace refs too).
            ExtraEnvironment = NoReplaceObjects,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Read commit headers");

        var records = Parse(result.StandardOutput);
        if (records.Count != ids.Count)
        {
            throw new HistoriaGitException(
                GitErrorKind.RefNotFound,
                $"Expected {ids.Count} commit header(s), but received {records.Count}.");
        }

        return records;
    }

    internal static List<CommitHeaderRecord> Parse(string stdout)
    {
        var fields = stdout.Split('\0');
        var usable = fields.Length - 1;
        if (usable < 0 || usable % FieldCount != 0 || !string.IsNullOrWhiteSpace(fields[^1]))
        {
            throw new FormatException(
                $"git log output ended mid-record ({usable} fields, not a multiple of {FieldCount}).");
        }

        var list = new List<CommitHeaderRecord>(usable / FieldCount);
        for (var i = 0; i < usable; i += FieldCount)
        {
            var idStr = fields[i].TrimStart('\r', '\n');
            var id = GitObjectId.Parse(idStr);

            var parentStr = fields[i + 1].Trim();
            var parentIds = string.IsNullOrEmpty(parentStr)
                ? (IReadOnlyList<GitObjectId>)[]
                : parentStr.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(GitObjectId.Parse).ToList();

            var authorName = fields[i + 2];
            var authorEmail = fields[i + 3];
            var authorWhen = DateTimeOffset.Parse(fields[i + 4], CultureInfo.InvariantCulture);

            var committerName = fields[i + 5];
            var committerEmail = fields[i + 6];
            var commitWhen = DateTimeOffset.Parse(fields[i + 7], CultureInfo.InvariantCulture);

            var subject = fields[i + 8];
            var message = fields[i + 9];

            list.Add(new CommitHeaderRecord
            {
                Id = id,
                ParentIds = parentIds,
                AuthorName = authorName,
                AuthorEmail = authorEmail,
                AuthorWhen = authorWhen,
                CommitterName = committerName,
                CommitterEmail = committerEmail,
                CommitWhen = commitWhen,
                Subject = subject,
                Message = message,
            });
        }

        return list;
    }
}
