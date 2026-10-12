namespace Historia.ViewModels;

/// <summary>
/// One issue-autolink rule row. The pattern and the URL save as they change; the view binds them
/// to update when the box loses focus.
/// </summary>
public sealed partial class IssueAutolinkRuleViewModel : ObservableObject
{
    private readonly Action<IssueAutolinkRuleViewModel, Func<IssueAutolinkRule, IssueAutolinkRule>> _update;
    private readonly Action<IssueAutolinkRuleViewModel> _remove;
    private readonly bool _ready;

    public IssueAutolinkRuleViewModel(
        IssueAutolinkRule rule,
        Action<IssueAutolinkRuleViewModel, Func<IssueAutolinkRule, IssueAutolinkRule>> update,
        Action<IssueAutolinkRuleViewModel> remove)
    {
        _update = update;
        _remove = remove;
        Pattern = rule.Pattern;
        UrlTemplate = rule.UrlTemplate;
        _ready = true;
    }

    [ObservableProperty]
    public partial string Pattern { get; set; }

    [ObservableProperty]
    public partial string UrlTemplate { get; set; }

    /// <summary>True when the row already shows <paramref name="rule"/>.</summary>
    public bool Shows(IssueAutolinkRule rule) => Pattern == rule.Pattern && UrlTemplate == rule.UrlTemplate;

    partial void OnPatternChanged(string value)
    {
        if (_ready)
            _update(this, r => r with { Pattern = value ?? "" });
    }

    partial void OnUrlTemplateChanged(string value)
    {
        if (_ready)
            _update(this, r => r with { UrlTemplate = value ?? "" });
    }

    [RelayCommand]
    private void Remove() => _remove(this);
}
