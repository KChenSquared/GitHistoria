namespace Historia.Views;

/// <summary>
/// Application singleton managing the lifetime of ConflictResolverView instances per RepositoryContext.
/// Prevents resolver disposal and unsaved work loss during tab switching and navigation.
/// </summary>
public sealed class ConflictResolverHost : IDisposable
{
    private readonly IServiceProvider _sp;
    private readonly IWorkspaceService? _workspace;
    private readonly Dictionary<RepositoryContext, ConflictResolverView> _resolvers = [];
    private readonly object _lock = new();
    private bool _disposed;

    public ConflictResolverHost(IServiceProvider sp)
    {
        _sp = sp;
        _workspace = sp.GetService(typeof(IWorkspaceService)) as IWorkspaceService;
        if (_workspace != null)
        {
            _workspace.OpenRepositoriesChanged += OnOpenRepositoriesChanged;
        }
    }

    /// <summary>The resolver for <paramref name="ctx"/>, created on first use; null for a tab that
    /// is closed. A posted content update for a tab that has just closed can arrive after the
    /// host cleaned up, and creating a resolver then would keep a view (and its file work) alive
    /// for a repository nobody can reach.</summary>
    public ConflictResolverView? GetOrCreate(RepositoryContext ctx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(ctx);

        if (ctx.Lifetime.IsCancellationRequested
            || (_workspace is not null && !_workspace.OpenRepositories.Contains(ctx)))
        {
            return null;
        }

        lock (_lock)
        {
            if (_resolvers.TryGetValue(ctx, out var existing))
            {
                return existing;
            }

            var created = new ConflictResolverView(_sp, ctx);
            _resolvers[ctx] = created;
            return created;
        }
    }

    public ConflictResolverView? TryGet(RepositoryContext ctx)
    {
        if (ctx == null)
        {
            return null;
        }

        lock (_lock)
        {
            return _resolvers.TryGetValue(ctx, out var existing) ? existing : null;
        }
    }

    public void Release(RepositoryContext ctx)
    {
        if (ctx == null)
        {
            return;
        }

        ConflictResolverView? view = null;
        lock (_lock)
        {
            if (_resolvers.Remove(ctx, out var existing))
            {
                view = existing;
            }
        }

        if (view != null)
        {
            if (view.IsBusy)
            {
                EventHandler? handler = null;
                handler = (s, e) =>
                {
                    if (!view.IsBusy)
                    {
                        view.BusyChanged -= handler;
                        view.Dispose();
                    }
                };
                view.BusyChanged += handler;
            }
            else
            {
                view.Dispose();
            }
        }
    }

    private void OnOpenRepositoriesChanged(object? sender, OpenRepositoriesChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed || _workspace == null)
            {
                return;
            }

            var openContexts = new HashSet<RepositoryContext>(_workspace.OpenRepositories);
            List<RepositoryContext> toRemove = [];

            lock (_lock)
            {
                foreach (var ctx in _resolvers.Keys)
                {
                    if (!openContexts.Contains(ctx))
                    {
                        toRemove.Add(ctx);
                    }
                }
            }

            foreach (var ctx in toRemove)
            {
                Release(ctx);
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_workspace != null)
        {
            _workspace.OpenRepositoriesChanged -= OnOpenRepositoriesChanged;
        }

        List<ConflictResolverView> views;
        lock (_lock)
        {
            views = [.. _resolvers.Values];
            _resolvers.Clear();
        }

        foreach (var view in views)
        {
            if (view.IsBusy)
            {
                EventHandler? handler = null;
                handler = (s, e) =>
                {
                    if (!view.IsBusy)
                    {
                        view.BusyChanged -= handler;
                        view.Dispose();
                    }
                };
                view.BusyChanged += handler;
            }
            else
            {
                view.Dispose();
            }
        }
    }
}
