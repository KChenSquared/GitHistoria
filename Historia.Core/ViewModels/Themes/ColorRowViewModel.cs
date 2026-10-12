namespace Historia.ViewModels;

/// <summary>
/// One colour override in the theme editor: a palette token or a graph lane. <see cref="Text"/> is
/// what the user typed. Empty means "inherit the base" and a parseable hex is the override
/// (<see cref="Value"/>); anything else is invalid, flagged inline and not persisted.
/// </summary>
public sealed partial class ColorRowViewModel : ObservableObject
{
    private readonly Action? _changed;

    public ColorRowViewModel(string label, string baseHex, string? current, Action? changed = null)
    {
        Label = label;
        BaseHex = baseHex;
        SwatchHex = baseHex;
        Text = current ?? "";
        _changed = changed;
    }

    public string Label { get; }

    /// <summary>The base theme's colour for this row, shown as the empty box's watermark.</summary>
    [ObservableProperty]
    public partial string BaseHex { get; private set; }

    /// <summary>The hex box's content, two-way.</summary>
    [ObservableProperty]
    public partial string? Text { get; set; }

    /// <summary>The trimmed override when <see cref="Text"/> parses, otherwise null.</summary>
    public string? Value { get; private set; }

    /// <summary>The colour the swatch shows: the override when valid, else the base colour. An
    /// invalid entry leaves the swatch as it was.</summary>
    [ObservableProperty]
    public partial string SwatchHex { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ErrorText))]
    public partial bool IsInvalid { get; private set; }

    public string ErrorText => IsInvalid ? "invalid" : "";

    /// <summary>Follows a change of the draft's base theme: the watermark and, while the row
    /// inherits, the swatch.</summary>
    public void SetBase(string baseHex)
    {
        BaseHex = baseHex;
        if (!IsInvalid && Value is null)
            SwatchHex = baseHex;
    }

    partial void OnTextChanged(string? value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (trimmed is null)
        {
            Value = null;
            IsInvalid = false;
            SwatchHex = BaseHex;
        }
        else if (ThemesViewModel.TryParseHex(trimmed, out _))
        {
            Value = trimmed;
            IsInvalid = false;
            SwatchHex = trimmed;
        }
        else
        {
            Value = null;
            IsInvalid = true;
        }

        _changed?.Invoke();
    }
}
