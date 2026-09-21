namespace Historia.Services.Watching;

/// <summary>
/// Debounced, gitignore-aware working-directory + <c>.git</c> watcher (§6.8.2). Filters
/// ignored paths before the debounce fires so a build cannot produce an event storm; falls
/// back to refresh-on-focus when the watcher cannot be set up or the tree is too large.
/// Owned by <see cref="RepositoryContext"/>.
/// </summary>
public sealed class FileWatcherService : IAsyncDisposable
{
    private const int DebounceMs = 150;
    private const int MaxWaitMs = 1000;
    private const int LargeTreeFileThreshold = 50_000;
    private const int MaxInternalBuffer = 64 * 1024;

    private readonly RepositoryContext _context;
    private readonly IRepositoryStateService _state;
    private readonly ISettingsService _settings;
    private readonly IWorkspaceService? _workspace;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _sync = new();
    private readonly HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Memoizes <see cref="IsBesideAProjectFile"/> per relative directory (see
    /// <see cref="IsLikelyIgnored"/>) so a build storm under one project's bin/obj pays the
    /// filesystem probe once rather than once per file event.</summary>
    private readonly ConcurrentDictionary<string, bool> _buildOutputParents = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _debounceTimer;
    private DateTimeOffset _firstPendingAt;
    private bool _refsTouched;
    private bool _forceFull;
    private volatile bool _disposed;

    public bool IsDegraded { get; private set; }
    public string? DegradedReason { get; private set; }

    public FileWatcherService(
        RepositoryContext context,
        IRepositoryStateService state,
        ISettingsService settings,
        IWorkspaceService? workspace = null)
    {
        _context = context;
        _state = state;
        _settings = settings;
        _workspace = workspace ?? context.Workspace;

        try
        {
            Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            EnterDegradedMode($"File watching is unavailable here ({ex.GetType().Name}). Using refresh-on-focus.");
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void Start()
    {
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        var gitDir = NormalizePath(_context.GitDirectory);
        if (Directory.Exists(gitDir))
        {
            AddWatcher(gitDir, includeSubdirectories: false, isGitDir: true);
            var refsDir = Path.Combine(gitDir, "refs");
            if (Directory.Exists(refsDir))
                AddWatcher(refsDir, includeSubdirectories: true, isGitDir: true);
        }

        if (_context.IsBare)
        {
            EnterDegradedMode("Bare repository — no working tree to watch.");
            return;
        }

        var workDir = NormalizePath(_context.RepositoryPath);
        if (Directory.Exists(workDir) && CountFilesCapped(workDir, LargeTreeFileThreshold) >= LargeTreeFileThreshold)
        {
            EnterDegradedMode("Repository working tree is very large. Watching .git only; working tree uses refresh-on-focus.");
            return;
        }

        if (Directory.Exists(workDir))
            AddWatcher(workDir, includeSubdirectories: true, isGitDir: false);
    }

    private void AddWatcher(string path, bool includeSubdirectories, bool isGitDir)
    {
        var watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = includeSubdirectories,
            InternalBufferSize = MaxInternalBuffer,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
        };

        watcher.Changed += (_, e) => OnRawEvent(e.FullPath, isGitDir);
        watcher.Created += (_, e) => OnRawEvent(e.FullPath, isGitDir);
        watcher.Deleted += (_, e) => OnRawEvent(e.FullPath, isGitDir);
        watcher.Renamed += (_, e) =>
        {
            OnRawEvent(e.FullPath, isGitDir);
            OnRawEvent(e.OldFullPath, isGitDir);
        };
        watcher.Error += OnWatcherError;

        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        if (_disposed)
            return;

        // Buffer overflow: discard pending state and force one full refresh — never ignored (§6.8.2).
        lock (_sync)
        {
            if (_disposed)
                return;
            _pendingPaths.Clear();
            _forceFull = true;
            _refsTouched = true;
            ScheduleLocked();
        }
    }

    private void OnRawEvent(string fullPath, bool isGitDir)
    {
        if (_disposed)
            return;

        // Historia's own Git operations force an explicit refresh on completion (§6.8.2).
        if (_context.IsSelfOperationInProgress)
            return;

        var normalizedFullPath = NormalizePath(fullPath);
        var gitDir = NormalizePath(_context.GitDirectory);
        var isUnderGit = isGitDir || (!string.IsNullOrEmpty(gitDir) && normalizedFullPath.StartsWith(gitDir, StringComparison.OrdinalIgnoreCase));

        if (isUnderGit)
        {
            var rel = Path.GetRelativePath(gitDir, normalizedFullPath).Replace('\\', '/');
            // Only a few .git entries matter; everything else (objects/**, logs/**) is noise.
            var meaningful = rel is "HEAD" or "index" or "packed-refs" or "MERGE_HEAD" or "REBASE_HEAD"
                                 or "CHERRY_PICK_HEAD" or "REVERT_HEAD" or "ORIG_HEAD"
                             || rel.StartsWith("refs/", StringComparison.Ordinal)
                             || rel.StartsWith("rebase-merge", StringComparison.Ordinal)
                             || rel.StartsWith("rebase-apply", StringComparison.Ordinal);
            if (!meaningful)
                return;

            lock (_sync)
            {
                _refsTouched = true;
                _pendingPaths.Add(rel);
                ScheduleLocked();
            }
            return;
        }

        // Working-tree path: drop ignored paths before the debounce fires (§6.8.2).
        var workDir = NormalizePath(_context.RepositoryPath);
        var relPath = Path.GetRelativePath(workDir, normalizedFullPath).Replace('\\', '/');
        if (relPath.StartsWith("../", StringComparison.Ordinal) || relPath == "..")
            return;

        if (IsLikelyIgnored(relPath))
            return;

        lock (_sync)
        {
            _pendingPaths.Add(relPath);
            ScheduleLocked();
        }
    }

    private void ScheduleLocked()
    {
        var timer = _debounceTimer;
        if (timer is null || _disposed)
            return;

        var now = DateTimeOffset.UtcNow;
        if (_pendingPaths.Count == 0 && !_forceFull)
            _firstPendingAt = now;
        else if (_firstPendingAt == default)
            _firstPendingAt = now;

        var elapsed = (now - _firstPendingAt).TotalMilliseconds;
        var dueIn = elapsed >= MaxWaitMs ? 0 : Math.Min(DebounceMs, MaxWaitMs - (int)elapsed);
        try
        {
            timer.Change(Math.Max(0, dueIn), Timeout.Infinite);
        }
        catch (ObjectDisposedException) { }
    }

    private void OnDebounceElapsed(object? _)
    {
        if (_disposed)
            return;

        bool refs;
        bool full;
        lock (_sync)
        {
            if (_disposed)
                return;

            if (_pendingPaths.Count == 0 && !_forceFull)
                return;
            refs = _refsTouched;
            full = _forceFull;
            _pendingPaths.Clear();
            _refsTouched = false;
            _forceFull = false;
            _firstPendingAt = default;
        }

        _ = PublishAsync(refs, full);
    }

    private async Task PublishAsync(bool refsTouched, bool full)
    {
        try
        {
            var isFull = full || refsTouched;
            var ws = _workspace ?? _context.Workspace;
            if (ws is not null)
            {
                await ws.RequestBackgroundRefreshAsync(_context, isFull, _context.Lifetime).ConfigureAwait(false);
            }
            else
            {
                if (isFull)
                    await _state.RefreshAllAsync(_context.Lifetime).ConfigureAwait(false);
                else
                    await _state.RefreshStatusAsync(_context.Lifetime).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Repo closing.
        }
        catch
        {
            // A refresh failure must not tear down the watcher.
        }
    }


    /// <summary>Internal for a direct test seam (Historia.Tests) — otherwise only called from
    /// <see cref="OnRawEvent"/>.</summary>
    internal bool IsLikelyIgnored(string relPath)
    {
        // Cheap synchronous pre-filter for the common build-output offenders only. The
        // authoritative .gitignore check is LibGit2Sharp's ignore rules, consulted lazily
        // during the status scan — never here: this runs on the FileSystemWatcher callback
        // thread, and blocking it on the per-repo async gate (a running walk can hold that
        // gate for seconds) stalls every later event and can starve the thread pool.
        //
        // node_modules/.vs/.git are near-certain vendor/tooling dirs, always skipped. bin/obj
        // are also skipped, but only where they actually look like build output — beside a
        // project file — so a repo that tracks a top-level bin/ of scripts (a common layout)
        // still gets its Changes panel updated when one of those tracked files is edited.
        var segments = relPath.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            if (seg is "node_modules" or ".vs" or ".git")
                return true;
            if (seg is "bin" or "obj" && IsBesideAProjectFile(segments, i))
                return true;
        }
        return false;
    }

    /// <summary>True when the directory containing <c>segments[binOrObjIndex]</c> holds a
    /// project file (<c>*.*proj</c> — csproj/fsproj/vbproj/…), i.e. <c>bin</c>/<c>obj</c> there
    /// really is that project's build output. Memoized per directory so a build storm — the
    /// exact case this pre-filter exists for — pays the one filesystem probe once, not once
    /// per event.</summary>
    private bool IsBesideAProjectFile(string[] segments, int binOrObjIndex)
    {
        var relDir = string.Join('/', segments.Take(binOrObjIndex));
        return _buildOutputParents.GetOrAdd(relDir, static (dir, workDir) =>
        {
            try
            {
                var full = string.IsNullOrEmpty(dir) ? workDir : Path.Combine(workDir, dir.Replace('/', Path.DirectorySeparatorChar));
                return Directory.Exists(full) && Directory.EnumerateFiles(full, "*.*proj").Any();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }, NormalizePath(_context.RepositoryPath));
    }

    private static int CountFilesCapped(string root, int cap)
    {
        var count = 0;
        try
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0 && count < cap)
            {
                var dir = stack.Pop();
                var name = Path.GetFileName(dir);
                if (name is "bin" or "obj" or "node_modules" or ".git" or ".vs")
                    continue;

                try
                {
                    count += Directory.EnumerateFiles(dir).Count();
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                        stack.Push(sub);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip unreadable directories.
                }
            }
        }
        catch
        {
            return 0;
        }

        return count;
    }

    private void EnterDegradedMode(string reason)
    {
        IsDegraded = true;
        DegradedReason = reason;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var w in _watchers)
        {
            try
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            catch
            {
                // ignore
            }
        }

        _watchers.Clear();
        lock (_sync)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
        return ValueTask.CompletedTask;
    }
}
