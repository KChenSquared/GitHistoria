namespace Historia.Services.Git;

public sealed partial class GitGateway
{

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
        var reader = new CommitHeaderReader(cli, context.RepositoryPath);
        var records = await reader.ReadAsync([id], ct).ConfigureAwait(false);
        var record = records[0];

        var parentIndex0 = Math.Max(0, parentIndex - 1);
        GitObjectId? parent = parentIndex0 < record.ParentIds.Count ? record.ParentIds[parentIndex0] : null;

        var args = new List<string> { "diff-tree", "-r", "-z", "-M", "--name-status", "--no-commit-id" };
        if (record.ParentIds.Count == 0 || parent is null)
        {
            args.Add("--root");
            args.Add(id.ToString());
        }
        else
        {
            args.Add(parent.Value.ToString());
            args.Add(id.ToString());
        }

        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = context.RepositoryPath,
            RawStandardOutput = true,
            Timeout = Timeout.InfiniteTimeSpan,
            ExtraEnvironment = CommitHeaderReader.NoReplaceObjects,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, "Commit files");

        return ParseCommitFiles(result.StandardOutput);
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
