namespace Historia.Tests;

/// <summary>
/// MVVM view guard. Views are AXAML bound to view models in <c>Historia.Core/ViewModels</c>, so under
/// <c>Historia.App/Views</c> and <c>Historia.App/Platform</c>: every <c>UserControl</c> or
/// <c>Window</c> subclass has a sibling <c>.axaml</c>; no <c>new Window</c> is built in code outside
/// <c>AvaloniaDialogService.cs</c>, which builds its plain dialogs from a title, a message and a set
/// of buttons; and no method named <c>Rebuild…</c> is declared or called, because a view that
/// rebuilds its tree by hand has stopped binding. Under <c>Historia.App/Views</c> only, a view takes
/// its dispatcher and clipboard from <c>ViewServices</c>, passing its service provider, instead of
/// constructing them.
/// </summary>
[Trait("tier", "unit")]
public class MvvmViewGuardTests
{
    private static readonly string[] ViewFolders = ["Historia.App/Views", "Historia.App/Platform"];

    /// <summary>The one file that may construct a plain <c>Window</c>.</summary>
    private const string DialogServiceFile = "Historia.App/Platform/AvaloniaDialogService.cs";

    /// <summary>
    /// View classes that stay code-built, by class name, each with the reason. An entry fails
    /// <see cref="Allowlisted_views_are_still_code_built"/> once the class gains a sibling
    /// <c>.axaml</c> or goes away.
    /// </summary>
    private static readonly (string Class, string Reason)[] CodeBuiltViews =
    [
        ("MarkdownView", "Renders Markdown text to a tree of inlines; it has no layout to declare."),
    ];

    private static readonly Regex ViewClass = new(
        @"\bclass\s+(?<name>\w+)\s*:\s*(?<bases>[^{;\n]*)",
        RegexOptions.Compiled);

    private static readonly Regex ViewBase = new(
        @"(?<![\w.])(?:Avalonia\.Controls\.)?(?:UserControl|Window)\b",
        RegexOptions.Compiled);

    private static readonly Regex NewWindow = new(
        @"\bnew\s+(?:Avalonia\.Controls\.)?Window\b",
        RegexOptions.Compiled);

    /// <summary>The one view file that may construct the dispatcher and clipboard.</summary>
    private const string ViewServicesFile = "Historia.App/Views/ViewServices.cs";

    // Constructing either service, or asking ViewServices for one with no provider, which constructs it.
    private static readonly Regex ServiceConstruction = new(
        @"\bnew\s+(?:UiDispatcher|ClipboardService)\s*\(|\bViewServices\s*\.\s*(?:Dispatcher|Clipboard)\s*\(\s*null\s*\)",
        RegexOptions.Compiled);

    private static readonly Regex RebuildMember = new(
        @"\bRebuild\w*\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex LineComment = new(@"//[^\n]*", RegexOptions.Compiled);
    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void Views_are_axaml_bound_to_view_models()
    {
        var root = RepoRoot();
        var violations = new List<string>();

        foreach (var file in ViewSourceFiles(root))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            violations.AddRange(FindViolations(rel, File.ReadAllText(file),
                sibling => File.Exists(Path.Combine(root, sibling))));
        }

        Assert.True(violations.Count == 0,
            "MVVM view violation:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Scanner_covers_the_view_and_platform_folders()
    {
        var root = RepoRoot();
        var scanned = ViewSourceFiles(root)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .ToList();

        Assert.Contains("Historia.App/Views/HomeView.axaml.cs", scanned);
        Assert.Contains("Historia.App/Views/MarkdownView.cs", scanned);
        Assert.Contains("Historia.App/Platform/AboutDialog.axaml.cs", scanned);
        Assert.Contains(DialogServiceFile, scanned);
    }

    [Fact]
    public void Allowlisted_views_are_still_code_built()
    {
        var root = RepoRoot();
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in ViewSourceFiles(root))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (var (name, _) in ViewClasses(StripComments(File.ReadAllText(file))))
                declared[name] = rel;
        }

        foreach (var (name, _) in CodeBuiltViews)
        {
            Assert.True(declared.TryGetValue(name, out var rel),
                $"{name} is allowlisted as code-built but no such view class exists; remove it from CodeBuiltViews.");
            Assert.False(HasSibling(rel!, name, sibling => File.Exists(Path.Combine(root, sibling))),
                $"{name} has an .axaml now; remove it from CodeBuiltViews.");
        }
    }

    [Theory]
    [InlineData("public sealed class Page : UserControl { }")]
    [InlineData("internal sealed partial class Page : Window, IDisposable { }")]
    [InlineData("public class Page : Avalonia.Controls.UserControl { }")]
    public void A_view_without_a_sibling_axaml_is_reported(string source)
    {
        var violations = FindViolations("Historia.App/Views/Page.cs", source, _ => false);

        Assert.Single(violations);
        Assert.Contains("Page", violations[0]);
        Assert.Contains(".axaml", violations[0]);
    }

    [Theory]
    [InlineData("Historia.App/Views/Page.axaml.cs", "Historia.App/Views/Page.axaml")]
    [InlineData("Historia.App/Views/Page.SectionHeader.axaml.cs", "Historia.App/Views/Page.SectionHeader.axaml")]
    [InlineData("Historia.App/Views/Page.Sync.cs", "Historia.App/Views/Page.axaml")]
    public void A_view_with_a_sibling_axaml_is_accepted(string rel, string axaml)
    {
        var violations = FindViolations(rel, "public sealed partial class Page : UserControl { }",
            sibling => sibling == axaml);

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("public static class Menu { }")]
    [InlineData("public sealed class Probe : IDisposable { }")]
    [InlineData("public sealed class Converter : Avalonia.Data.Converters.IValueConverter { }")]
    [InlineData("public sealed class Host : IWindowHost { }")]
    [InlineData("public partial class Page { }")]
    public void A_class_that_is_not_a_view_needs_no_axaml(string source)
    {
        Assert.Empty(FindViolations("Historia.App/Views/Thing.cs", source, _ => false));
    }

    [Fact]
    public void An_allowlisted_view_is_accepted_without_an_axaml()
    {
        var violations = FindViolations("Historia.App/Views/MarkdownView.cs",
            "public sealed class MarkdownView : UserControl { }", _ => false);

        Assert.Empty(violations);
    }

    [Theory]
    [InlineData("var w = new Window { Title = \"x\" };")]
    [InlineData("var w = new Window();")]
    [InlineData("var w = new Avalonia.Controls.Window();")]
    public void A_code_built_window_is_reported_outside_the_dialog_service(string source)
    {
        var violations = FindViolations("Historia.App/Views/Form.axaml.cs", source, _ => true);

        Assert.Single(violations);
        Assert.Contains("new Window", violations[0]);
    }

    [Fact]
    public void The_dialog_service_may_build_a_window()
    {
        Assert.Empty(FindViolations(DialogServiceFile, "var window = new Window { Title = t };", _ => true));
    }

    [Theory]
    [InlineData("var w = new WindowChrome();")]
    [InlineData("// var w = new Window();")]
    [InlineData("/* var w = new Window(); */")]
    [InlineData("/// builds a <c>new Window</c> for each call")]
    public void Other_text_is_not_mistaken_for_a_code_built_window(string source)
    {
        Assert.Empty(FindViolations("Historia.App/Views/Form.axaml.cs", source, _ => true));
    }

    [Theory]
    [InlineData("private void RebuildTabs() { }")]
    [InlineData("private Task RebuildAsync() => Task.CompletedTask;")]
    [InlineData("RebuildList();")]
    [InlineData("view.Rebuild ();")]
    public void A_rebuild_member_is_reported(string source)
    {
        var violations = FindViolations("Historia.App/Views/Strip.axaml.cs", source, _ => true);

        Assert.Single(violations);
        Assert.Contains("Rebuild", violations[0]);
    }

    [Fact]
    public void A_rebuild_in_a_comment_is_not_reported()
    {
        Assert.Empty(FindViolations("Historia.App/Views/Strip.axaml.cs", "// RebuildTabs() was removed", _ => true));
    }

    [Theory]
    [InlineData("var ui = new UiDispatcher();")]
    [InlineData("var clip = new ClipboardService();")]
    [InlineData("var ui = new UiDispatcher ( );")]
    [InlineData("var ui = ViewServices.Dispatcher(null);")]
    [InlineData("var clip = ViewServices.Clipboard( null );")]
    public void A_view_that_constructs_a_service_is_reported(string source)
    {
        var violations = FindViolations("Historia.App/Views/Page.axaml.cs", source, _ => true);

        Assert.Single(violations);
        Assert.Contains("ViewServices", violations[0]);
    }

    [Theory]
    [InlineData(ViewServicesFile, "return new UiDispatcher();")]
    [InlineData("Historia.App/Platform/Host.cs", "var ui = new UiDispatcher();")]
    [InlineData("Historia.App/Views/Page.axaml.cs", "// var ui = new UiDispatcher();")]
    [InlineData("Historia.App/Views/Page.axaml.cs", "var ui = ViewServices.Dispatcher(sp);")]
    public void Other_text_is_not_mistaken_for_a_constructed_service(string rel, string source)
    {
        Assert.Empty(FindViolations(rel, source, _ => true));
    }

    [Fact]
    public void Only_ViewServices_constructs_the_dispatcher_and_clipboard_in_views()
    {
        var root = RepoRoot();
        var constructing = new List<string>();
        foreach (var file in ViewSourceFiles(root))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (ServiceConstruction.IsMatch(StripComments(File.ReadAllText(file))))
                constructing.Add(rel);
        }

        Assert.Equal([ViewServicesFile], constructing.Where(r => r.StartsWith("Historia.App/Views/", StringComparison.Ordinal)));
    }

    /// <summary>Every rule broken by the source in <paramref name="text"/>, as readable lines.</summary>
    private static List<string> FindViolations(string rel, string text, Func<string, bool> axamlExists)
    {
        var violations = new List<string>();
        var code = StripComments(text);

        foreach (var (name, index) in ViewClasses(code))
        {
            if (CodeBuiltViews.Any(v => v.Class == name))
                continue;
            if (!HasSibling(rel, name, axamlExists))
                violations.Add($"{rel}:{LineOf(code, index)}: view {name} has no sibling .axaml — write the view as AXAML bound to a view model.");
        }

        if (rel != DialogServiceFile)
        {
            foreach (Match m in NewWindow.Matches(code))
                violations.Add($"{rel}:{LineOf(code, m.Index)}: new Window — a dialog is an AXAML window bound to a view model.");
        }

        if (rel.StartsWith("Historia.App/Views/", StringComparison.Ordinal) && rel != ViewServicesFile)
        {
            foreach (Match m in ServiceConstruction.Matches(code))
                violations.Add($"{rel}:{LineOf(code, m.Index)}: {m.Value.TrimEnd('(', ' ')} — a view resolves the dispatcher and clipboard through ViewServices.");
        }

        foreach (Match m in RebuildMember.Matches(code))
            violations.Add($"{rel}:{LineOf(code, m.Index)}: {m.Value.TrimEnd('(', ' ')} — a view binds to its view model instead of rebuilding its tree.");

        return violations;
    }

    /// <summary>The <c>UserControl</c> and <c>Window</c> subclasses declared in <paramref name="code"/>.</summary>
    private static IEnumerable<(string Name, int Index)> ViewClasses(string code)
    {
        foreach (Match m in ViewClass.Matches(code))
        {
            if (ViewBase.IsMatch(m.Groups["bases"].Value))
                yield return (m.Groups["name"].Value, m.Index);
        }
    }

    /// <summary>
    /// True when the view has its markup beside it: an <c>.axaml</c> named for the class, for the
    /// file (<c>X.axaml.cs</c> pairs with <c>X.axaml</c>) or for the file's first segment
    /// (<c>X.Sync.cs</c>, a partial of <c>X</c>).
    /// </summary>
    private static bool HasSibling(string rel, string className, Func<string, bool> axamlExists)
    {
        var slash = rel.LastIndexOf('/');
        var dir = slash < 0 ? "" : rel[..slash];
        var file = rel[(slash + 1)..];
        var stem = file[..^".cs".Length];
        if (stem.EndsWith(".axaml", StringComparison.Ordinal))
            stem = stem[..^".axaml".Length];

        var candidates = new List<string> { $"{dir}/{className}.axaml", $"{dir}/{stem}.axaml" };
        var dot = stem.IndexOf('.');
        if (dot > 0)
            candidates.Add($"{dir}/{stem[..dot]}.axaml");
        return candidates.Any(axamlExists);
    }

    private static string StripComments(string text) =>
        LineComment.Replace(BlockComment.Replace(text, m => new string('\n', m.Value.Count(c => c == '\n'))), "");

    private static int LineOf(string text, int index) => text[..index].Count(c => c == '\n') + 1;

    private static IEnumerable<string> ViewSourceFiles(string root)
    {
        foreach (var folder in ViewFolders)
        {
            var dir = Path.Combine(root, folder);
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/") || rel.Contains("/bin/"))
                    continue;
                yield return file;
            }
        }
    }

    private static string RepoRoot()
    {
        // A worktree's top-level .git is a file, not a directory.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var gitEntry = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
                break;
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
