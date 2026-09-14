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
/// Manages the scripts folder tree, validating names and creating/renaming/deleting folders and scripts.
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

    public static string? ValidateFolderName(string name, string targetFolder, string? existingPath = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Folder name is required.";

        if (!ValidNameRegex.IsMatch(name))
            return "Name must only contain letters, numbers, '.', '_', or '-'.";

        if (name.StartsWith('_'))
            return "Folder name cannot start with an underscore.";

        var path = Path.Combine(targetFolder, name);
        if (!string.Equals(path, existingPath, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(path) || File.Exists(path))
                return $"A folder or file named '{name}' already exists in this folder.";
        }

        return null;
    }

    public static string? ValidateScriptName(string name, string targetFolder, string scriptsRoot, string? existingPath = null)
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

        var existingBase = !string.IsNullOrEmpty(existingPath)
            ? Path.GetFileNameWithoutExtension(existingPath)
            : null;

        // Unique tree-wide: check all .sh files under scriptsRoot
        if (Directory.Exists(scriptsRoot))
        {
            var allScripts = Directory.GetFiles(scriptsRoot, "*.sh", SearchOption.AllDirectories);
            foreach (var s in allScripts)
            {
                var sBase = Path.GetFileNameWithoutExtension(s);
                if (string.Equals(sBase, baseName, StringComparison.OrdinalIgnoreCase))
                {
                    if (existingBase is null || !string.Equals(existingBase, baseName, StringComparison.OrdinalIgnoreCase))
                    {
                        return $"A script named '{baseName}' already exists in the workspace.";
                    }
                }
            }
        }

        var fileName = baseName + ".sh";
        var path = Path.Combine(targetFolder, fileName);
        if (!string.Equals(path, existingPath, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(path) || Directory.Exists(path))
                return $"A file named '{fileName}' already exists in this folder.";
        }

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

    public static string DescribeDeletion(WorkspaceNode node, string templatesRoot)
    {
        if (node is ScriptNode script)
        {
            var tmplPath = Path.Combine(templatesRoot, script.ScriptName);
            var hasTemplate = Directory.Exists(tmplPath);
            return hasTemplate
                ? $"Delete script '{script.Name}' and its template folder 'templates/{script.ScriptName}'?"
                : $"Delete script '{script.Name}'?";
        }

        if (node is FolderNode folder)
        {
            var allScripts = Directory.Exists(folder.FullPath)
                ? Directory.GetFiles(folder.FullPath, "*.sh", SearchOption.AllDirectories)
                : Array.Empty<string>();

            var templateNames = new List<string>();
            foreach (var s in allScripts)
            {
                var baseName = Path.GetFileNameWithoutExtension(s);
                var tmplPath = Path.Combine(templatesRoot, baseName);
                if (Directory.Exists(tmplPath))
                {
                    templateNames.Add($"templates/{baseName}");
                }
            }

            var scriptDesc = $"{allScripts.Length} script(s)";
            var tmplDesc = templateNames.Count > 0
                ? $" and {templateNames.Count} associated template folder(s) ({string.Join(", ", templateNames)})"
                : "";

            return $"Delete folder '{folder.Name}' containing {scriptDesc}{tmplDesc}?";
        }

        return $"Delete '{node.Name}'?";
    }

    public static string Rename(WorkspaceNode node, string newName, string scriptsRoot, string templatesRoot)
    {
        if (node is ScriptNode script)
        {
            var oldBase = script.ScriptName;
            var newBase = newName.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
                ? newName[..^3]
                : newName;

            var oldTemplate = Path.Combine(templatesRoot, oldBase);
            var newTemplate = Path.Combine(templatesRoot, newBase);

            if (Directory.Exists(oldTemplate) && !string.Equals(oldBase, newBase, StringComparison.OrdinalIgnoreCase) && Directory.Exists(newTemplate))
            {
                throw new InvalidOperationException($"Target template folder 'templates/{newBase}' already exists.");
            }

            var parent = Path.GetDirectoryName(script.FullPath) ?? scriptsRoot;
            var newFilePath = Path.Combine(parent, newBase + ".sh");

            File.Move(script.FullPath, newFilePath);

            if (Directory.Exists(oldTemplate) && !string.Equals(oldBase, newBase, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Move(oldTemplate, newTemplate);
            }

            node.Name = newBase + ".sh";
            node.FullPath = newFilePath;
            return newFilePath;
        }

        if (node is FolderNode folder)
        {
            var parent = Path.GetDirectoryName(folder.FullPath) ?? scriptsRoot;
            var newFolderPath = Path.Combine(parent, newName);

            Directory.Move(folder.FullPath, newFolderPath);

            node.Name = newName;
            node.FullPath = newFolderPath;
            return newFolderPath;
        }

        return node.FullPath;
    }

    public static void Delete(WorkspaceNode node, string templatesRoot)
    {
        if (node is ScriptNode script)
        {
            if (File.Exists(script.FullPath))
            {
                File.Delete(script.FullPath);
            }

            var tmpl = Path.Combine(templatesRoot, script.ScriptName);
            if (Directory.Exists(tmpl))
            {
                Directory.Delete(tmpl, recursive: true);
            }
            return;
        }

        if (node is FolderNode folder)
        {
            if (Directory.Exists(folder.FullPath))
            {
                var scripts = Directory.GetFiles(folder.FullPath, "*.sh", SearchOption.AllDirectories);
                foreach (var s in scripts)
                {
                    var baseName = Path.GetFileNameWithoutExtension(s);
                    var tmpl = Path.Combine(templatesRoot, baseName);
                    if (Directory.Exists(tmpl))
                    {
                        Directory.Delete(tmpl, recursive: true);
                    }
                }

                Directory.Delete(folder.FullPath, recursive: true);
            }
        }
    }
}
