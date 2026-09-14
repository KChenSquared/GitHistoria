namespace Historia.ScenarioLab.Views;

public partial class SettingsWindow : Window
{
    public LabSettings Settings { get; private set; }

    public SettingsWindow() : this(LabSettingsStore.Load())
    {
    }

    public SettingsWindow(LabSettings current)
    {
        InitializeComponent();
        Settings = current;

        Local1Box.Text = current.Local1;
        Local2Box.Text = current.Local2;
        RemotesBox.Text = current.Remotes;

        BrowseLocal1Button.Click += async (_, _) => await BrowseFolderAsync(Local1Box, "Select Local 1 Folder");
        BrowseLocal2Button.Click += async (_, _) => await BrowseFolderAsync(Local2Box, "Select Local 2 Folder");
        BrowseRemotesButton.Click += async (_, _) => await BrowseFolderAsync(RemotesBox, "Select Remotes Folder");

        Local1Box.TextChanged += (_, _) => ClearErrors();
        Local2Box.TextChanged += (_, _) => ClearErrors();
        RemotesBox.TextChanged += (_, _) => ClearErrors();

        CancelButton.Click += (_, _) => Close(false);
        SaveButton.Click += (_, _) => TrySave();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(false);
                e.Handled = true;
            }
        };
    }

    private void ClearErrors()
    {
        Local1ErrorText.IsVisible = false;
        Local1ErrorText.Text = string.Empty;
        Local2ErrorText.IsVisible = false;
        Local2ErrorText.Text = string.Empty;
        RemotesErrorText.IsVisible = false;
        RemotesErrorText.Text = string.Empty;
    }

    private async Task BrowseFolderAsync(TextBox targetBox, string title)
    {
        if (StorageProvider is not { CanOpen: true } sp)
            return;

        IStorageFolder? startFolder = null;
        var currentText = targetBox.Text?.Trim();
        if (!string.IsNullOrEmpty(currentText) && Directory.Exists(currentText))
        {
            try
            {
                startFolder = await sp.TryGetFolderFromPathAsync(currentText);
            }
            catch
            {
                // Fall back to default
            }
        }

        var picks = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = startFolder
        });

        if (picks.Count > 0)
        {
            var localPath = picks[0].TryGetLocalPath();
            if (!string.IsNullOrEmpty(localPath))
            {
                targetBox.Text = localPath;
            }
        }
    }

    private void TrySave()
    {
        ClearErrors();

        var local1 = Local1Box.Text?.Trim() ?? string.Empty;
        var local2 = Local2Box.Text?.Trim() ?? string.Empty;
        var remotes = RemotesBox.Text?.Trim() ?? string.Empty;

        var candidate = new LabSettings(local1, local2, remotes);
        var errors = candidate.Validate(LabPaths.RepoRoot);

        if (errors.Count > 0)
        {
            if (errors.TryGetValue("Local1", out var err1))
            {
                Local1ErrorText.Text = err1;
                Local1ErrorText.IsVisible = true;
            }

            if (errors.TryGetValue("Local2", out var err2))
            {
                Local2ErrorText.Text = err2;
                Local2ErrorText.IsVisible = true;
            }

            if (errors.TryGetValue("Remotes", out var err3))
            {
                RemotesErrorText.Text = err3;
                RemotesErrorText.IsVisible = true;
            }

            return;
        }

        // Valid: create missing folders on Save per requirements
        try
        {
            if (!Directory.Exists(candidate.Local1)) Directory.CreateDirectory(candidate.Local1);
        }
        catch (Exception ex)
        {
            Local1ErrorText.Text = $"Could not create folder: {ex.Message}";
            Local1ErrorText.IsVisible = true;
            return;
        }

        try
        {
            if (!Directory.Exists(candidate.Local2)) Directory.CreateDirectory(candidate.Local2);
        }
        catch (Exception ex)
        {
            Local2ErrorText.Text = $"Could not create folder: {ex.Message}";
            Local2ErrorText.IsVisible = true;
            return;
        }

        try
        {
            if (!Directory.Exists(candidate.Remotes)) Directory.CreateDirectory(candidate.Remotes);
        }
        catch (Exception ex)
        {
            RemotesErrorText.Text = $"Could not create folder: {ex.Message}";
            RemotesErrorText.IsVisible = true;
            return;
        }

        LabSettingsStore.Save(candidate);
        Settings = candidate;
        Close(true);
    }
}
