using System.Text.RegularExpressions;

namespace Historia.ScenarioLab.Services;

public abstract class WorkspaceNode
{
    public required string Name { get; set; }
    public required string FullPath { get; set; }
}

public sealed class FolderNode : WorkspaceNode
{
    public List<WorkspaceNode> Children { get; } = new();
    public bool IsExpanded { get; set; }
}

public sealed class ScriptNode : WorkspaceNode
{
    public string ScriptName => Path.GetFileNameWithoutExtension(Name);
}

/// <summary>
/// Manages the scripts folder tree, validating names and creating folders/scripts.
/// </summary>
public static class ScriptWorkspace
{
    private static readonly Regex ValidNameRegex = new("^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    public const string ScriptSkeleton =
        "#!/usr/bin/env bash\n" +
        "# @short: <describe the scenario in under 72 characters>\n\n" +
        "source \"$SCENARIO_LIB\"\n" +
        "lab_begin\n\n" +
        "lab_done\n";

    public static List<WorkspaceNode> LoadTree(string scriptsRoot)
    {
        if (!Directory.Exists(scriptsRoot))
            return new List<WorkspaceNode>();

        return LoadFolderChildren(scriptsRoot);
    }

    private static List<WorkspaceNode> LoadFolderChildren(string folderPath)
    {
        var result = new List<WorkspaceNode>();

        var dirInfo = new DirectoryInfo(folderPath);

        // Folders first
        var dirs = dirInfo.GetDirectories()
            .Where(d => !d.Name.StartsWith('.'))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var d in dirs)
        {
            var node = new FolderNode
            {
                Name = d.Name,
                FullPath = d.FullName
            };
            node.Children.AddRange(LoadFolderChildren(d.FullName));
            result.Add(node);
        }

        // Files second
        var files = dirInfo.GetFiles("*.sh")
            .Where(f => !f.Name.StartsWith('.'))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            result.Add(new ScriptNode
            {
                Name = f.Name,
                FullPath = f.FullName
            });
        }

        return result;
    }

    public static string? ValidateFolderName(string name, string targetFolder)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Folder name is required.";

        if (!ValidNameRegex.IsMatch(name))
            return "Name must only contain letters, numbers, '.', '_', or '-'.";

        if (name.StartsWith('_'))
            return "Folder name cannot start with an underscore.";

        var path = Path.Combine(targetFolder, name);
        if (Directory.Exists(path) || File.Exists(path))
            return $"A folder or file named '{name}' already exists in this folder.";

        return null;
    }

    public static string? ValidateScriptName(string name, string targetFolder, string scriptsRoot)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Script name is required.";

        var baseName = name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
            ? name[..^3]
            : name;

        if (string.IsNullOrWhiteSpace(baseName))
            return "Script name is required.";

        if (!ValidNameRegex.IsMatch(baseName))
            return "Script name must only contain letters, numbers, '.', '_', or '-'.";

        if (baseName.StartsWith('_'))
            return "Script name cannot start with an underscore.";

        // Unique tree-wide: check all .sh files under scriptsRoot
        if (Directory.Exists(scriptsRoot))
        {
            var allScripts = Directory.GetFiles(scriptsRoot, "*.sh", SearchOption.AllDirectories);
            foreach (var s in allScripts)
            {
                var existingBase = Path.GetFileNameWithoutExtension(s);
                if (string.Equals(existingBase, baseName, StringComparison.OrdinalIgnoreCase))
                {
                    return $"A script named '{baseName}' already exists in the workspace.";
                }
            }
        }

        var fileName = baseName + ".sh";
        var path = Path.Combine(targetFolder, fileName);
        if (File.Exists(path) || Directory.Exists(path))
            return $"A file named '{fileName}' already exists in this folder.";

        return null;
    }

    public static string AddFolder(string targetFolder, string name)
    {
        var path = Path.Combine(targetFolder, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public static string AddScript(string targetFolder, string name)
    {
        var baseName = name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
            ? name[..^3]
            : name;
        var fileName = baseName + ".sh";
        var path = Path.Combine(targetFolder, fileName);

        File.WriteAllText(path, ScriptSkeleton, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
