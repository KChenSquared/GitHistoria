namespace Historia.ViewModels;

/// <summary>
/// One row of the rebase todo list: the commit, the action chosen for it, and the buttons that
/// move it up or down. The host owns the order and tells the row whether it can still move.
/// </summary>
public sealed partial class RebaseTodoItemViewModel : ObservableObject
{
    private readonly RebaseEntry _entry;
    private readonly Action<RebaseTodoItemViewModel, int> _move;

    public RebaseTodoItemViewModel(RebaseEntry entry, Action<RebaseTodoItemViewModel, int> move)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(move);
        _entry = entry;
        _move = move;
        Action = entry.Action;
    }

    /// <summary>Every action a row can take, in the order the picker lists them.</summary>
    public static IReadOnlyList<RebaseAction> AllActions { get; } = Enum.GetValues<RebaseAction>();

    public IReadOnlyList<RebaseAction> Actions => AllActions;

    public string ShortId => _entry.CommitId.ToShortString(8);

    public string Message => _entry.MessageShort;

    /// <summary>What the rebase does with this commit.</summary>
    [ObservableProperty]
    public partial RebaseAction Action { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    public partial bool CanMoveUp { get; internal set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    public partial bool CanMoveDown { get; internal set; }

    /// <summary>The todo entry with the action chosen so far.</summary>
    public RebaseEntry Entry => _entry with { Action = Action };

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => _move(this, -1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => _move(this, 1);
}
