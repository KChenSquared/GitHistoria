using System.Diagnostics;
using System.IO;
using LibGit2Sharp;
using Historia.Services.Git;
using Historia.Services.Settings;
using Historia.Services.Workspace;

namespace Historia.Services.PowerTools;

/// <inheritdoc cref="IConflictService"/>
public sealed class ConflictService(IGitCliService cli, ISettingsService settings) : IConflictService
{
    public Task<IReadOnlyList<ConflictFile>> GetConflictsAsync(RepositoryContext context, CancellationToken ct = default)
        => context.WithRepositoryAsync(ListConflicts, ct);

    public Task<ConflictFile> LoadConflictContentAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
        => context.WithRepositoryAsync(repo => LoadContent(repo, filePath), ct);

    public IReadOnlyList<ConflictRegion> ParseMarkerRegions(string workingTreeContent)
    {
        var regions = new List<ConflictRegion>();
        if (string.IsNullOrEmpty(workingTreeContent))
            return regions;

        var lines = workingTreeContent.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("<<<<<<<", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            var start = i;
            var ours = new List<string>();
            var theirs = new List<string>();
            List<string>? baseText = null;
            var section = 0; // 0 = ours, 1 = base (diff3), 2 = theirs
            i++;

            while (i < lines.Length && !lines[i].StartsWith(">>>>>>>", StringComparison.Ordinal))
            {
                if (lines[i].StartsWith("|||||||", StringComparison.Ordinal)) { section = 1; baseText = []; }
                else if (lines[i].StartsWith("=======", StringComparison.Ordinal)) section = 2;
                else switch (section)
                {
                    case 0: ours.Add(lines[i]); break;
                    case 1: baseText!.Add(lines[i]); break;
                    default: theirs.Add(lines[i]); break;
                }

                i++;
            }

            regions.Add(new ConflictRegion
            {
                StartLine = start + 1,
                EndLine = i + 1,
                BaseText = baseText is null ? null : string.Join('\n', baseText),
                OursText = string.Join('\n', ours),
                TheirsText = string.Join('\n', theirs),
            });
            i++;
        }

        return regions;
    }

    public async Task MarkResolvedAsync(RepositoryContext context, string filePath, string resolvedContent, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        var full = Path.Combine(context.RepositoryPath, filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, resolvedContent, ct).ConfigureAwait(false);

        await context.WithRepositoryAsync(repo => Commands.Stage(repo, filePath), ct).ConfigureAwait(false);
    }

    public async Task ResolveWithSideAsync(RepositoryContext context, string filePath, ConflictSide side, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        var conflict = await context.WithRepositoryAsync(repo => LoadContent(repo, filePath), ct).ConfigureAwait(false);
        var keepContent = side == ConflictSide.Ours ? conflict.OursContent : conflict.TheirsContent;

        if (keepContent is null)
        {
            // The chosen side deleted the file.
            var rm = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["rm", "-f", "--", filePath],
                WorkingDirectory = context.RepositoryPath,
            }, ct).ConfigureAwait(false);
            if (!rm.IsSuccess)
                throw HistoriaGitException.FromCli(rm, "Resolve (delete)");
            return;
        }

        var full = Path.Combine(context.RepositoryPath, filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, keepContent, ct).ConfigureAwait(false);
        await context.WithRepositoryAsync(repo => Commands.Stage(repo, filePath), ct).ConfigureAwait(false);
    }

    public async Task<bool> LaunchExternalMergeToolAsync(RepositoryContext context, string filePath, CancellationToken ct = default)
    {
        var conflict = await context.WithRepositoryAsync(repo => LoadContent(repo, filePath), ct).ConfigureAwait(false);
        var tool = settings.Current.ExternalMergeTool;
        if (string.IsNullOrWhiteSpace(tool))
        {
            // Fall back to Git's own configured mergetool.
            var result = await cli.RunAsync(new GitCliRequest
            {
                Arguments = ["mergetool", "--no-prompt", "--", filePath],
                WorkingDirectory = context.RepositoryPath,
                Timeout = TimeSpan.FromMinutes(30),
            }, ct).ConfigureAwait(false);
            return result.ExitCode == 0;
        }

        // §8.4.5: write temp files from stages 1/2/3 + working tree; substitute $BASE/$LOCAL/$REMOTE/$MERGED.
        var temp = Directory.CreateTempSubdirectory("Historia-merge-").FullName;
        try
        {
            var basePath = WriteTemp(temp, "BASE", conflict.BaseContent);
            var localPath = WriteTemp(temp, "LOCAL", conflict.OursContent);
            var remotePath = WriteTemp(temp, "REMOTE", conflict.TheirsContent);
            var mergedPath = Path.Combine(context.RepositoryPath, filePath);

            var cmd = tool
                .Replace("$BASE", Quote(basePath)).Replace("$LOCAL", Quote(localPath))
                .Replace("$REMOTE", Quote(remotePath)).Replace("$MERGED", Quote(mergedPath));

            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(cmd);
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var merged = await File.ReadAllTextAsync(mergedPath, ct).ConfigureAwait(false);
            return !merged.Contains("<<<<<<<", StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    public async Task ContinueOperationAsync(RepositoryContext context, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();

        var stillUnmerged = await context.WithRepositoryAsync(HasUnmergedPaths, ct).ConfigureAwait(false);
        if (stillUnmerged)
            throw new HistoriaGitException(GitErrorKind.MergeConflict, "Some paths are still unmerged. Resolve every conflicted file first.");

        await RunSequencerAsync(context, "--continue", ct).ConfigureAwait(false);
    }

    public Task SkipCurrentAsync(RepositoryContext context, CancellationToken ct = default)
    {
        var op = RepoOperation(context);
        if (op == OperationState.Merging)
            throw new HistoriaGitException(GitErrorKind.Unknown, "A merge cannot skip a commit — resolve or abort it.");
        return RunSequencerAsync(context, "--skip", ct);
    }

    public Task AbortOperationAsync(RepositoryContext context, CancellationToken ct = default)
        => RunSequencerAsync(context, "--abort", ct);

    // ── internals ──

    private async Task RunSequencerAsync(RepositoryContext context, string action, CancellationToken ct)
    {
        using var _ = context.BeginSelfOperation();

        var verb = RepoOperation(context) switch
        {
            OperationState.Merging => "merge",
            OperationState.Rebasing => "rebase",
            OperationState.CherryPicking => "cherry-pick",
            OperationState.Reverting => "revert",
            _ => throw new HistoriaGitException(GitErrorKind.Unknown, "No operation is in progress."),
        };

        var env = new Dictionary<string, string> { ["GIT_EDITOR"] = "true" }; // never open an editor on --continue
        var result = await cli.RunAsync(new GitCliRequest
        {
            Arguments = [verb, action],
            WorkingDirectory = context.RepositoryPath,
            ExtraEnvironment = env,
        }, ct).ConfigureAwait(false);

        if (!result.IsSuccess && result.ErrorKind != GitErrorKind.MergeConflict)
            throw HistoriaGitException.FromCli(result, $"{verb} {action}");

        await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);
    }

    private static OperationState RepoOperation(RepositoryContext context)
        => Historia.Services.State.RepositoryStateService.DetectOperationState(context.GitDirectory);

    private static IReadOnlyList<ConflictFile> ListConflicts(Repository repo)
    {
        var result = new List<ConflictFile>();
        foreach (var conflict in repo.Index.Conflicts)
        {
            var path = conflict.Ancestor?.Path ?? conflict.Ours?.Path ?? conflict.Theirs?.Path ?? "";
            var status = DeriveStatus(conflict);
            var isBinary = IsBinary(repo, conflict.Ours) || IsBinary(repo, conflict.Theirs) || IsBinary(repo, conflict.Ancestor);
            result.Add(new ConflictFile
            {
                FilePath = path,
                Status = status,
                IsBinary = isBinary,
                BaseContent = null,
                OursContent = null,
                TheirsContent = null,
            });
        }

        return result;
    }

    private static ConflictFile LoadContent(Repository repo, string filePath)
    {
        var conflict = repo.Index.Conflicts[filePath]
            ?? throw new HistoriaGitException(GitErrorKind.RefNotFound, $"'{filePath}' is not a conflicted path.");

        var isBinary = IsBinary(repo, conflict.Ours) || IsBinary(repo, conflict.Theirs);
        return new ConflictFile
        {
            FilePath = filePath,
            Status = DeriveStatus(conflict),
            IsBinary = isBinary,
            BaseContent = isBinary ? null : StageText(repo, conflict.Ancestor),
            OursContent = isBinary ? null : StageText(repo, conflict.Ours),
            TheirsContent = isBinary ? null : StageText(repo, conflict.Theirs),
        };
    }

    private static bool HasUnmergedPaths(Repository repo) => repo.Index.Conflicts.Any();

    private static ConflictStatus DeriveStatus(Conflict c)
    {
        var haveBase = c.Ancestor is not null;
        var haveOurs = c.Ours is not null;
        var haveTheirs = c.Theirs is not null;

        return (haveBase, haveOurs, haveTheirs) switch
        {
            (true, true, true) => ConflictStatus.BothModified,
            (false, true, true) => ConflictStatus.BothAdded,
            (true, false, true) => ConflictStatus.DeletedByUs,
            (true, true, false) => ConflictStatus.DeletedByThem,
            (false, true, false) => ConflictStatus.AddedByUs,
            (false, false, true) => ConflictStatus.AddedByThem,
            _ => ConflictStatus.BothDeletedRenamed,
        };
    }

    private static string? StageText(Repository repo, IndexEntry? entry)
    {
        if (entry is null)
            return null;
        var blob = repo.Lookup<Blob>(entry.Id);
        return blob?.GetContentText();
    }

    private static bool IsBinary(Repository repo, IndexEntry? entry)
        => entry is not null && (repo.Lookup<Blob>(entry.Id)?.IsBinary ?? false);

    private static string WriteTemp(string dir, string name, string? content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content ?? string.Empty);
        return path;
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}
