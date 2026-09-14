namespace Historia.ScenarioLab;

public partial class MainWindow : Window
{
    private LabSettings _currentSettings;

    public MainWindow()
    {
        InitializeComponent();
        _currentSettings = LabSettingsStore.Load();

        SettingsButton.Click += async (_, _) => await OpenSettingsAsync();
        NavPanel.ScriptSelected += path => StatusFilePathText.Text = path;

        Opened += async (_, _) =>
        {
            UpdateSettingsStatus();
            if (!_currentSettings.IsComplete || _currentSettings.Validate(LabPaths.RepoRoot).Count > 0)
            {
                await OpenSettingsAsync();
            }
        };

        UpdateSettingsStatus();
    }

    private void UpdateSettingsStatus()
    {
        var isValid = _currentSettings.IsComplete && _currentSettings.Validate(LabPaths.RepoRoot).Count == 0;
        if (isValid)
        {
            StatusSettingsText.Text = string.Empty;
        }
        else
        {
            StatusSettingsText.Text = "Settings incomplete";
        }
    }

    private async Task OpenSettingsAsync()
    {
        var dialog = new SettingsWindow(_currentSettings);
        var result = await dialog.ShowDialog<bool>(this);
        if (result)
        {
            _currentSettings = dialog.Settings;
            UpdateSettingsStatus();
        }
    }
}
