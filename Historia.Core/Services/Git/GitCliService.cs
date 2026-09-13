namespace Historia.Services.Git;

/// <inheritdoc cref="IGitCliService"/>
public sealed class GitCliService : IGitCliService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IGitExecutableLocator _locator;

    public GitCliService(IGitExecutableLocator locator)
    {
        _locator = locator;
    }

    public bool IsGitAvailable => _locator.Current.Found;

    public Task<GitCliResult> RunAsync(GitCliRequest request, CancellationToken ct = default)
        => RunAsync(request, progress: null, onStdoutLine: null, ct);

    public async Task<GitCliResult> RunAsync(
        GitCliRequest request,
        IProgress<GitProgress>? progress,
        Action<string>? onStdoutLine,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!AppLog.Enabled)
            return await RunCoreAsync(request, progress, onStdoutLine, ct).ConfigureAwait(false);

        var op = AppLog.Operation("git-cli", $"git {string.Join(' ', request.Arguments)}  (cwd={request.WorkingDirectory})");
        try
        {
            var result = await RunCoreAsync(request, progress, onStdoutLine, ct).ConfigureAwait(false);
            op.Outcome($"exit={result.ExitCode} kind={result.ErrorKind} ({result.Duration.TotalMilliseconds:F0}ms)");
            if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError))
            {
                var firstLine = result.StandardError.Split('\n', 2)[0].TrimEnd('\r');
                AppLog.Write(AppLogLevel.Warn, "git-cli", firstLine);
            }
            return result;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
        finally
        {
            op.Dispose();
        }
    }

    private async Task<GitCliResult> RunCoreAsync(
        GitCliRequest request,
        IProgress<GitProgress>? progress,
        Action<string>? onStdoutLine,
        CancellationToken ct)
    {
        var git = _locator.Current;
        if (!git.Found || git.Path is null)
        {
            return new GitCliResult(-1, string.Empty,
                "git.exe was not found. Set its location in Settings.",
                GitErrorKind.Unknown, TimeSpan.Zero);
        }

        var psi = new ProcessStartInfo(git.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = request.StandardInput is not null,
        };

        foreach (var arg in GitCliProcessSetup.DeterministicPrefix)
            psi.ArgumentList.Add(arg);
        foreach (var arg in request.Arguments)
            psi.ArgumentList.Add(arg);

        GitCliProcessSetup.ComposeEnvironment(psi, request.Profile, request.ExtraEnvironment);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutDone = new TaskCompletionSource();
        var stderrDone = new TaskCompletionSource();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) { stdoutDone.TrySetResult(); return; }
            stdout.AppendLine(e.Data);
            onStdoutLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) { stderrDone.TrySetResult(); return; }
            stderr.AppendLine(e.Data);
            if (progress is not null && GitProgressParser.TryParse(e.Data, out var gp))
                progress.Report(gp);
        };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new GitCliResult(-1, string.Empty, ex.Message, GitErrorKind.Unknown, stopwatch.Elapsed);
        }

        if (request.LowerPriority)
        {
            // Best-effort: the process may already have exited (or the platform may refuse the
            // change) by the time this runs — never let a priority tweak fail the invocation.
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch { }
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (request.StandardInput is { } stdin)
        {
            try
            {
                var writer = process.StandardInput;
                // Git expects LF-terminated patch text on stdin, not the platform newline.
                writer.NewLine = "\n";
                writer.Write(stdin);
                writer.Close();
            }
            catch (IOException)
            {
                // The process may have exited before consuming stdin; the exit code tells the story.
            }
        }

        using var timeoutCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        if (request.Timeout != Timeout.InfiniteTimeSpan && request.Timeout > TimeSpan.Zero)
            timeoutCts.CancelAfter(request.Timeout);

        var cancelled = false;
        await using (linkedCts.Token.Register(() =>
        {
            cancelled = true;
            GitCliProcessSetup.TryKill(process);
        }))
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                // Drain both pipes fully before reading — never before this point (§6.5.1).
                await Task.WhenAll(stdoutDone.Task, stderrDone.Task).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
        }

        stopwatch.Stop();

        // The caller's own token and the per-request Timeout both trip `cancelled` via the same
        // linked registration — tell them apart so a stalled invocation (killed by the timeout,
        // with nothing in stderr to classify) isn't reported as an opaque Unknown failure.
        if (cancelled && ct.IsCancellationRequested)
        {
            return new GitCliResult(-1, stdout.ToString(), stderr.ToString(),
                GitErrorKind.Cancelled, stopwatch.Elapsed);
        }

        if (cancelled && timeoutCts.IsCancellationRequested)
        {
            return new GitCliResult(-1, stdout.ToString(), stderr.ToString(),
                GitErrorKind.TimedOut, stopwatch.Elapsed);
        }

        var exitCode = SafeExitCode(process);
        var kind = exitCode == 0
            ? GitErrorKind.None
            : GitErrorClassifier.Classify(exitCode, stderr.ToString());

        return new GitCliResult(exitCode, stdout.ToString(), stderr.ToString(), kind, stopwatch.Elapsed);
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }
}
