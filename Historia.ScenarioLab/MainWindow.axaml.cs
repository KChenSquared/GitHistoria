namespace Historia.ScenarioLab;

public partial class MainWindow : Window
{
    private LabSettings _currentSettings;
    private bool _isClosingConfirmed;
    private readonly DispatcherTimer _editorDebounceTimer;
    private bool _isSyncingFromBoxes;
    private bool _isSyncingFromEditor;

    private readonly ScriptRunner _runner = new();
    private CancellationTokenSource? _runCts;
    private bool _isRunning;

    public MainWindow()
    {
        InitializeComponent();
        _currentSettings = LabSettingsStore.Load();

        SettingsButton.Click += async (_, _) => await OpenSettingsAsync();
        SaveButton.Click += async (_, _) => await SaveCurrentScriptAsync();
        ExecuteButton.Click += async (_, _) => await OnExecuteOrStopClickAsync();
        PurgeLocalsButton.Click += async (_, _) => await PurgeLocalsAsync();
        PurgeRemotesButton.Click += async (_, _) => await PurgeRemotesAsync();

        NavPanel.ScriptSelected += async path => await OnScriptSelectedAsync(path);
        NavPanel.ScriptRenamed += (oldPath, newPath) => OnNodeRenamed(oldPath, newPath);
        NavPanel.NodeDeleted += path => OnNodeDeleted(path);

        EditorPanel.DirtyChanged += _ =>
        {
            UpdateTitle();
            UpdateUiState();
        };

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
            UpdateUiState();
            if (!_currentSettings.IsComplete || _currentSettings.Validate(LabPaths.RepoRoot).Count > 0)
            {
                await OpenSettingsAsync();
            }
        };

        Closing += async (_, e) =>
        {
            if (_isClosingConfirmed) return;

            if (_isRunning)
            {
                _runCts?.Cancel();
                _runner.Stop();
            }

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
        UpdateUiState();
        FileInfo.SetDisabled();
    }

    private void UpdateUiState()
    {
        var settingsValid = _currentSettings.IsComplete && _currentSettings.Validate(LabPaths.RepoRoot).Count == 0;
        var hasScript = !string.IsNullOrEmpty(EditorPanel.CurrentPath);

        if (_isRunning)
        {
            ExecuteButton.Content = "Stop";
            ExecuteButton.Classes.Clear();
            ExecuteButton.Classes.Add("danger");
            ExecuteButton.IsEnabled = true;

            SaveButton.IsEnabled = false;
            PurgeLocalsButton.IsEnabled = false;
            PurgeRemotesButton.IsEnabled = false;
            SettingsButton.IsEnabled = false;
            NavPanel.SetOperationsEnabled(false);
            EditorPanel.IsEnabled = true;
        }
        else
        {
            ExecuteButton.Content = "Execute";
            ExecuteButton.Classes.Clear();
            ExecuteButton.Classes.Add("primary");
            ExecuteButton.IsEnabled = settingsValid && hasScript;

            SaveButton.IsEnabled = hasScript;
            PurgeLocalsButton.IsEnabled = settingsValid;
            PurgeRemotesButton.IsEnabled = settingsValid;
            SettingsButton.IsEnabled = true;
            NavPanel.SetOperationsEnabled(true);
            EditorPanel.IsEnabled = true;
        }
    }

    private async Task OnExecuteOrStopClickAsync()
    {
        if (_isRunning)
        {
            _runCts?.Cancel();
            _runner.Stop();
            return;
        }

        if (string.IsNullOrEmpty(EditorPanel.CurrentPath))
        {
            LogOutput.AppendLine("No script open to execute.");
            return;
        }

        if (!_currentSettings.IsComplete || _currentSettings.Validate(LabPaths.RepoRoot).Count > 0)
        {
            LogOutput.AppendLine("Cannot execute: settings are incomplete.");
            return;
        }

        // 1. Save first
        await SaveCurrentScriptAsync();

        // 2. Counts for all 3 folders
        var c1 = FolderPurger.CountTopLevel(_currentSettings.Local1);
        var c2 = FolderPurger.CountTopLevel(_currentSettings.Local2);
        var c3 = FolderPurger.CountTopLevel(_currentSettings.Remotes);
        var total = c1 + c2 + c3;

        if (total > 0)
        {
            var scriptName = Path.GetFileName(EditorPanel.CurrentPath);
            var msg = $"Purge Local 1 ({c1}), Local 2 ({c2}), Remotes ({c3}) and run '{scriptName}'?";
            var confirmed = await LabDialogs.ConfirmAsync(this, "Execute Scenario", msg, confirmText: "Purge & Run", danger: true);
            if (!confirmed) return;
        }

        // 3. Purge all three folders
        LogOutput.Clear();
        LogOutput.AppendLine("Purging Local 1, Local 2, and Remotes before run...");

        var purgeFailed = false;
        await Task.Run(() =>
        {
            var r1 = FolderPurger.PurgeContents(_currentSettings.Local1, LabPaths.RepoRoot);
            var r2 = FolderPurger.PurgeContents(_currentSettings.Local2, LabPaths.RepoRoot);
            var r3 = FolderPurger.PurgeContents(_currentSettings.Remotes, LabPaths.RepoRoot);

            Dispatcher.UIThread.Post(() =>
            {
                if (r1.Failures.Count > 0 || r2.Failures.Count > 0 || r3.Failures.Count > 0)
                {
                    purgeFailed = true;
                    foreach (var f in r1.Failures) LogOutput.AppendLine($"  [FAIL Local 1] {f}");
                    foreach (var f in r2.Failures) LogOutput.AppendLine($"  [FAIL Local 2] {f}");
                    foreach (var f in r3.Failures) LogOutput.AppendLine($"  [FAIL Remotes] {f}");
                    LogOutput.AppendLine("[ERROR] Purge failed. Aborting execution.");
                }
                else
                {
                    LogOutput.AppendLine($"Purged Local 1 ({r1.DeletedCount}), Local 2 ({r2.DeletedCount}), Remotes ({r3.DeletedCount}).");
                }
            });
        });

        if (purgeFailed) return;

        // 4. Run
        _isRunning = true;
        UpdateUiState();
        _runCts = new CancellationTokenSource();

        LogOutput.AppendLine($"▶ {Path.GetFileName(EditorPanel.CurrentPath)}");

        ScriptRunResult res;
        try
        {
            var scriptPath = EditorPanel.CurrentPath;
            res = await _runner.RunAsync(
                scriptPath,
                _currentSettings,
                line => LogOutput.AppendLine(line),
                _runCts.Token);
        }
        finally
        {
            _isRunning = false;
            _runCts.Dispose();
            _runCts = null;
            UpdateUiState();
        }

        if (res.WasStopped)
        {
            LogOutput.AppendLine("stopped");
        }
        else
        {
            LogOutput.AppendLine($"exit {res.ExitCode} in {res.Duration.TotalSeconds:F1}s");
        }
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
            UpdateUiState();
        }
    }

    private void LoadScript(string path)
    {
        EditorPanel.Load(path);
        StatusFilePathText.Text = path;
        UpdateTitle();
        UpdateUiState();
        SyncBoxesFromEditor();
    }

    private async Task SaveCurrentScriptAsync()
    {
        await EditorPanel.SaveAsync();
        UpdateTitle();
        UpdateUiState();
    }

    private async Task PurgeLocalsAsync()
    {
        if (!_currentSettings.IsComplete || _currentSettings.Validate(LabPaths.RepoRoot).Count > 0)
        {
            LogOutput.AppendLine("Cannot purge: settings are incomplete.");
            return;
        }

        var count1 = FolderPurger.CountTopLevel(_currentSettings.Local1);
        var count2 = FolderPurger.CountTopLevel(_currentSettings.Local2);
        var total = count1 + count2;

        if (total > 0)
        {
            var msg = $"Purge Local 1 ({count1} items at '{_currentSettings.Local1}') and Local 2 ({count2} items at '{_currentSettings.Local2}')?";
            var confirmed = await LabDialogs.ConfirmAsync(this, "Purge Locals", msg, confirmText: "Purge", danger: true);
            if (!confirmed) return;
        }

        LogOutput.AppendLine($"Purging Local 1 and Local 2...");
        await Task.Run(() =>
        {
            var res1 = FolderPurger.PurgeContents(_currentSettings.Local1, LabPaths.RepoRoot);
            var res2 = FolderPurger.PurgeContents(_currentSettings.Local2, LabPaths.RepoRoot);

            Dispatcher.UIThread.Post(() =>
            {
                LogOutput.AppendLine($"Purged {res1.DeletedCount} items from Local 1.");
                foreach (var f in res1.Failures)
                    LogOutput.AppendLine($"  [FAIL] {f}");

                LogOutput.AppendLine($"Purged {res2.DeletedCount} items from Local 2.");
                foreach (var f in res2.Failures)
                    LogOutput.AppendLine($"  [FAIL] {f}");
            });
        });
    }

    private async Task PurgeRemotesAsync()
    {
        if (!_currentSettings.IsComplete || _currentSettings.Validate(LabPaths.RepoRoot).Count > 0)
        {
            LogOutput.AppendLine("Cannot purge: settings are incomplete.");
            return;
        }

        var count = FolderPurger.CountTopLevel(_currentSettings.Remotes);
        if (count > 0)
        {
            var msg = $"Purge Remotes ({count} items at '{_currentSettings.Remotes}')?";
            var confirmed = await LabDialogs.ConfirmAsync(this, "Purge Remotes", msg, confirmText: "Purge", danger: true);
            if (!confirmed) return;
        }

        LogOutput.AppendLine($"Purging Remotes ({_currentSettings.Remotes})...");
        await Task.Run(() =>
        {
            var res = FolderPurger.PurgeContents(_currentSettings.Remotes, LabPaths.RepoRoot);
            Dispatcher.UIThread.Post(() =>
            {
                LogOutput.AppendLine($"Purged {res.DeletedCount} items from Remotes.");
                foreach (var f in res.Failures)
                    LogOutput.AppendLine($"  [FAIL] {f}");
            });
        });
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
            UpdateUiState();
        }
    }
}
