namespace Historia.ScenarioLab;

public partial class MainWindow : Window
{
    private LabSettings _currentSettings;
    private bool _isClosingConfirmed;
    private readonly DispatcherTimer _editorDebounceTimer;
    private bool _isSyncingFromBoxes;
    private bool _isSyncingFromEditor;

    public MainWindow()
    {
        InitializeComponent();
        _currentSettings = LabSettingsStore.Load();

        SettingsButton.Click += async (_, _) => await OpenSettingsAsync();
        SaveButton.Click += async (_, _) => await SaveCurrentScriptAsync();

        NavPanel.ScriptSelected += async path => await OnScriptSelectedAsync(path);
        NavPanel.ScriptRenamed += (oldPath, newPath) => OnNodeRenamed(oldPath, newPath);
        NavPanel.NodeDeleted += path => OnNodeDeleted(path);

        EditorPanel.DirtyChanged += _ => UpdateTitle();

        FileInfo.HeaderEdited += OnHeaderEditedFromBoxes;

        _editorDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _editorDebounceTimer.Tick += (_, _) =>
        {
            _editorDebounceTimer.Stop();
            SyncBoxesFromEditor();
        };

        EditorPanel.TextEditor.TextChanged += (_, _) =>
        {
            if (_isSyncingFromBoxes) return;
            _editorDebounceTimer.Stop();
            _editorDebounceTimer.Start();
        };

        Opened += async (_, _) =>
        {
            UpdateSettingsStatus();
            if (!_currentSettings.IsComplete || _currentSettings.Validate(LabPaths.RepoRoot).Count > 0)
            {
                await OpenSettingsAsync();
            }
        };

        Closing += async (_, e) =>
        {
            if (_isClosingConfirmed) return;

            if (EditorPanel.IsDirty && !string.IsNullOrEmpty(EditorPanel.CurrentPath))
            {
                e.Cancel = true;
                var fileName = Path.GetFileName(EditorPanel.CurrentPath);
                var choice = await LabDialogs.SaveDiscardCancelAsync(this, fileName);

                if (choice == SaveDiscardCancelResult.Save)
                {
                    await EditorPanel.SaveAsync();
                    _isClosingConfirmed = true;
                    Close();
                }
                else if (choice == SaveDiscardCancelResult.Discard)
                {
                    _isClosingConfirmed = true;
                    Close();
                }
            }
        };

        UpdateSettingsStatus();
        UpdateTitle();
        FileInfo.SetDisabled();
    }

    private void OnHeaderEditedFromBoxes(string shortText, string longText)
    {
        if (_isSyncingFromEditor || string.IsNullOrEmpty(EditorPanel.CurrentPath))
            return;

        _isSyncingFromBoxes = true;
        try
        {
            var doc = EditorPanel.TextEditor.Document;
            var text = doc.Text;
            var info = ScriptHeader.Parse(text);
            var rendered = ScriptHeader.Render(shortText, longText);

            doc.BeginUpdate();
            doc.Replace(info.Offset, info.Length, rendered);
            doc.EndUpdate();
        }
        finally
        {
            _isSyncingFromBoxes = false;
        }
    }

    private void SyncBoxesFromEditor()
    {
        if (string.IsNullOrEmpty(EditorPanel.CurrentPath))
        {
            FileInfo.SetDisabled();
            return;
        }

        _isSyncingFromEditor = true;
        try
        {
            var text = EditorPanel.TextEditor.Text ?? string.Empty;
            var info = ScriptHeader.Parse(text);
            FileInfo.SetScriptHeader(info.Short, info.Long);
        }
        finally
        {
            _isSyncingFromEditor = false;
        }
    }

    private void UpdateTitle()
    {
        var title = "Historia ScenarioLab";
        if (!string.IsNullOrEmpty(EditorPanel.CurrentPath))
        {
            var fileName = Path.GetFileName(EditorPanel.CurrentPath);
            title = EditorPanel.IsDirty
                ? $"Historia ScenarioLab — {fileName} *"
                : $"Historia ScenarioLab — {fileName}";
        }
        Title = title;
    }

    private async Task OnScriptSelectedAsync(string newPath)
    {
        if (string.Equals(newPath, EditorPanel.CurrentPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (EditorPanel.IsDirty && !string.IsNullOrEmpty(EditorPanel.CurrentPath))
        {
            var fileName = Path.GetFileName(EditorPanel.CurrentPath);
            var choice = await LabDialogs.SaveDiscardCancelAsync(this, fileName);

            if (choice == SaveDiscardCancelResult.Save)
            {
                await EditorPanel.SaveAsync();
                LoadScript(newPath);
            }
            else if (choice == SaveDiscardCancelResult.Discard)
            {
                LoadScript(newPath);
            }
            else
            {
                // Cancel: restore selection in the nav panel
                NavPanel.SelectPath(EditorPanel.CurrentPath);
            }
        }
        else
        {
            LoadScript(newPath);
        }
    }

    private void OnNodeRenamed(string oldPath, string newPath)
    {
        if (EditorPanel.CurrentPath is null) return;

        if (string.Equals(EditorPanel.CurrentPath, oldPath, StringComparison.OrdinalIgnoreCase))
        {
            EditorPanel.RetargetPath(newPath);
            StatusFilePathText.Text = newPath;
            UpdateTitle();
        }
        else if (EditorPanel.CurrentPath.StartsWith(oldPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var sub = EditorPanel.CurrentPath.Substring(oldPath.Length);
            var updatedPath = newPath + sub;
            EditorPanel.RetargetPath(updatedPath);
            StatusFilePathText.Text = updatedPath;
            UpdateTitle();
        }
    }

    private void OnNodeDeleted(string deletedPath)
    {
        if (EditorPanel.CurrentPath is null) return;

        if (string.Equals(EditorPanel.CurrentPath, deletedPath, StringComparison.OrdinalIgnoreCase) ||
            EditorPanel.CurrentPath.StartsWith(deletedPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            EditorPanel.Close();
            FileInfo.SetDisabled();
            StatusFilePathText.Text = "No script open";
            UpdateTitle();
        }
    }

    private void LoadScript(string path)
    {
        EditorPanel.Load(path);
        StatusFilePathText.Text = path;
        UpdateTitle();
        SyncBoxesFromEditor();
    }

    private async Task SaveCurrentScriptAsync()
    {
        await EditorPanel.SaveAsync();
        UpdateTitle();
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
