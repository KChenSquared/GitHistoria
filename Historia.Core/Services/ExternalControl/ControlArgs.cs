namespace Historia.Services.ExternalControl;

/// <summary>The outcome of parsing a <c>historia-cmdr</c> command line.</summary>
public sealed record ControlArgsResult
{
    public ControlRequest? Request { get; init; }
    public string? UsageError { get; init; }
    public bool ShowHelp { get; init; }
    public bool ShowVersion { get; init; }

    /// <summary>Do not start Historia when none is running. Client-only; never sent.</summary>
    public bool NoLaunch { get; init; }

    /// <summary>The <c>-F</c> argument of <c>propose-commit</c> (<c>-</c> is standard input). The
    /// client reads and normalizes it; the parser does not touch the file system.</summary>
    public string? MessageFile { get; init; }

    public bool IsUsageError => UsageError is not null;
}

/// <summary>Parses the <c>historia-cmdr</c> command line into a <see cref="ControlRequest"/>.</summary>
public static class ControlArgs
{
    public const string UsageText = """
        usage:
          historia-cmdr ping
          historia-cmdr selection
          historia-cmdr open                              [-C <repo>] [--no-focus] [--no-launch]
          historia-cmdr show    <rev>                     [-C <repo>] [--no-focus] [--no-launch]
          historia-cmdr diff    <rev> <file>     [--line N] [-C <repo>] [--no-focus] [--no-launch]
          historia-cmdr compare <base> <head> <file> [--line N] [-C <repo>] [--no-focus] [--no-launch]
          historia-cmdr changes <file> [--staged] [--line N] [-C <repo>] [--no-focus] [--no-launch]
          historia-cmdr propose-commit (-m <text>... | -F <file|->) [-C <repo>] [--no-focus] [--no-launch]
          historia-cmdr --help | --version

        Shows the result in Historia. Prints one JSON object. Exit codes: 0 ok, 1 command error,
        2 Historia unreachable or control disabled, 64 usage.
        """;

    public static ControlArgsResult Parse(IReadOnlyList<string> args, string cwd)
    {
        if (args.Count == 0)
            return Usage("A command is required.");

        if (args[0] is "--help" or "-h" or "-?")
            return new ControlArgsResult { ShowHelp = true };
        if (args[0] is "--version")
            return new ControlArgsResult { ShowVersion = true };

        if (!ControlVerbs.TryParse(args[0], out var verb))
            return Usage($"Unknown command '{args[0]}'.");

        string absoluteCwd;
        try
        {
            absoluteCwd = Path.GetFullPath(cwd);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Usage("The working directory is not a valid path.");
        }

        var takesPathOptions = verb is ControlVerb.Open or ControlVerb.Show or ControlVerb.Diff
            or ControlVerb.Compare or ControlVerb.Changes or ControlVerb.ProposeCommit;
        var takesLine = verb is ControlVerb.Diff or ControlVerb.Compare or ControlVerb.Changes;

        var positionals = new List<string>();
        var messages = new List<string>();
        string? repoOption = null;
        string? messageFile = null;
        int? line = null;
        var staged = false;
        var noFocus = false;
        var noLaunch = false;
        var optionsEnded = false;

        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            if (optionsEnded || a.Length == 0 || a[0] != '-' || a == "-")
            {
                positionals.Add(a);
                continue;
            }

            switch (a)
            {
                case "--":
                    optionsEnded = true;
                    break;
                case "-C" when takesPathOptions:
                    if (!TryTakeValue(args, ref i, out repoOption))
                        return Usage("-C needs a repository path.");
                    break;
                case "--no-focus" when takesPathOptions:
                    noFocus = true;
                    break;
                case "--no-launch" when takesPathOptions:
                    noLaunch = true;
                    break;
                case "--staged" when verb == ControlVerb.Changes:
                    staged = true;
                    break;
                case "--line" when takesLine:
                    if (!TryTakeValue(args, ref i, out var rawLine)
                        || !int.TryParse(rawLine, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                        || n <= 0)
                        return Usage("--line needs a positive line number.");
                    line = n;
                    break;
                case "-m" when verb == ControlVerb.ProposeCommit:
                    if (!TryTakeValue(args, ref i, out var message))
                        return Usage("-m needs a message.");
                    messages.Add(message!);
                    break;
                case "-F" when verb == ControlVerb.ProposeCommit:
                    if (!TryTakeValue(args, ref i, out messageFile))
                        return Usage("-F needs a file, or - for standard input.");
                    break;
                default:
                    return Usage($"Unknown option '{a}' for '{args[0]}'.");
            }
        }

        var expected = verb switch
        {
            ControlVerb.Show => 1,
            ControlVerb.Diff => 2,
            ControlVerb.Compare => 3,
            ControlVerb.Changes => 1,
            _ => 0,
        };
        if (positionals.Count != expected)
            return Usage($"'{args[0]}' takes {expected} argument{(expected == 1 ? "" : "s")}, got {positionals.Count}.");

        string? repo = null;
        if (takesPathOptions)
        {
            try
            {
                repo = repoOption is null ? absoluteCwd : Path.GetFullPath(repoOption, absoluteCwd);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Usage("-C is not a valid path.");
            }
        }

        string? rev = null, otherRev = null, file = null;
        switch (verb)
        {
            case ControlVerb.Show:
                rev = positionals[0];
                break;
            case ControlVerb.Diff:
                (rev, file) = (positionals[0], positionals[1]);
                break;
            case ControlVerb.Compare:
                (rev, otherRev, file) = (positionals[0], positionals[1], positionals[2]);
                break;
            case ControlVerb.Changes:
                file = positionals[0];
                break;
        }

        foreach (var r in new[] { rev, otherRev })
        {
            if (r is not null && !ControlInputValidation.TryValidateRevision(r, out var revError))
                return Usage(revError!);
        }

        if (file is not null && (file.Length == 0 || file.Contains('\0')))
            return Usage("A file path is required.");

        string? commitMessage = null;
        if (verb == ControlVerb.ProposeCommit)
        {
            if (messages.Count > 0 && messageFile is not null)
                return Usage("Use -m or -F, not both.");
            if (messages.Count == 0 && messageFile is null)
                return Usage("propose-commit needs -m <text> or -F <file|->.");

            if (messages.Count > 0)
            {
                if (!ControlInputValidation.NormalizeCommitMessage(string.Join("\n\n", messages), out var normalized, out var messageError))
                    return Usage(messageError!);
                commitMessage = normalized;
            }
        }

        return new ControlArgsResult
        {
            NoLaunch = noLaunch,
            MessageFile = messageFile,
            Request = new ControlRequest
            {
                Verb = verb,
                Cwd = absoluteCwd,
                Repo = repo,
                Rev = rev,
                OtherRev = otherRev,
                Path = file,
                Line = line,
                Staged = staged,
                Focus = !noFocus,
                CommitMessage = commitMessage,
            },
        };
    }

    private static ControlArgsResult Usage(string error) => new() { UsageError = error };

    private static bool TryTakeValue(IReadOnlyList<string> args, ref int i, out string? value)
    {
        if (i + 1 >= args.Count)
        {
            value = null;
            return false;
        }

        value = args[++i];
        return true;
    }
}
