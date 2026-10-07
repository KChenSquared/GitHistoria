namespace Historia.Services.Ai;

/// <summary>
/// The one HTTP pipeline both AI protocols use: a shared pooled <see cref="HttpClient"/>, a
/// per-attempt timeout inside an overall deadline, a small retry policy for transient statuses,
/// and classification of every failure into <see cref="AiException"/>. Nothing here is ever
/// logged: request and response bodies hold the user's code.
/// </summary>
public sealed class AiHttp
{
    private const int MaxRetries = 2;
    private const int MaxDetailsBytes = 2048;
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(10);

    public static AiHttp Shared { get; } = new();

    private readonly HttpClient _client;
    private readonly TimeSpan _attemptTimeout;
    private readonly TimeSpan _overallDeadline;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public AiHttp(
        HttpMessageHandler? handler = null,
        TimeSpan? attemptTimeout = null,
        TimeSpan? overallDeadline = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _client = new HttpClient(
            handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) },
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(180);
        _overallDeadline = overallDeadline ?? TimeSpan.FromMinutes(4);
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Sends the request built by <paramref name="createRequest"/> (called once per attempt) and
    /// returns the body of the first 2xx response. <paramref name="refine"/> may turn a failed
    /// status and body into a more specific kind. <paramref name="secrets"/> are replaced in the
    /// details of any error.
    /// </summary>
    public async Task<string> SendAsync(
        Func<HttpRequestMessage> createRequest,
        IReadOnlyCollection<string> secrets,
        Func<int, string, AiErrorKind?>? refine,
        CancellationToken ct)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(_overallDeadline);

        for (var attempt = 0; ; attempt++)
        {
            int status;
            string reason;
            string body;
            TimeSpan? retryAfter;
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                attemptCts.CancelAfter(_attemptTimeout);
                using var request = CreateRequest(createRequest);
                using var response = await _client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                    .ConfigureAwait(false);
                body = await response.Content.ReadAsStringAsync(attemptCts.Token).ConfigureAwait(false);
                status = (int)response.StatusCode;
                reason = response.ReasonPhrase ?? "";
                retryAfter = ParseRetryAfter(response);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                throw new AiException(AiErrorKind.Timeout, "The AI service took too long to respond.", null, ex);
            }
            catch (HttpRequestException ex)
            {
                throw new AiException(AiErrorKind.Network,
                    "Couldn't reach the AI service. Check the server address and your connection.",
                    Redact(ex.Message, secrets), ex);
            }

            if (status is >= 200 and < 300)
                return body;

            if (IsTransient(status) && attempt < MaxRetries)
            {
                var wait = retryAfter ?? TimeSpan.FromSeconds(1 << attempt);
                if (wait > MaxRetryAfter)
                    throw Fail(AiErrorKind.RateLimited, status, reason, body, secrets);

                await WaitAsync(wait, overall.Token, ct, status, reason, body, secrets).ConfigureAwait(false);
                continue;
            }

            var kind = refine?.Invoke(status, body) ?? Classify(status, body);
            throw Fail(kind, status, reason, body, secrets);
        }
    }

    /// <summary>Builds one attempt's request and refuses it before sending unless its address is an
    /// absolute http or https URL. A Server such as <c>localhost:11434/v1</c> parses as a URL with
    /// the scheme <c>localhost</c>, and one without a scheme as a relative URL; HttpClient rejects
    /// both with exceptions that say nothing about the Server field.</summary>
    private static HttpRequestMessage CreateRequest(Func<HttpRequestMessage> createRequest)
    {
        HttpRequestMessage request;
        try
        {
            request = createRequest();
        }
        catch (UriFormatException ex)
        {
            throw new AiException(AiErrorKind.InvalidServer, Summary(AiErrorKind.InvalidServer, 0), null, ex);
        }

        if (request.RequestUri is { IsAbsoluteUri: true } uri
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Length > 0)
        {
            return request;
        }

        request.Dispose();
        throw new AiException(AiErrorKind.InvalidServer, Summary(AiErrorKind.InvalidServer, 0));
    }

    private async Task WaitAsync(TimeSpan wait, CancellationToken overall, CancellationToken caller,
        int status, string reason, string body, IReadOnlyCollection<string> secrets)
    {
        try
        {
            await _delay(wait, overall).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw Fail(Classify(status, body), status, reason, body, secrets);
        }
    }

    internal static AiErrorKind Classify(int status, string body) => status switch
    {
        401 or 403 => AiErrorKind.Unauthorized,
        404 => AiErrorKind.NotFound,
        408 => AiErrorKind.Timeout,
        413 => AiErrorKind.ContextTooLarge,
        429 => AiErrorKind.RateLimited,
        400 when MentionsSize(body) => AiErrorKind.ContextTooLarge,
        >= 500 => AiErrorKind.Server,
        _ => AiErrorKind.Unknown,
    };

    private static bool MentionsSize(string body) =>
        body.Contains("context", StringComparison.OrdinalIgnoreCase)
        || body.Contains("token", StringComparison.OrdinalIgnoreCase)
        || body.Contains("too large", StringComparison.OrdinalIgnoreCase)
        || body.Contains("exceeds", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(int status) => status is 408 or 429 or 500 or 502 or 503 or 504;

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }

    private static AiException Fail(AiErrorKind kind, int status, string reason, string body, IReadOnlyCollection<string> secrets) =>
        new(kind, Summary(kind, status), Details(status, reason, body, secrets));

    internal static string Summary(AiErrorKind kind, int status) => kind switch
    {
        AiErrorKind.InvalidServer => "The Server address isn't a valid http:// or https:// URL. Check the Server in AI Integrations.",
        AiErrorKind.Unauthorized => "The AI service rejected the API key.",
        AiErrorKind.NotFound => "The AI service address or model wasn't found. Check the Server and Model in AI Integrations.",
        AiErrorKind.ModelNotFound => "The AI service doesn't have the selected model.",
        AiErrorKind.RateLimited => "The AI service is rate limiting requests. Try again in a moment.",
        AiErrorKind.ContextTooLarge => "The staged changes are too large for this model. Set a Diff size limit for this AI integration.",
        AiErrorKind.Server => "The AI service had an internal error. Try again in a moment.",
        AiErrorKind.Timeout => "The AI service took too long to respond.",
        AiErrorKind.UnsupportedOption => "The model doesn't accept this Reasoning Effort Level. Choose another level, or unspecified, in AI Integrations.",
        _ => $"The AI service returned an error (HTTP {status}).",
    };

    private static string Details(int status, string reason, string body, IReadOnlyCollection<string> secrets)
    {
        // Redact before truncating so a secret cut by the limit can't survive as a fragment.
        var text = Redact($"HTTP {status} {reason}\n{body}", secrets);
        if (Encoding.UTF8.GetByteCount(text) > MaxDetailsBytes)
        {
            // Cut on a character boundary: back off while the first excluded byte continues a
            // multi-byte sequence, so the kept bytes never end in half a character.
            var bytes = Encoding.UTF8.GetBytes(text);
            var cut = MaxDetailsBytes;
            while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
                cut--;
            text = Encoding.UTF8.GetString(bytes, 0, cut) + "…";
        }
        return text;
    }

    /// <summary>Replaces every secret with <c>•••</c>.</summary>
    internal static string Redact(string text, IReadOnlyCollection<string> secrets)
    {
        foreach (var secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
                text = text.Replace(secret, "•••", StringComparison.Ordinal);
        }
        return text;
    }
}
