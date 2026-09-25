namespace Historia.Services.UI;

/// <inheritdoc cref="IUnsavedWorkRegistry"/>
public sealed class UnsavedWorkRegistry : IUnsavedWorkRegistry
{
    private readonly object _gate = new();
    private readonly List<IUnsavedWork> _items = [];

    public IDisposable Register(IUnsavedWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            _items.Add(work);
        }

        return new UnregisterHandle(this, work);
    }

    public IReadOnlyList<IUnsavedWork> For(RepositoryContext? ctx)
    {
        lock (_gate)
        {
            if (ctx is null)
                return _items.ToArray();

            return _items.Where(w => ReferenceEquals(w.Context, ctx)).ToArray();
        }
    }

    private void Unregister(IUnsavedWork work)
    {
        lock (_gate)
        {
            _items.Remove(work);
        }
    }

    private sealed class UnregisterHandle(UnsavedWorkRegistry registry, IUnsavedWork work) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                registry.Unregister(work);
        }
    }
}
