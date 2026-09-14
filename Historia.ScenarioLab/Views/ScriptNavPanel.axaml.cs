namespace Historia.ScenarioLab.Views;

public partial class ScriptNavPanel : UserControl
{
    public event Action<string>? ScriptSelected;

    private List<WorkspaceNode> _nodes = new();
    private readonly HashSet<string> _expandedPaths = new(StringComparer.OrdinalIgnoreCase);

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
}
