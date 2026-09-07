using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Historia.Platform;

/// <summary>
/// Single-instance guard (§3). The first process to start holds a named mutex and runs a tiny
/// named-pipe listener; a later launch hands its arguments to that listener and exits, so there
/// is only ever one Historia writing <c>settings.json</c> / <c>profiles.json</c> / <c>session.json</c>.
/// Windows is the parity target; the mutex and the pipe both work on macOS / Linux as well, and
/// any failure here degrades to "just start a normal instance".
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\Historia.SingleInstance.Mutex";
    private const string PipeName = "Historia.SingleInstance.Pipe";

    // Held for the process lifetime so the name stays claimed; never released explicitly (the
    // OS frees it on exit, and an abandoned mutex from a crash is reclaimable by the next launch).
    private static Mutex? _held;

    /// <summary>
    /// True when this process is the primary instance. On any platform/permission problem it
    /// returns true (behave as a lone instance) rather than blocking startup.
    /// </summary>
    public static bool TryAcquirePrimary()
    {
        try
        {
            _held = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew)
            {
                _held.Dispose();
                _held = null;
            }
            return createdNew;
        }
        catch
        {
            return true;
        }
    }

    internal enum SecondInstanceOutcome
    {
        /// <summary>A live primary accepted this launch's arguments.</summary>
        Forwarded,
        /// <summary>The previous primary is gone; this process took the mutex and should start the UI.</summary>
        BecamePrimary,
        /// <summary>A primary still holds the mutex but never answered — likely shutting down.</summary>
        StillBlocked,
    }

    /// <summary>
    /// Resolve a non-primary launch: hand off to a live primary, or — if none answers (the
    /// previous instance may still be shutting down) — poll up to <paramref name="wait"/> for
    /// the mutex to free (take over) or the pipe to come back (hand off).
    /// </summary>
    public static SecondInstanceOutcome ResolveSecondInstance(IReadOnlyList<string> args, TimeSpan wait)
    {
        if (ForwardToPrimary(args))
            return SecondInstanceOutcome.Forwarded;

        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);

            try
            {
                var m = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
                if (createdNew)
                {
                    _held = m;
                    return SecondInstanceOutcome.BecamePrimary;
                }
                m.Dispose();
            }
            catch { /* keep polling */ }

            if (ForwardToPrimary(args))
                return SecondInstanceOutcome.Forwarded;
        }

        return SecondInstanceOutcome.StillBlocked;
    }

    /// <summary>
    /// Windows-only lightweight "still closing — Retry / Cancel" prompt (there is no Avalonia UI
    /// this early). Returns true if the user chose Retry. Elsewhere returns false (just abort).
    /// </summary>
    public static bool PromptRetry()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        const uint MB_RETRYCANCEL = 0x00000005;
        const uint MB_ICONINFORMATION = 0x00000040;
        const uint MB_SETFOREGROUND = 0x00010000;
        const uint MB_TOPMOST = 0x00040000;
        const int IDRETRY = 4;

        try
        {
            return MessageBoxW(IntPtr.Zero,
                "Another Historia window is still closing.\n\nWait a moment, then click Retry — or Cancel to abort this launch.",
                "Historia",
                MB_RETRYCANCEL | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST) == IDRETRY;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>Best-effort hand-off of <paramref name="args"/> to the running primary. False if none answered.</summary>
    public static bool ForwardToPrimary(IReadOnlyList<string> args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var w = new StreamWriter(client) { AutoFlush = true };
            foreach (var a in args)
                w.WriteLine(a);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs the primary's listener until <paramref name="ct"/> trips. Each client sends zero or
    /// more newline-separated argument lines; <paramref name="onArgs"/> is invoked with them (the
    /// caller marshals to a UI thread if it needs one). A single empty connection (no lines) is
    /// still delivered as an empty array so the primary can just raise/focus its window.
    /// </summary>
    public static async Task RunServerAsync(Action<string[]> onArgs, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var r = new StreamReader(server);
                var lines = new List<string>();
                string? line;
                while ((line = await r.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                    lines.Add(line);

                onArgs(lines.ToArray());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A malformed client must not kill the listener; accept the next one.
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { return; }
            }
        }
    }
}
