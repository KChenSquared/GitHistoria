namespace Historia.Services.Git.Helpers;

/// <summary>
/// The <c>GIT_SEQUENCE_EDITOR</c> / <c>GIT_EDITOR</c> body for interactive rebase.
/// Invoked as <c>Historia --rebase-seq &lt;dir&gt; &lt;gitFile&gt;</c> — reads a pre-staged file
/// from <c>&lt;dir&gt;</c> and overwrites the file Git supplied.
/// </summary>
public static class RebaseHelper
{
    public static void Run(string mode, string dir, string gitFile)
    {
        try
        {
            // Only act when the caller proves it is the Historia process that started this
            // rebase: the per-session token must be present on the environment and match the
            // one written into <dir>. Otherwise leave the file Git supplied untouched.
            string expectedToken;
            try { expectedToken = File.ReadAllText(Path.Combine(dir, "token")).Trim(); }
            catch { return; }
            if (expectedToken.Length == 0
                || !string.Equals(expectedToken, Environment.GetEnvironmentVariable("HISTORIA_REBASE_TOKEN"), StringComparison.Ordinal))
                return;

            if (mode == "--rebase-seq")
            {
                var plan = Path.Combine(dir, "plan");
                if (File.Exists(plan))
                    File.WriteAllText(gitFile, File.ReadAllText(plan));
                return;
            }

            // --rebase-msg: pop the next staged message for a reword/squash step.
            var cursorPath = Path.Combine(dir, "msg-cursor");
            var messagesPath = Path.Combine(dir, "messages");
            if (!File.Exists(messagesPath))
                return;

            var messages = File.ReadAllText(messagesPath).Split('\0');
            var cursor = File.Exists(cursorPath) && int.TryParse(File.ReadAllText(cursorPath), out var c) ? c : 0;
            if (cursor < messages.Length && !string.IsNullOrEmpty(messages[cursor]))
                File.WriteAllText(gitFile, messages[cursor]);

            File.WriteAllText(cursorPath, (cursor + 1).ToString());
        }
        catch
        {
            // A failure here would hang the rebase; better to let Git use the file as-is.
        }
    }
}
