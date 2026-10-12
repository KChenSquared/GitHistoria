namespace Historia.ViewModels;

/// <summary>
/// One row of the graph-column editor: a column's kind, whether it is shown, and whether the row
/// is being dragged. Turning <see cref="Visible"/> on or off asks the owning
/// <see cref="RepoSettingsViewModel"/> to apply it; a change the layout rules refuse is undone.
/// </summary>
public sealed partial class ColumnRowViewModel : ObservableObject
{
    private readonly RepoSettingsViewModel _owner;
    private bool _restoring;

    internal ColumnRowViewModel(RepoSettingsViewModel owner, GraphColumn column)
    {
        _owner = owner;
        Kind = column.Kind;
        _restoring = true;
        Visible = column.Visible;
        _restoring = false;
    }

    public GraphColumnKind Kind { get; }

    /// <summary>The column's caption.</summary>
    public string Name => Kind.ToString();

    /// <summary>True while the column is shown in the graph.</summary>
    [ObservableProperty]
    public partial bool Visible { get; set; }

    /// <summary>True while the row is lifted by a reorder drag.</summary>
    [ObservableProperty]
    public partial bool IsDragging { get; set; }

    partial void OnVisibleChanged(bool value)
    {
        if (!_restoring)
            _owner.ApplyColumnVisibility(this, value);
    }

    /// <summary>Sets <see cref="Visible"/> without asking the owner to apply it.</summary>
    internal void RestoreVisible(bool visible)
    {
        _restoring = true;
        try { Visible = visible; }
        finally { _restoring = false; }
    }
}
