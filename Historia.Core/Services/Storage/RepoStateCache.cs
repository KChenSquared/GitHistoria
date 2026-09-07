using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibGit2Sharp;
using Historia.Services.Settings;

namespace Historia.Services.Storage;

/// <inheritdoc cref="IRepoStateCache"/>
public sealed class RepoStateCache(ISettingsService settings) : IRepoStateCache
{
    private const int Version = 1;
    private const int MaxFilesPerKind = 60;

    private static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    private bool Enabled => settings.Current.PersistRepoCache;

    // ── Load ──

    public RepoStateSnapshot? TryLoad(string repoPath)
    {
        if (!Enabled)
            return null;

        var refs = ReadGz<RefsFile>(FileFor(repoPath, "refs"));
        var pt = ReadGz<PowerFile>(FileFor(repoPath, "pt"));
        if (refs is null && pt is null)
            return null;

        return new RepoStateSnapshot(
            refs?.Branches?.Select(FromDto).ToList(),
            refs?.Tags?.Select(FromDto).ToList(),
            refs?.Remotes?.Select(FromDto).ToList(),
            pt?.Stashes is { } st ? st.Select(FromDto).ToList() : null,
            pt?.Submodules is { } sm ? sm.Select(FromDto).ToList() : null,
            pt?.Worktrees is { } wt ? wt.Select(FromDto).ToList() : null);
    }

    // ── Save ──

    public void SaveRefs(string repoPath, IReadOnlyList<BranchInfo> branches, IReadOnlyList<TagInfo> tags, IReadOnlyList<RemoteInfo> remotes)
    {
        if (!Enabled)
            return;
        WriteGz(FileFor(repoPath, "refs"), new RefsFile
        {
            Version = Version,
            Branches = branches.Select(ToDto).ToList(),
            Tags = tags.Select(ToDto).ToList(),
            Remotes = remotes.Select(ToDto).ToList(),
        });
    }

    public void SavePowerTools(string repoPath, IReadOnlyList<StashEntry>? stashes, IReadOnlyList<SubmoduleInfo>? submodules, IReadOnlyList<WorktreeInfo>? worktrees)
    {
        if (!Enabled)
            return;
        var existing = ReadGz<PowerFile>(FileFor(repoPath, "pt"));
        WriteGz(FileFor(repoPath, "pt"), new PowerFile
        {
            Version = Version,
            Stashes = stashes is not null ? stashes.Select(ToDto).ToList() : existing?.Stashes,
            Submodules = submodules is not null ? submodules.Select(ToDto).ToList() : existing?.Submodules,
            Worktrees = worktrees is not null ? worktrees.Select(ToDto).ToList() : existing?.Worktrees,
        });
    }

    public int WipeAll()
    {
        var n = 0;
        try
        {
            var dir = new DirectoryInfo(AppPaths.CacheDirectory);
            if (!dir.Exists)
                return 0;
            foreach (var f in dir.GetFiles("repo-refs-*.json.gz")
                         .Concat(dir.GetFiles("repo-pt-*.json.gz"))
                         .Concat(dir.GetFiles("repo-*.tmp")))
            {
                try { f.Delete(); n++; } catch { /* locked — skip */ }
            }
        }
        catch { }
        return n;
    }

    // ── gzip JSON helpers ──

    private static T? ReadGz<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var dto = JsonSerializer.Deserialize<T>(gz, Json);
            return dto is IVersioned v && v.Version != Version ? null : dto;
        }
        catch
        {
            return null; // corruption → treat as a miss
        }
    }

    private static void WriteGz<T>(string dest, T value)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDirectory);
            var tmp = $"{dest}.{Guid.NewGuid():N}.tmp";
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gz, value, Json);
            File.Move(tmp, dest, overwrite: true);
            Prune();
        }
        catch { /* cache write is best-effort */ }
    }

    private static void Prune()
    {
        try
        {
            var dir = new DirectoryInfo(AppPaths.CacheDirectory);
            foreach (var pattern in new[] { "repo-refs-*.json.gz", "repo-pt-*.json.gz" })
                foreach (var f in dir.GetFiles(pattern).OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxFilesPerKind))
                    f.Delete();
            foreach (var f in dir.GetFiles("repo-*.tmp"))
                if (DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromHours(1))
                    f.Delete();
        }
        catch { }
    }

    private static string FileFor(string repoPath, string kind)
    {
        var norm = repoPath.Replace('\\', '/').TrimEnd('/');
        if (OperatingSystem.IsWindows())
            norm = norm.ToLowerInvariant();
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(norm)));
        return Path.Combine(AppPaths.CacheDirectory, $"repo-{kind}-{hash}.json.gz");
    }

    private static ObjectId? Oid(string? sha) => sha is { Length: 40 } ? new ObjectId(sha) : null;

    // ── DTO mapping ──

    private static BranchDto ToDto(BranchInfo b) => new()
    {
        Name = b.Name, CanonicalName = b.CanonicalName, IsRemote = b.IsRemote, IsCurrent = b.IsCurrent,
        TipSha = b.TipCommitId?.Sha, RemoteName = b.RemoteName, UpstreamName = b.UpstreamName,
    };

    private static BranchInfo FromDto(BranchDto d) => new()
    {
        Name = d.Name, CanonicalName = d.CanonicalName, IsRemote = d.IsRemote, IsCurrent = d.IsCurrent,
        TipCommitId = Oid(d.TipSha), RemoteName = d.RemoteName, UpstreamName = d.UpstreamName,
    };

    private static TagDto ToDto(TagInfo t) => new()
    {
        Name = t.Name, TargetSha = t.TargetCommitId.Sha, IsAnnotated = t.IsAnnotated,
        Message = t.Message, TaggerName = t.TaggerName, TaggerWhen = t.TaggerWhen,
    };

    private static TagInfo FromDto(TagDto d) => new()
    {
        Name = d.Name, TargetCommitId = Oid(d.TargetSha) ?? ObjectId.Zero, IsAnnotated = d.IsAnnotated,
        Message = d.Message, TaggerName = d.TaggerName, TaggerWhen = d.TaggerWhen,
    };

    private static RemoteDto ToDto(RemoteInfo r) => new() { Name = r.Name, Url = r.Url, PushUrl = r.PushUrl };
    private static RemoteInfo FromDto(RemoteDto d) => new() { Name = d.Name, Url = d.Url, PushUrl = d.PushUrl };

    private static StashDto ToDto(StashEntry s) => new()
    {
        Index = s.Index, CommitSha = s.CommitId.Sha, Message = s.Message,
        AuthorName = s.AuthorName, AuthorEmail = s.AuthorEmail, CreatedAt = s.CreatedAt,
        BranchName = s.BranchName, FileCount = s.FileCount, IncludesUntracked = s.IncludesUntracked,
    };

    private static StashEntry FromDto(StashDto d) => new()
    {
        Index = d.Index, CommitId = Oid(d.CommitSha) ?? ObjectId.Zero, Message = d.Message,
        AuthorName = d.AuthorName, AuthorEmail = d.AuthorEmail, CreatedAt = d.CreatedAt,
        BranchName = d.BranchName, FileCount = d.FileCount, IncludesUntracked = d.IncludesUntracked,
    };

    private static SubDto ToDto(SubmoduleInfo s) => new()
    {
        Name = s.Name, Path = s.Path, Url = s.Url, HeadSha = s.HeadCommitId?.Sha,
        RegisteredSha = s.RegisteredCommitId?.Sha, Status = (int)s.Status, BranchTracking = s.BranchTracking,
    };

    private static SubmoduleInfo FromDto(SubDto d) => new()
    {
        Name = d.Name, Path = d.Path, Url = d.Url, HeadCommitId = Oid(d.HeadSha),
        RegisteredCommitId = Oid(d.RegisteredSha), Status = (Historia.Models.SubmoduleStatus)d.Status, BranchTracking = d.BranchTracking,
    };

    private static WtDto ToDto(WorktreeInfo w) => new()
    {
        Path = w.Path, Branch = w.Branch, HeadSha = w.HeadCommitId?.Sha,
        IsMainWorktree = w.IsMainWorktree, IsLocked = w.IsLocked, LockReason = w.LockReason, IsPrunable = w.IsPrunable,
    };

    private static WorktreeInfo FromDto(WtDto d) => new()
    {
        Path = d.Path, Branch = d.Branch, HeadCommitId = Oid(d.HeadSha),
        IsMainWorktree = d.IsMainWorktree, IsLocked = d.IsLocked, LockReason = d.LockReason, IsPrunable = d.IsPrunable,
    };

    // ── file shapes ──

    private interface IVersioned { int Version { get; } }

    private sealed class RefsFile : IVersioned
    {
        public int Version { get; set; }
        public List<BranchDto> Branches { get; set; } = [];
        public List<TagDto> Tags { get; set; } = [];
        public List<RemoteDto> Remotes { get; set; } = [];
    }

    private sealed class PowerFile : IVersioned
    {
        public int Version { get; set; }
        public List<StashDto>? Stashes { get; set; }
        public List<SubDto>? Submodules { get; set; }
        public List<WtDto>? Worktrees { get; set; }
    }

    private sealed class BranchDto
    {
        public string Name { get; set; } = "";
        public string CanonicalName { get; set; } = "";
        public bool IsRemote { get; set; }
        public bool IsCurrent { get; set; }
        public string? TipSha { get; set; }
        public string? RemoteName { get; set; }
        public string? UpstreamName { get; set; }
    }

    private sealed class TagDto
    {
        public string Name { get; set; } = "";
        public string TargetSha { get; set; } = "";
        public bool IsAnnotated { get; set; }
        public string? Message { get; set; }
        public string? TaggerName { get; set; }
        public DateTimeOffset? TaggerWhen { get; set; }
    }

    private sealed class RemoteDto
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string? PushUrl { get; set; }
    }

    private sealed class StashDto
    {
        public int Index { get; set; }
        public string CommitSha { get; set; } = "";
        public string Message { get; set; } = "";
        public string AuthorName { get; set; } = "";
        public string AuthorEmail { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public string BranchName { get; set; } = "";
        public int FileCount { get; set; }
        public bool IncludesUntracked { get; set; }
    }

    private sealed class SubDto
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Url { get; set; } = "";
        public string? HeadSha { get; set; }
        public string? RegisteredSha { get; set; }
        public int Status { get; set; }
        public string? BranchTracking { get; set; }
    }

    private sealed class WtDto
    {
        public string Path { get; set; } = "";
        public string Branch { get; set; } = "";
        public string? HeadSha { get; set; }
        public bool IsMainWorktree { get; set; }
        public bool IsLocked { get; set; }
        public string? LockReason { get; set; }
        public bool IsPrunable { get; set; }
    }
}
