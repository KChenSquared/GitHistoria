namespace Historia.ViewModels;

/// <summary>
/// A numeric setting field. <see cref="Value"/> is what the field shows and changes as the user
/// types or spins it; nothing is saved until <see cref="CommitAsync"/> runs, which the view calls
/// when the field loses focus or Enter is pressed. A commit saves only a changed value, clamped
/// to the setting's range, and a setting with a confirmation saves only when the user accepts it.
/// </summary>
public sealed partial class NumericSettingViewModel : ObservableObject
{
    private readonly Func<int, int> _clamp;
    private readonly Func<int, Task> _save;
    private readonly Func<int, Task<bool>>? _confirm;
    private int _committed;
    private bool _confirming;

    /// <param name="clamp">Maps a typed value into the setting's valid range.</param>
    /// <param name="save">Writes an accepted value to the settings.</param>
    /// <param name="confirm">Asks the user before a changed value is saved; null saves without asking.</param>
    public NumericSettingViewModel(
        int minimum,
        int maximum,
        int increment,
        Func<int, int> clamp,
        Func<int, Task> save,
        Func<int, Task<bool>>? confirm = null)
    {
        Minimum = minimum;
        Maximum = maximum;
        Increment = increment;
        _clamp = clamp;
        _save = save;
        _confirm = confirm;
    }

    public int Minimum { get; }

    public int Maximum { get; }

    public int Increment { get; }

    /// <summary>What the field shows, which may differ from the saved value until it is committed.</summary>
    [ObservableProperty]
    public partial int Value { get; set; }

    /// <summary>The saved value.</summary>
    public int Committed => _committed;

    /// <summary>Follows the saved value. A pending edit survives a refresh that brings nothing new.</summary>
    public void Sync(int saved)
    {
        if (saved == _committed)
            return;
        _committed = saved;
        Value = saved;
    }

    /// <summary>
    /// Saves <see cref="Value"/> when its clamped form differs from the saved value; the field is
    /// then reset to what was kept. <paramref name="beforeConfirm"/> runs once a confirmation is
    /// about to open and <see cref="Value"/> is read again afterwards. While a confirmation is open
    /// another commit does nothing, because the dialog taking focus commits the field a second time.
    /// </summary>
    public async Task CommitAsync(Func<Task>? beforeConfirm = null)
    {
        if (_confirming)
            return;

        var next = _clamp(Value);
        if (next == _committed)
        {
            Value = _committed;
            return;
        }

        if (_confirm is null)
        {
            await SaveAsync(next);
            return;
        }

        _confirming = true;
        try
        {
            if (beforeConfirm is not null)
            {
                await beforeConfirm();
                next = _clamp(Value);
                if (next == _committed)
                {
                    Value = _committed;
                    return;
                }
            }

            if (!await _confirm(next))
            {
                Value = _committed;
                return;
            }

            await SaveAsync(next);
        }
        finally
        {
            _confirming = false;
        }
    }

    private Task SaveAsync(int next)
    {
        _committed = next;
        Value = next;
        return _save(next);
    }
}
