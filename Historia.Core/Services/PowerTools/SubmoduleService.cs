using System.IO;
using System.Text.RegularExpressions;
using Historia.Services.Git;
using Historia.Services.Workspace;

namespace Historia.Services.PowerTools;

/// <inheritdoc cref="ISubmoduleService"/>
public sealed partial class SubmoduleService(IGitCliService cli) : ISubmoduleService
{
    public async Task<IReadOnlyList<SubmoduleInfo>> ListAsync(RepositoryContext context, CancellationToken ct = default)
    {
        // `git submodule status` prefix: '-' uninit, '+' HEAD differs, 'U' conflicts, ' ' clean.
        var status = await Run(context, ["submodule", "status", "--recursive"], ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(status.StandardOutput))
            return [];

        var urls = await ReadModuleConfigAsync(context, ct).ConfigureAwait(false);
        var result = new List<SubmoduleInfo>();

        foreach (var line in status.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = StatusLine().Match(line);
            if (!m.Success)
                continue;

            var flag = m.Groups["flag"].Value;
            var sha = m.Groups["sha"].Value;
            var path = m.Groups["path"].Value.Trim();

            result.Add(new SubmoduleInfo
            {
                Name = path,
                Path = path,
                Url = urls.GetValueOrDefault(path, ""),
                HeadCommitId = LibGit2Sharp.ObjectId.TryParse(sha, out var id) ? id : null,
                RegisteredCommitId = null,
                Status = flag switch
                {
                    "-" => SubmoduleStatus.Uninitialized,
                    "+" => SubmoduleStatus.Modified,
                    "U" => SubmoduleStatus.OutOfSync,
                    _ => SubmoduleStatus.Clean,
                },
                BranchTracking = m.Groups["branch"].Success ? m.Groups["branch"].Value : null,
            });
        }

        return result;
    }

    public Task AddAsync(RepositoryContext context, string url, string path, CancellationToken ct = default)
        => Mutate(context, ["submodule", "add", "--", url, path], "Add submodule", ct);

    public Task InitAndUpdateAsync(RepositoryContext context, string? path, bool recursive, CancellationToken ct = default)
    {
        // `--init` on every update path: plain `--recursive` silently does nothing for
        // never-checked-out submodules (§8.6.2 correction).
        var args = new List<string> { "submodule", "update", "--init" };
        if (recursive) args.Add("--recursive");
        if (path is not null) { args.Add("--"); args.Add(path); }
        return Mutate(context, args, "Update submodule", ct);
    }

    public Task SyncAsync(RepositoryContext context, string? path, CancellationToken ct = default)
    {
        var args = new List<string> { "submodule", "sync", "--recursive" };
        if (path is not null) { args.Add("--"); args.Add(path); }
        return Mutate(context, args, "Sync submodule", ct);
    }

    public async Task RemoveAsync(RepositoryContext context, SubmoduleInfo submodule, CancellationToken ct = default)
    {
        using var _ = context.BeginSelfOperation();
        await Expect(context, ["submodule", "deinit", "-f", "--", submodule.Path], "Deinit submodule", ct).ConfigureAwait(false);
        await Expect(context, ["rm", "-f", "--", submodule.Path], "Remove submodule", ct).ConfigureAwait(false);

        TryDeleteModuleDir(Path.Combine(context.GitDirectory, "modules", submodule.Name));
        await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);
    }

    private static void TryDeleteModuleDir(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        try
        {
            // Git packs objects read-only; clear the attribute before deleting.
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — an orphaned cached module directory is harmless.
        }
    }

    private async Task<Dictionary<string, string>> ReadModuleConfigAsync(RepositoryContext context, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var gm = Path.Combine(context.RepositoryPath, ".gitmodules");
        if (!File.Exists(gm))
            return map;

        var list = await Run(context, ["config", "-f", ".gitmodules", "--list"], ct).ConfigureAwait(false);
        string? currentPath = null;
        foreach (var line in list.StandardOutput.Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq];
            var val = line[(eq + 1)..];
            if (key.EndsWith(".path", StringComparison.Ordinal)) currentPath = val;
            else if (key.EndsWith(".url", StringComparison.Ordinal) && currentPath is not null)
            {
                map[currentPath] = val;
                currentPath = null;
            }
        }

        return map;
    }

    private async Task Mutate(RepositoryContext context, IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        using var _ = context.BeginSelfOperation();
        await Expect(context, args, what, ct).ConfigureAwait(false);
        await context.State.RefreshAllAsync(context.Lifetime).ConfigureAwait(false);
    }

    private Task<GitCliResult> Run(RepositoryContext context, IReadOnlyList<string> args, CancellationToken ct)
        => cli.RunAsync(new GitCliRequest
        {
            Arguments = args,
            WorkingDirectory = context.RepositoryPath,
            Timeout = TimeSpan.FromMinutes(10),
        }, ct);

    private async Task Expect(RepositoryContext context, IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        var result = await Run(context, args, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw HistoriaGitException.FromCli(result, what);
    }

    [GeneratedRegex(@"^(?<flag>[-+U ])(?<sha>[0-9a-f]{7,40}) (?<path>[^\(]+)(?:\((?<branch>[^\)]+)\))?", RegexOptions.CultureInvariant)]
    private static partial Regex StatusLine();
}
