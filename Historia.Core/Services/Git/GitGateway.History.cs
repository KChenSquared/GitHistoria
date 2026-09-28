namespace Historia.Services.Git;

public sealed partial class GitGateway
{

    private const string Sha1EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
    private const string Sha256EmptyTree = "6ef19b41225c5369f1c104d45d8d85efa9b057b53b14b4b9b939dd74decc5321";
    private static readonly IReadOnlyDictionary<string, string> NoReplaceObjects =
        new Dictionary<string, string> { ["GIT_NO_REPLACE_OBJECTS"] = "1" };

    public async Task<CommitDetail> GetCommitDetailAsync(GitObjectId id, CancellationToken ct = default)
    {
        var reader = new CommitHeaderReader(cli, context.RepositoryPath);
        var records = await reader.ReadAsync([id], ct).ConfigureAwait(false);
        var record = records[0];

        var fullMessage = record.Message ?? "";
        var firstBreak = fullMessage.IndexOf('\n');
        var messageBody = firstBreak >= 0 ? fullMessage[(firstBreak + 1)..].Trim() : "";

        return new CommitDetail
        {
            Id = record.Id,
            Message = fullMessage.TrimEnd('\n', '\r'),
            MessageShort = record.Subject,
            MessageBody = messageBody,
            AuthorName = record.AuthorName,
            AuthorEmail = record.AuthorEmail,
            AuthorWhen = record.AuthorWhen,
            CommitterName = record.CommitterName,
            CommitterEmail = record.CommitterEmail,
            CommitWhen = record.CommitWhen,
            ParentIds = record.ParentIds,
            Decorations = [],
        };
    }

    public async Task<IReadOnlyList<FileStatusEntry>> GetCommitFilesAsync(GitObjectId id, int parentIndex, CancellationToken ct = default)
    {
        var n = Math.Max(1, parentIndex);
        var args = new List<string>
        {
            "diff-tree", "-r", "-z", "-M", "--name-status", "--no-commit-id",
            $"{id}^{n}",
            id.ToString(),
        };

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = context.RepositoryPath,
            RawStandardOutput = true,
            Timeout = Timeout.InfiniteTimeSpan,
            ExtraEnvironment = NoReplaceObjects,
        }, ct).ConfigureAwait(false);

        if (result.IsSuccess)
            return ParseCommitFiles(result.StandardOutput);

        var emptyTree = id.HashSize == 32 ? Sha256EmptyTree : Sha1EmptyTree;
        var fallbackArgs = new List<string>
        {
            "diff-tree", "-r", "-z", "-M", "--name-status", "--no-commit-id",
            emptyTree,
            id.ToString(),
        };

        var fallbackResult = await cli.RunAsync(new GitCliRequest
        {
            Arguments = fallbackArgs,
            WorkingDirectory = context.RepositoryPath,
            RawStandardOutput = true,
            Timeout = Timeout.InfiniteTimeSpan,
            ExtraEnvironment = NoReplaceObjects,
        }, ct).ConfigureAwait(false);

        if (!fallbackResult.IsSuccess)
            throw HistoriaGitException.FromCli(fallbackResult, "Commit files");

        return ParseCommitFiles(fallbackResult.StandardOutput);
    }

    internal static IReadOnlyList<FileStatusEntry> ParseCommitFiles(string stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return [];

        var fields = stdout.Split('\0');
        var result = new List<FileStatusEntry>();
        var i = 0;
        while (i < fields.Length)
        {
            var status = fields[i++];
            if (string.IsNullOrEmpty(status))
                continue;

            var code = status[0];
            if (code is 'R' or 'C')
            {
                if (i + 1 >= fields.Length)
                    break;
                var oldPath = fields[i++];
                var newPath = fields[i++];
                result.Add(new FileStatusEntry
                {
                    Path = newPath,
                    Kind = code == 'R' ? FileChangeKind.Renamed : FileChangeKind.Copied,
                    OldPath = oldPath == newPath ? null : oldPath,
                    IsBinary = false,
                    IsSubmodule = false,
                });
            }
            else
            {
                if (i >= fields.Length)
                    break;
                var path = fields[i++];
                result.Add(new FileStatusEntry
                {
                    Path = path,
                    Kind = code switch
                    {
                        'A' => FileChangeKind.Added,
                        'D' => FileChangeKind.Deleted,
                        'T' => FileChangeKind.TypeChanged,
                        _ => FileChangeKind.Modified,
                    },
                    OldPath = null,
                    IsBinary = false,
                    IsSubmodule = false,
                });
            }
        }

        return result;
    }
}
