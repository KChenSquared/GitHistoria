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

        await _locator.WhenResolvedAsync(ct).ConfigureAwait(false);

        if (!AppLog.Enabled)
            return await RunCoreAsync(request, progress, onStdoutLine, ct).ConfigureAwait(false);

        var op = AppLog.Operation("git-cli", $"git {string.Join(' ', request.Arguments)}  (cwd={request.WorkingDirectory})");
        try
        {
            var result = await RunCoreAsync(request, progress, onStdoutLine, ct).ConfigureAwait(false);
            op.Outcome($"exit={result.ExitCode} kind={result.ErrorKind} ({result.Duration.TotalMilliseconds:F0}ms)");
            if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError))
                AppLog.Write(AppLogLevel.Warn, "git-cli", GitStderr.SummaryLine(result.StandardError));
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
        var raw = request.RawStandardOutput;
        if (raw && (request.StreamOutput || onStdoutLine is not null))
            throw new ArgumentException("Raw stdout capture can't also stream stdout lines.", nameof(request));

        var git = _locator.Current;
        if (!git.Found || git.Path is null)
        {
            return new GitCliResult(-1, string.Empty,
                git.NotFoundReason ?? "git.exe was not found. Set its location in Settings.",
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
        var rawStdout = raw ? new MemoryStream() : null;
        var stdoutDone = new TaskCompletionSource();
        var stderrDone = new TaskCompletionSource();
        var outputTruncated = false;
        long lineBufferedBytes = 0;

        if (!raw)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) { stdoutDone.TrySetResult(); return; }
                if (outputTruncated) return;

                var lineBytes = Utf8NoBom.GetByteCount(e.Data) + Environment.NewLine.Length;
                if (request.MaxStandardOutputBytes is { } max && lineBufferedBytes + lineBytes > max)
                {
                    outputTruncated = true;
                    GitCliProcessSetup.TryKill(process);
                    stdoutDone.TrySetResult();
                    return;
                }
                lineBufferedBytes += lineBytes;
                stdout.AppendLine(e.Data);
                onStdoutLine?.Invoke(e.Data);
            };
        }
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

        // Both streams drain concurrently from here on — in raw mode stdout is copied
        // as bytes instead of being read line by line.
        Task rawCopyTask = Task.CompletedTask;
        if (raw)
        {
            rawCopyTask = CopyRawAsync(
                process.StandardOutput.BaseStream,
                rawStdout!,
                request.MaxStandardOutputBytes,
                process,
                () => outputTruncated = true);
        }
        var stdoutDrained = raw ? rawCopyTask : stdoutDone.Task;
        if (!raw)
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
                // Drain both pipes fully before reading — never before this point.
                await Task.WhenAll(stdoutDrained, stderrDone.Task).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
        }

        stopwatch.Stop();

        var stdoutBytes = rawStdout?.ToArray();
        var stdoutText = stdoutBytes is not null ? Utf8NoBom.GetString(stdoutBytes) : stdout.ToString();
        GitCliResult Result(int exitCode, GitErrorKind kind) =>
            new(exitCode, stdoutText, stderr.ToString(), kind, stopwatch.Elapsed)
            {
                StandardOutputBytes = stdoutBytes,
                OutputTruncated = outputTruncated,
            };

        // The caller's own token and the per-request Timeout both trip `cancelled` via the same
        // linked registration — tell them apart so a stalled invocation (killed by the timeout,
        // with nothing in stderr to classify) isn't reported as an opaque Unknown failure.
        if (cancelled && ct.IsCancellationRequested)
            return Result(-1, GitErrorKind.Cancelled);

        if (cancelled && timeoutCts.IsCancellationRequested)
            return Result(-1, GitErrorKind.TimedOut);

        var exitCode = SafeExitCode(process);
        var kind = exitCode == 0 || outputTruncated
            ? GitErrorKind.None
            : GitErrorClassifier.Classify(exitCode, stderr.ToString());

        return Result(exitCode, kind);
    }

    /// <summary>Copies raw stdout to <paramref name="into"/> until git closes the pipe or
    /// <paramref name="maxBytes"/> is exceeded. A pipe broken by a kill (cancel / timeout /
    /// truncation) just ends the copy — whatever arrived is kept.</summary>
    private static async Task CopyRawAsync(
        Stream stdout,
        MemoryStream into,
        long? maxBytes,
        Process process,
        Action onTruncated)
    {
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await stdout.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read == 0) break;

                if (maxBytes is { } max)
                {
                    var remaining = max - into.Length;
                    if (remaining <= 0)
                    {
                        onTruncated();
                        GitCliProcessSetup.TryKill(process);
                        break;
                    }

                    var toWrite = (int)Math.Min(read, remaining);
                    into.Write(buffer, 0, toWrite);
                    if (read > toWrite)
                    {
                        onTruncated();
                        GitCliProcessSetup.TryKill(process);
                        break;
                    }
                }
                else
                {
                    into.Write(buffer, 0, read);
                }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }
}
