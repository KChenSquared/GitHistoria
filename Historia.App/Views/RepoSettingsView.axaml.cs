namespace Historia.Views;

/// <summary>Per-repository settings. The view is markup bound to a <see cref="RepoSettingsViewModel"/>;
/// the code here is view mechanics: the identity menu flyout and the column-row drag reorder with
/// its ghost, which previews and saves through the view model.</summary>
public sealed partial class RepoSettingsView : UserControl, IDisposable
{
    private const double DragThreshold = 4;

    private ColumnRowViewModel? _dragRow;
    private Border? _dragVisual;
    private bool _dragging;
    private Point _pressPos;
    private Point _grabOffset;
    private double[] _slotMidpoints = [];
    private AppBandDragGhost? _ghost;

    public RepoSettingsView(IServiceProvider sp)
    {
        InitializeComponent();
        ViewModel = ActivatorUtilities.CreateInstance<RepoSettingsViewModel>(sp, new UiDispatcher());
        DataContext = ViewModel;

        ColumnList.AddHandler(PointerPressedEvent, OnColumnsPointerPressed, RoutingStrategies.Bubble);
        Root.AddHandler(PointerMovedEvent, OnRowsPointerMoved, RoutingStrategies.Bubble);
        Root.AddHandler(PointerReleasedEvent, OnRowsPointerReleased, RoutingStrategies.Bubble);
        Root.PointerCaptureLost += OnRowsCaptureLost;
    }

    internal RepoSettingsViewModel ViewModel { get; }

    private void OnProfileButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor)
            return;

        var items = new List<Control>();
        foreach (var item in ViewModel.BuildProfileMenu())
        {
            if (item.IsSeparator)
            {
                items.Add(new Separator());
                continue;
            }

            items.Add(new MenuItem
            {
                Header = item.Header,
                Icon = item.IsChecked ? new TextBlock { Text = "✓" } : null,
                Command = item.Command,
                CommandParameter = item.CommandParameter,
            });
        }

        new MenuFlyout { ItemsSource = items }.ShowAt(anchor);
    }

    // ── column-row drag reorder (mirrors the app-band tab reorder: live preview + ghost) ──
    private static Border? AncestorWithClass(Visual? start, string cssClass)
        => start?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains(cssClass));

    private void OnColumnsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (AncestorWithClass(e.Source as Visual, "column-handle") is null)
            return;
        if (AncestorWithClass(e.Source as Visual, "column-row") is not { DataContext: ColumnRowViewModel row } visual)
            return;

        _dragRow = row;
        _dragVisual = visual;
        _pressPos = e.GetPosition(this);
        _grabOffset = e.GetPosition(visual);
        _dragging = false;

        // Every row has the same height, so the slot midpoints measured now stay valid while the
        // rows trade places during the preview.
        _slotMidpoints = ColumnList.ItemsPanelRoot is { } panel
            ? [.. panel.Children.Select(c => c.Bounds.Y + c.Bounds.Height / 2)]
            : [];
        e.Handled = true;
    }

    private void OnRowsPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragRow is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var here = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(here.Y - _pressPos.Y) < DragThreshold)
                return;
            _dragging = true;
            e.Pointer.Capture(Root);
            StartGhost(_dragVisual);
            _dragRow.IsDragging = true;
        }

        if (ColumnList.ItemsPanelRoot is { } panel)
            ReorderTo(e.GetPosition(panel).Y);
        _ghost?.MoveTo(new Point(here.X - _grabOffset.X, here.Y - _grabOffset.Y));
    }

    private void OnRowsPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragRow is null)
            return;
        var wasDragging = _dragging;
        var dropped = _dragRow;
        _dragRow = null;
        _dragVisual = null;
        _dragging = false;
        StopGhost();
        dropped.IsDragging = false;
        e.Pointer.Capture(null);

        if (!wasDragging)
            return;
        e.Handled = true;
        _ = ViewModel.CommitColumnOrderAsync();
    }

    private void OnRowsCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        var wasDragging = _dragging;
        _dragRow = null;
        _dragVisual = null;
        _dragging = false;
        StopGhost();
        if (wasDragging)
            ViewModel.CancelColumnReorder();
    }

    /// <summary>Live preview: shuffle the dragged row into the slot under the cursor, like the
    /// app-band tab reorder.</summary>
    private void ReorderTo(double y)
    {
        if (_dragRow is null || _slotMidpoints.Length == 0)
            return;

        var target = 0;
        for (var i = 0; i < _slotMidpoints.Length; i++)
        {
            if (y < _slotMidpoints[i])
                break;
            target = i + 1;
        }
        ViewModel.PreviewMoveColumn(_dragRow, Math.Min(target, _slotMidpoints.Length - 1));
    }

    private void StartGhost(Border? row)
    {
        if (row is null || row.Bounds.Width <= 0 || row.Bounds.Height <= 0)
            return;
        var px = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(row.Bounds.Width)),
            Math.Max(1, (int)Math.Ceiling(row.Bounds.Height)));
        var rtb = new RenderTargetBitmap(px);
        rtb.Render(row);
        _ghost = new AppBandDragGhost(rtb, row.Bounds.Size);
        Overlay.Children.Add(_ghost);
    }

    private void StopGhost()
    {
        if (_ghost is not null)
            Overlay.Children.Remove(_ghost);
        _ghost?.Dispose();
        _ghost = null;
    }

    public void Dispose() => ViewModel.Dispose();
}
