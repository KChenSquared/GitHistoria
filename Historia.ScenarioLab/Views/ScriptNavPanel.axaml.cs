namespace Historia.ScenarioLab.Views;

public partial class ScriptNavPanel : UserControl
{
    public event Action<string>? ScriptSelected;
    public event Action<string, string>? ScriptRenamed;
    public event Action<string>? NodeDeleted;

    private List<WorkspaceNode> _nodes = new();
    private bool _operationsEnabled = true;

    public WorkspaceNode? SelectedNode => ScriptTreeView.SelectedItem as WorkspaceNode;
    public string? SelectedPath => SelectedNode?.FullPath;

    public ScriptNavPanel()
    {
        InitializeComponent();

        AddFolderButton.Click += async (_, _) => await OnAddFolderAsync();
        AddScriptButton.Click += async (_, _) => await OnAddScriptAsync();
        RefreshButton.Click += (_, _) => Refresh(SelectedPath);

        ScriptTreeView.SelectionChanged += (_, _) =>
        {
            if (ScriptTreeView.SelectedItem is ScriptNode scriptNode)
            {
                ScriptSelected?.Invoke(scriptNode.FullPath);
            }
        };

        Loaded += (_, _) => Refresh();
    }

    public void SetOperationsEnabled(bool enabled)
    {
        _operationsEnabled = enabled;
        AddFolderButton.IsEnabled = enabled;
        AddScriptButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
    }

    public void Refresh(string? pathToSelect = null)
    {
        pathToSelect ??= SelectedPath;

        _nodes = ScriptWorkspace.LoadTree(LabPaths.Scripts);
        ScriptTreeView.ItemsSource = _nodes;

        if (!string.IsNullOrEmpty(pathToSelect))
        {
            SelectPath(pathToSelect);
        }
    }

    public bool SelectPath(string fullPath)
    {
        var node = FindNode(_nodes, fullPath);
        if (node is not null)
        {
            ScriptTreeView.SelectedItem = node;
            return true;
        }
        return false;
    }

    private static WorkspaceNode? FindNode(IEnumerable<WorkspaceNode> nodes, string fullPath)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return node;

            if (node is FolderNode folder)
            {
                var found = FindNode(folder.Children, fullPath);
                if (found is not null) return found;
            }
        }
        return null;
    }

    private string GetTargetFolder()
    {
        if (SelectedNode is FolderNode folder)
            return folder.FullPath;

        if (SelectedNode is ScriptNode script)
        {
            var parent = Path.GetDirectoryName(script.FullPath);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                return parent;
        }

        return LabPaths.Scripts;
    }

    private Window? GetOwnerWindow() => TopLevel.GetTopLevel(this) as Window;

    private async Task OnAddFolderAsync()
    {
        if (!_operationsEnabled) return;
        var owner = GetOwnerWindow();
        if (owner is null) return;

        var targetFolder = GetTargetFolder();
        var name = await LabDialogs.PromptAsync(
            owner,
            "Add Folder",
            "Folder name:",
            validate: n => ScriptWorkspace.ValidateFolderName(n, targetFolder));

        if (!string.IsNullOrWhiteSpace(name))
        {
            var newFolder = ScriptWorkspace.AddFolder(targetFolder, name);
            Refresh(newFolder);
        }
    }

    private async Task OnAddScriptAsync()
    {
        if (!_operationsEnabled) return;
        var owner = GetOwnerWindow();
        if (owner is null) return;

        var targetFolder = GetTargetFolder();
        var name = await LabDialogs.PromptAsync(
            owner,
            "Add Script",
            "Script name:",
            validate: n => ScriptWorkspace.ValidateScriptName(n, targetFolder, LabPaths.Scripts));

        if (!string.IsNullOrWhiteSpace(name))
        {
            var newScript = ScriptWorkspace.AddScript(targetFolder, name);
            Refresh(newScript);
            ScriptSelected?.Invoke(newScript);
        }
    }

    private async void OnRenameNodeClick(object? sender, RoutedEventArgs e)
    {
        if (!_operationsEnabled) return;
        var node = (sender as MenuItem)?.DataContext as WorkspaceNode ?? SelectedNode;
        if (node is null) return;

        var owner = GetOwnerWindow();
        if (owner is null) return;

        if (node is ScriptNode script)
        {
            var targetFolder = Path.GetDirectoryName(script.FullPath) ?? LabPaths.Scripts;
            var initial = script.ScriptName;
            var newName = await LabDialogs.PromptAsync(
                owner,
                "Rename Script",
                "New script name:",
                initial: initial,
                validate: n => ScriptWorkspace.ValidateScriptName(n, targetFolder, LabPaths.Scripts, existingPath: script.FullPath));

            if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName, initial, StringComparison.OrdinalIgnoreCase))
            {
                var oldPath = script.FullPath;
                try
                {
                    var newPath = ScriptWorkspace.Rename(script, newName, LabPaths.Scripts, LabPaths.Templates);
                    Refresh(newPath);
                    ScriptRenamed?.Invoke(oldPath, newPath);
                }
                catch (Exception ex)
                {
                    await LabDialogs.ConfirmAsync(owner, "Rename Failed", ex.Message, confirmText: "OK");
                }
            }
        }
        else if (node is FolderNode folder)
        {
            var targetFolder = Path.GetDirectoryName(folder.FullPath) ?? LabPaths.Scripts;
            var initial = folder.Name;
            var newName = await LabDialogs.PromptAsync(
                owner,
                "Rename Folder",
                "New folder name:",
                initial: initial,
                validate: n => ScriptWorkspace.ValidateFolderName(n, targetFolder, existingPath: folder.FullPath));

            if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName, initial, StringComparison.OrdinalIgnoreCase))
            {
                var oldPath = folder.FullPath;
                try
                {
                    var newPath = ScriptWorkspace.Rename(folder, newName, LabPaths.Scripts, LabPaths.Templates);
                    Refresh(newPath);
                    ScriptRenamed?.Invoke(oldPath, newPath);
                }
                catch (Exception ex)
                {
                    await LabDialogs.ConfirmAsync(owner, "Rename Failed", ex.Message, confirmText: "OK");
                }
            }
        }
    }

    private async void OnDeleteNodeClick(object? sender, RoutedEventArgs e)
    {
        if (!_operationsEnabled) return;
        var node = (sender as MenuItem)?.DataContext as WorkspaceNode ?? SelectedNode;
        if (node is null) return;

        var owner = GetOwnerWindow();
        if (owner is null) return;

        var desc = ScriptWorkspace.DescribeDeletion(node, LabPaths.Templates);
        var confirmed = await LabDialogs.ConfirmAsync(
            owner,
            "Confirm Delete",
            desc,
            confirmText: "Delete",
            danger: true);

        if (confirmed)
        {
            var path = node.FullPath;
            ScriptWorkspace.Delete(node, LabPaths.Templates);
            Refresh();
            NodeDeleted?.Invoke(path);
        }
    }
}
