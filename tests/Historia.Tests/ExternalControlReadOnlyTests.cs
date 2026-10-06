namespace Historia.Tests;

/// <summary>
/// The read-only guarantee of external control, checked at the source level: everything an
/// external tool can ask Historia to do may navigate and may offer a commit message, and must never
/// reach a repository write, a settings write, a dialog or the user's own pending proposal. A scan
/// of the host and every control file fails the build the moment a mutating call appears — a
/// regression here produces no error at runtime, which is why it is a test.
/// </summary>
[Trait("tier", "unit")]
public class ExternalControlReadOnlyTests
{
    private const string HostFile = "Historia.App/Services/ExternalControlHost.cs";
    private const string ControlFolder = "Historia.Core/Services/ExternalControl";

    /// <summary>Tokens no control code may contain.</summary>
    private static readonly (Regex Pattern, string What)[] Forbidden =
    [
        (new(@"\bIGitGateway\b", RegexOptions.Compiled), "IGitGateway (every write goes through it)"),
        (new(@"\.Gateway\b", RegexOptions.Compiled), "a repository's Gateway"),
        (new(@"WithWriteLockAsync", RegexOptions.Compiled), "the repository write lock"),
        (new(@"\.UpdateAsync\s*\(", RegexOptions.Compiled), "a settings (or layout) write"),
        (new(@"\bIDialogService\b", RegexOptions.Compiled), "a dialog"),
        (new(@"\.CloseAsync\s*\(", RegexOptions.Compiled), "closing a tab"),
        (new(@"\bIPatchService\b", RegexOptions.Compiled), "staging patches"),
        (new(@"ClearPendingCommitMessage", RegexOptions.Compiled), "clearing a proposal (only the user's commit box does that)"),
        (new(@"\bIRecentRepositoryService\b", RegexOptions.Compiled), "the Recent list (opening goes through the workspace)"),
        (new(@"\b(File|Directory)\.(Write|Append|Delete|Move|Copy|Create|Replace|SetAttributes)", RegexOptions.Compiled), "a file-system write"),
        (new(@"\bProcess\.Start\b|\bnew\s+ProcessStartInfo\b", RegexOptions.Compiled), "starting a process"),
    ];

    /// <summary>The members the host may use on each collaborator.</summary>
    private static readonly Dictionary<string, HashSet<string>> AllowedMembers = new()
    {
        ["_workspace"] = ["OpenAsync", "Focused", "EnsureInitialized"],
        ["state"] =
        [
            "SetSelectedCommit", "RequestRevealCommit", "SetOpenDiff", "SelectedCommitId", "OpenDiff",
            "RefreshStatusAsync", "Status", "ProposeCommitMessage", "PendingCommitMessage",
        ],
        ["ctx"] = ["FirstGraphPaint", "RepositoryPath", "State", "DisplayName"],
        ["_settings"] = ["Current"],
    };

    [Fact]
    public void Control_code_reaches_no_write_path()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var (rel, text) in ControlSources(root))
            offenders.AddRange(FindForbidden(rel, text));

        Assert.True(offenders.Count == 0,
            "External control must only navigate and propose — it reached a write path:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_host_uses_only_the_allowed_members_of_its_collaborators()
    {
        var root = RepoRoot();
        var text = StripComments(File.ReadAllText(Path.Combine(root, HostFile)));

        var offenders = FindDisallowedMembers(text);

        Assert.True(offenders.Count == 0,
            "ExternalControlHost used a member outside the navigate-and-propose allowlist:\n  "
            + string.Join("\n  ", offenders)
            + "\nA new verb that is neither navigation nor a proposal the user must act on does not ship as an agent verb.");
    }

    [Fact]
    public void Only_the_revision_resolver_starts_git_and_it_only_reads()
    {
        var root = RepoRoot();
        var runners = ControlSources(root)
            .Where(s => Regex.IsMatch(s.Text, @"\.RunAsync\s*\("))
            .Select(s => s.Rel)
            .ToList();

        Assert.Equal([ControlFolder + "/RevisionResolver.cs"], runners);

        var resolver = File.ReadAllText(Path.Combine(root, ControlFolder, "RevisionResolver.cs"));
        Assert.Contains("Arguments = [\"rev-parse\", \"--verify\", \"-q\", \"--end-of-options\", rev + \"^{commit}\"]", resolver);
    }

    [Fact]
    public void The_scan_catches_a_mutating_call()
    {
        Assert.NotEmpty(FindForbidden("x.cs", "await ctx.Gateway.StageAsync(paths);"));
        Assert.NotEmpty(FindForbidden("x.cs", "await _settings.UpdateAsync(s => s);"));
        Assert.NotEmpty(FindForbidden("x.cs", "state.ClearPendingCommitMessage();"));
        Assert.NotEmpty(FindForbidden("x.cs", "File.WriteAllText(p, t);"));
        Assert.NotEmpty(FindDisallowedMembers("state.SetSelectedPaths(x); _workspace.CloseAsync(c);"));
        Assert.Empty(FindForbidden("x.cs", "state.SetOpenDiff(null);"));
        Assert.Empty(FindDisallowedMembers("state.SetOpenDiff(null); ctx.State.ProposeCommitMessage(m);"));
    }

    private static List<string> FindForbidden(string rel, string text)
    {
        var stripped = StripComments(text);
        var found = new List<string>();
        foreach (var (pattern, what) in Forbidden)
        {
            if (pattern.IsMatch(stripped))
                found.Add($"{rel}: {what}");
        }
        return found;
    }

    private static List<string> FindDisallowedMembers(string strippedText)
    {
        var found = new List<string>();
        foreach (Match m in Regex.Matches(strippedText, @"\b(_workspace|state|ctx|_settings)\.(\w+)"))
        {
            var receiver = m.Groups[1].Value;
            var member = m.Groups[2].Value;
            if (!AllowedMembers[receiver].Contains(member))
                found.Add($"{receiver}.{member}");
        }
        return found.Distinct().ToList();
    }

    private static string StripComments(string text) => Regex.Replace(text, @"//.*$", "", RegexOptions.Multiline);

    private static List<(string Rel, string Text)> ControlSources(string root)
    {
        var files = Directory.GetFiles(Path.Combine(root, ControlFolder), "*.cs", SearchOption.AllDirectories)
            .Append(Path.Combine(root, HostFile))
            .ToList();
        Assert.True(files.Count >= 6, "Expected the host and the control files to exist.");

        return files
            .Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f)))
            .ToList();
    }

    /// <summary>Walks up from the test binary to the repo root; a worktree's top-level <c>.git</c>
    /// is a file, so accept either.</summary>
    private static string RepoRoot()
    {
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
