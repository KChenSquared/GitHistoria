namespace Historia.Views;

/// <summary>The clickable header of a collapsible settings section: title, an optional help
/// badge and a chevron. Pressing it toggles the section; pressing the badge does not.</summary>
public sealed partial class SettingsSectionHeader : UserControl
{
    public SettingsSectionHeader()
    {
        InitializeComponent();
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<HelpHint>(includeSelf: true) is not null)
            return;

        (DataContext as SettingsSectionViewModel)?.ToggleCommand.Execute(null);
    }
}
