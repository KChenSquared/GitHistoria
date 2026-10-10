namespace Historia.Controls;

/// <summary>
/// Base code editor control providing synchronized scrolling and dynamic syntax highlighting.
/// </summary>
public abstract class ScrollSyncCodeEditor : CodeEditor
{
    private IDisposable? _syntaxSubscription;
    private string? _filePath;
    private ScrollViewer? _scrollViewer;
    private Vector _scrollOffset = Vector.Zero;

    public IServiceProvider? ServicesProvider { get; set; }

    public string? FilePath
    {
        get => _filePath;
        set
        {
            _filePath = value;
            if (_syntaxSubscription != null)
            {
                DetachSyntaxHighlighting();
                AttachSyntaxHighlighting();
            }
        }
    }

    public event EventHandler<Vector>? ScrollOffsetChanged;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        UnhookScrollViewer();
        _scrollViewer = e.NameScope.Find<ScrollViewer>("PART_ScrollViewer") ?? this.FindDescendantOfType<ScrollViewer>();
        if (_scrollViewer != null)
        {
            _scrollViewer.ScrollChanged += OnScrollViewerScrollChanged;
            if (_scrollOffset != Vector.Zero)
            {
                _scrollViewer.Offset = _scrollOffset;
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachSyntaxHighlighting();
        if (_scrollViewer == null)
        {
            HookScrollViewer();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnhookScrollViewer();
        DetachSyntaxHighlighting();
        base.OnDetachedFromVisualTree(e);
    }

    private void HookScrollViewer()
    {
        _scrollViewer = this.FindDescendantOfType<ScrollViewer>();
        if (_scrollViewer != null)
        {
            _scrollViewer.ScrollChanged += OnScrollViewerScrollChanged;
            if (_scrollOffset != Vector.Zero)
            {
                _scrollViewer.Offset = _scrollOffset;
            }
        }
    }

    private void UnhookScrollViewer()
    {
        if (_scrollViewer != null)
        {
            _scrollViewer.ScrollChanged -= OnScrollViewerScrollChanged;
            _scrollViewer = null;
        }
    }

    private void OnScrollViewerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scrollViewer != null)
        {
            _scrollOffset = _scrollViewer.Offset;
            ScrollOffsetChanged?.Invoke(this, _scrollOffset);
            OnScrollOffsetApplied();
        }
    }

    public Vector GetScrollOffset() => _scrollViewer?.Offset ?? _scrollOffset;

    public void SetScrollOffset(Vector offset)
    {
        _scrollOffset = offset;
        if (_scrollViewer != null)
        {
            _scrollViewer.Offset = offset;
        }
        else
        {
            ScrollToHorizontalOffset(offset.X);
            ScrollToVerticalOffset(offset.Y);
        }
        ScrollOffsetChanged?.Invoke(this, offset);
        OnScrollOffsetApplied();
    }

    protected virtual void OnScrollOffsetApplied() { }

    private void AttachSyntaxHighlighting()
    {
        if (_syntaxSubscription != null)
        {
            return;
        }

        var host = ServicesProvider?.GetService(typeof(SyntaxThemeHost)) as SyntaxThemeHost
                   ?? (Application.Current as App)?.Services?.GetService(typeof(SyntaxThemeHost)) as SyntaxThemeHost;

        if (host != null)
        {
            try
            {
                _syntaxSubscription = host.Attach(this, FilePath);
            }
            catch
            {
                // Fall back to plain text
            }
        }
    }

    private void DetachSyntaxHighlighting()
    {
        _syntaxSubscription?.Dispose();
        _syntaxSubscription = null;
    }
}
