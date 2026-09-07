namespace Historia;

internal static class Program
{
    /// <summary>Set once in <see cref="Main"/>: whether this process owns the single-instance mutex (§3).</summary>
    public static bool IsPrimaryInstance { get; private set; }

    // Avalonia configuration — referenced by the visual designer and by Main.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    [STAThread]
    public static int Main(string[] args)
    {
        // ── Re-invoked-self helpers (§8.1.4). Checked BEFORE anything else: the git process that
        //    spawns these carries HISTORIA_ASKPASS on its environment too. Each writes one file
        //    Git handed it and exits; the UI never starts. ──
        if (args is [_, ..] && (args[0] == "--rebase-seq" || args[0] == "--rebase-msg") && args.Length >= 3)
        {
            RebaseHelper.Run(args[0], args[1], args[2]);
            return 0;
        }

        // The Historia executable re-invoked as the GIT_ASKPASS / SSH_ASKPASS helper (§6.5.2).
        if (Environment.GetEnvironmentVariable("HISTORIA_ASKPASS") == "1")
        {
            var prompt = args.Length > 0 ? args[0] : string.Empty;
            var answer = prompt.Contains("Username", StringComparison.OrdinalIgnoreCase)
                ? Environment.GetEnvironmentVariable("HISTORIA_ASKPASS_USERNAME")
                : Environment.GetEnvironmentVariable("HISTORIA_ASKPASS_PASSWORD");
            Console.Out.Write(answer ?? string.Empty);
            Console.Out.Write('\n');
            return 0;
        }

        // ── Single-instance guard (§3). A second launch forwards its non-switch arguments
        //    (repo paths) to the running instance and exits, so only one process ever writes
        //    the shared JSON state files. ──
        IsPrimaryInstance = SingleInstance.TryAcquirePrimary();
        if (!IsPrimaryInstance)
        {
            var forwarded = new List<string>();
            foreach (var a in args)
                if (!a.StartsWith('-'))
                    forwarded.Add(a);

            var outcome = SingleInstance.ResolveSecondInstance(forwarded, TimeSpan.FromSeconds(3));
            // If the previous instance is still shutting down, offer a Retry rather than
            // silently doing nothing.
            while (outcome == SingleInstance.SecondInstanceOutcome.StillBlocked && SingleInstance.PromptRetry())
                outcome = SingleInstance.ResolveSecondInstance(forwarded, TimeSpan.FromSeconds(3));

            if (outcome != SingleInstance.SecondInstanceOutcome.BecamePrimary)
                return 0; // handed off to a live primary, or the user gave up

            IsPrimaryInstance = true; // the old primary exited — we own the mutex now
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
}
