namespace Historia.Tests;

[Trait("tier", "unit")]
public class AiClientTests
{
    private const string Host = "ai.example.test";
    private static readonly string Server = $"https://{Host}/v1";
    private static readonly string GeminiServer = $"https://{Host}/v1beta";

    private sealed record Seen(HttpMethod Method, Uri Uri, HttpRequestHeaders Headers, string? Body);

    /// <summary>Replays queued responses and records what was sent.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        public List<Seen> Requests { get; } = [];

        public FakeHandler Enqueue(HttpStatusCode status, string body, Action<HttpResponseMessage>? configure = null)
        {
            _responses.Enqueue(() =>
            {
                var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                configure?.Invoke(r);
                return r;
            });
            return this;
        }

        public FakeHandler EnqueueThrow(Exception ex)
        {
            _responses.Enqueue(() => throw ex);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new Seen(request.Method, request.RequestUri!, request.Headers, body));
            return _responses.Dequeue()();
        }
    }

    private static AiHttp Http(FakeHandler handler, TimeSpan? overall = null, List<TimeSpan>? delays = null) =>
        new(handler, overallDeadline: overall, delay: (wait, ct) =>
        {
            delays?.Add(wait);
            return overall is null ? Task.CompletedTask : Task.Delay(wait, ct);
        });

    private static AiChatRequest Req(string server, string? key = "sk-secret", string model = "m1",
        string effort = "unspecified", string headers = "") => new(server, key, model, effort, headers, "PROMPT");

    private const string OpenAiReply = """{"choices":[{"finish_reason":"stop","message":{"content":"feat: hi"}}]}""";
    private const string GeminiReply = """{"candidates":[{"finishReason":"STOP","content":{"parts":[{"text":"feat: "},{"text":"hi"}]}}]}""";

    // ── OpenAI-compatible ──

    [Fact]
    public async Task OpenAi_posts_the_chat_completion_with_a_bearer_key()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, OpenAiReply);

        var text = await new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req($"{Server}/"), default);

        Assert.Equal("feat: hi", text);
        var seen = Assert.Single(h.Requests);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal($"{Server}/chat/completions", seen.Uri.ToString());
        Assert.Equal("Bearer sk-secret", seen.Headers.Authorization!.ToString());
        var json = JsonNode.Parse(seen.Body!)!;
        Assert.Equal("m1", json["model"]!.GetValue<string>());
        Assert.Equal("user", json["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Equal("PROMPT", json["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Null(json["reasoning_effort"]);
    }

    [Fact]
    public async Task OpenAi_omits_the_bearer_header_when_the_key_is_blank()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, OpenAiReply);

        await new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server, key: ""), default);

        Assert.Null(h.Requests[0].Headers.Authorization);
    }

    [Fact]
    public async Task OpenAi_sends_reasoning_effort_unless_unspecified()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, OpenAiReply);

        await new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server, effort: "high"), default);

        Assert.Equal("high", JsonNode.Parse(h.Requests[0].Body!)!["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenAi_applies_extra_headers_but_never_overrides_auth()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, OpenAiReply);

        await new OpenAiCompatibleClient(Http(h)).CompleteAsync(
            Req(Server, headers: "X-Team: core\nAuthorization: Bearer evil\nno colon\n: nameless\nEmpty:"), default);

        var seen = h.Requests[0];
        Assert.Equal("Bearer sk-secret", seen.Headers.Authorization!.ToString());
        Assert.Equal("core", Assert.Single(seen.Headers.GetValues("X-Team")));
        Assert.False(seen.Headers.Contains("Empty"));
    }

    [Fact]
    public async Task OpenAi_lists_models_from_data()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, """{"data":[{"id":"a"},{"id":"b"},{"id":"a"}]}""");

        var models = await new OpenAiCompatibleClient(Http(h)).ListModelsAsync(Req(Server), default);

        Assert.Equal(["a", "b"], models);
        Assert.Equal(HttpMethod.Get, h.Requests[0].Method);
        Assert.Equal($"{Server}/models", h.Requests[0].Uri.ToString());
    }

    [Theory]
    [InlineData("length", AiErrorKind.Truncated)]
    [InlineData("content_filter", AiErrorKind.ContentFiltered)]
    public async Task OpenAi_maps_finish_reasons(string reason, AiErrorKind kind)
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK,
            $$$"""{"choices":[{"finish_reason":"{{{reason}}}","message":{"content":"x"}}]}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));
        Assert.Equal(kind, ex.Kind);
    }

    [Fact]
    public async Task OpenAi_html_reply_is_a_bad_response()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, "<html>nope</html>");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));
        Assert.Equal(AiErrorKind.BadResponse, ex.Kind);
    }

    // ── Gemini ──

    [Fact]
    public async Task Gemini_posts_generateContent_with_the_key_in_a_header_never_the_query()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, GeminiReply);

        var text = await new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer, model: "gemini-2.5-flash"), default);

        Assert.Equal("feat: hi", text);
        var seen = Assert.Single(h.Requests);
        Assert.Equal($"{GeminiServer}/models/gemini-2.5-flash:generateContent", seen.Uri.ToString());
        Assert.Equal("sk-secret", Assert.Single(seen.Headers.GetValues("x-goog-api-key")));
        Assert.DoesNotContain("key=", seen.Uri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-secret", seen.Uri.ToString());
        var json = JsonNode.Parse(seen.Body!)!;
        Assert.Equal("PROMPT", json["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Null(json["generationConfig"]);
    }

    [Theory]
    [InlineData("low", "thinkingLevel", "low")]
    [InlineData("high", "thinkingLevel", "high")]
    public async Task Gemini_maps_reasoning_effort_to_a_thinking_level(string effort, string field, string expected)
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, GeminiReply);

        await new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer, effort: effort), default);

        var thinking = JsonNode.Parse(h.Requests[0].Body!)!["generationConfig"]!["thinkingConfig"]!;
        Assert.Equal(expected, thinking[field]!.GetValue<string>());
    }

    [Fact]
    public async Task Gemini_none_turns_thinking_off_with_a_zero_budget()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, GeminiReply);

        await new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer, effort: "none"), default);

        var thinking = JsonNode.Parse(h.Requests[0].Body!)!["generationConfig"]!["thinkingConfig"]!;
        Assert.Equal(0, thinking["thinkingBudget"]!.GetValue<int>());
    }

    [Fact]
    public async Task Gemini_accepts_a_model_with_the_models_prefix()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, GeminiReply);

        await new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer, model: "models/gemini-2.5-flash"), default);

        Assert.EndsWith("/models/gemini-2.5-flash:generateContent", h.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Gemini_lists_generateContent_models_across_pages_and_strips_the_prefix()
    {
        var h = new FakeHandler()
            .Enqueue(HttpStatusCode.OK, """
                {"models":[
                  {"name":"models/gemini-2.5-flash","supportedGenerationMethods":["generateContent","countTokens"]},
                  {"name":"models/embedding-001","supportedGenerationMethods":["embedContent"]}],
                 "nextPageToken":"tok 1"}
                """)
            .Enqueue(HttpStatusCode.OK, """
                {"models":[{"name":"models/gemini-2.5-pro","supportedGenerationMethods":["generateContent"]}]}
                """);

        var models = await new GeminiClient(Http(h)).ListModelsAsync(Req(GeminiServer), default);

        Assert.Equal(["gemini-2.5-flash", "gemini-2.5-pro"], models);
        Assert.Equal(2, h.Requests.Count);
        Assert.Equal($"{GeminiServer}/models?pageSize=1000", h.Requests[0].Uri.ToString());
        Assert.Equal($"{GeminiServer}/models?pageSize=1000&pageToken=tok%201", h.Requests[1].Uri.OriginalString);
        Assert.All(h.Requests, r => Assert.DoesNotContain("key=", r.Uri.Query, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("SAFETY")]
    [InlineData("PROHIBITED_CONTENT")]
    public async Task Gemini_safety_finish_is_content_filtered(string finish)
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, $$"""{"candidates":[{"finishReason":"{{finish}}"}]}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));
        Assert.Equal(AiErrorKind.ContentFiltered, ex.Kind);
    }

    [Fact]
    public async Task Gemini_a_blocked_prompt_is_content_filtered()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK, """{"promptFeedback":{"blockReason":"SAFETY"}}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));
        Assert.Equal(AiErrorKind.ContentFiltered, ex.Kind);
    }

    [Fact]
    public async Task Gemini_max_tokens_is_truncated()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.OK,
            """{"candidates":[{"finishReason":"MAX_TOKENS","content":{"parts":[{"text":"half"}]}}]}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));
        Assert.Equal(AiErrorKind.Truncated, ex.Kind);
    }

    [Fact]
    public async Task Gemini_a_NOT_FOUND_json_body_is_a_missing_model()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.NotFound,
            """{"error":{"code":404,"message":"models/gemini-1.0-pro is not found for API version v1beta","status":"NOT_FOUND"}}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));
        Assert.Equal(AiErrorKind.ModelNotFound, ex.Kind);
    }

    [Fact]
    public async Task Gemini_an_html_404_is_a_wrong_server_not_a_missing_model()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.NotFound, "<html>404</html>");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));
        Assert.Equal(AiErrorKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task Gemini_a_400_saying_the_model_is_not_supported_is_a_missing_model()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.BadRequest,
            """{"error":{"code":400,"message":"Model x is not supported for generateContent.","status":"INVALID_ARGUMENT"}}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).CompleteAsync(Req(GeminiServer), default));
        Assert.Equal(AiErrorKind.ModelNotFound, ex.Kind);
    }

    [Fact]
    public async Task Gemini_listing_never_classifies_a_404_as_a_missing_model()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.NotFound,
            """{"error":{"code":404,"message":"nope","status":"NOT_FOUND"}}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new GeminiClient(Http(h)).ListModelsAsync(Req(GeminiServer), default));
        Assert.Equal(AiErrorKind.NotFound, ex.Kind);
    }

    // ── Shared pipeline ──

    [Fact]
    public async Task A_429_with_retry_after_is_retried_then_succeeds()
    {
        var delays = new List<TimeSpan>();
        var h = new FakeHandler()
            .Enqueue((HttpStatusCode)429, "slow down", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1)))
            .Enqueue(HttpStatusCode.OK, OpenAiReply);

        var text = await new OpenAiCompatibleClient(Http(h, delays: delays)).CompleteAsync(Req(Server), default);

        Assert.Equal("feat: hi", text);
        Assert.Equal(2, h.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1)], delays);
    }

    [Fact]
    public async Task Transient_failures_back_off_exponentially_for_two_retries_then_fail()
    {
        var delays = new List<TimeSpan>();
        var h = new FakeHandler()
            .Enqueue(HttpStatusCode.ServiceUnavailable, "a")
            .Enqueue(HttpStatusCode.ServiceUnavailable, "b")
            .Enqueue(HttpStatusCode.ServiceUnavailable, "c");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h, delays: delays)).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.Server, ex.Kind);
        Assert.Equal(3, h.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task A_retry_after_longer_than_ten_seconds_fails_as_rate_limited_without_waiting()
    {
        var delays = new List<TimeSpan>();
        var h = new FakeHandler()
            .Enqueue((HttpStatusCode)429, "x", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)));

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h, delays: delays)).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.RateLimited, ex.Kind);
        Assert.Single(h.Requests);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task A_400_is_not_retried()
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.BadRequest, "bad");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.Unknown, ex.Kind);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task The_overall_deadline_stops_retrying()
    {
        var h = new FakeHandler()
            .Enqueue((HttpStatusCode)429, "x", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5)))
            .Enqueue(HttpStatusCode.OK, OpenAiReply);

        var ex = await Assert.ThrowsAsync<AiException>(
            () => new OpenAiCompatibleClient(Http(h, overall: TimeSpan.FromMilliseconds(100))).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.RateLimited, ex.Kind);
        Assert.Single(h.Requests);
    }

    [Theory]
    [InlineData(401, AiErrorKind.Unauthorized)]
    [InlineData(403, AiErrorKind.Unauthorized)]
    [InlineData(404, AiErrorKind.NotFound)]
    [InlineData(413, AiErrorKind.ContextTooLarge)]
    [InlineData(418, AiErrorKind.Unknown)]
    public async Task Statuses_map_to_kinds(int status, AiErrorKind kind)
    {
        var h = new FakeHandler().Enqueue((HttpStatusCode)status, "{}");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));

        Assert.Equal(kind, ex.Kind);
    }

    [Fact]
    public async Task A_413_suggests_a_diff_size_limit()
    {
        var h = new FakeHandler().Enqueue((HttpStatusCode)413, "{}");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));

        Assert.Contains("Diff size limit", ex.Message);
    }

    [Theory]
    [InlineData("This model's maximum context length is 8192 tokens")]
    [InlineData("request too large")]
    [InlineData("input exceeds the limit")]
    public async Task A_400_that_mentions_size_is_context_too_large(string message)
    {
        var h = new FakeHandler().Enqueue(HttpStatusCode.BadRequest, $$$"""{"error":{"message":"{{{message}}}"}}""");

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.ContextTooLarge, ex.Kind);
    }

    [Fact]
    public async Task A_connection_failure_is_a_network_error()
    {
        var h = new FakeHandler().EnqueueThrow(new HttpRequestException("no route"));

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.Network, ex.Kind);
    }

    [Fact]
    public async Task An_attempt_that_outlives_its_timeout_is_a_timeout()
    {
        var http = new AiHttp(new HangingHandler(), attemptTimeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(http).CompleteAsync(Req(Server), default));

        Assert.Equal(AiErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_as_cancellation_not_an_error()
    {
        var http = new AiHttp(new HangingHandler());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new OpenAiCompatibleClient(http).CompleteAsync(Req(Server), cts.Token));
    }

    [Fact]
    public async Task Details_are_truncated_and_have_every_secret_replaced()
    {
        var body = "bad key sk-secret and header-secret " + new string('x', 5000);
        var h = new FakeHandler().Enqueue(HttpStatusCode.Unauthorized, body);

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(
            Req(Server, headers: "X-Org: header-secret"), default));

        Assert.NotNull(ex.Details);
        Assert.DoesNotContain("sk-secret", ex.Details);
        Assert.DoesNotContain("header-secret", ex.Details);
        Assert.Contains("•••", ex.Details);
        Assert.True(Encoding.UTF8.GetByteCount(ex.Details) < 2100);
        Assert.DoesNotContain("sk-secret", ex.Message);
    }

    [Fact]
    public async Task A_truncated_secret_cannot_survive_as_a_fragment()
    {
        var key = "sk-" + new string('k', 40);
        var body = new string('x', 2040) + key;
        var h = new FakeHandler().Enqueue(HttpStatusCode.Unauthorized, body);

        var ex = await Assert.ThrowsAsync<AiException>(() => new OpenAiCompatibleClient(Http(h)).CompleteAsync(Req(Server, key: key), default));

        Assert.DoesNotContain("sk-kkk", ex.Details);
    }

    [Fact]
    public void The_factory_picks_the_client_for_each_kind()
    {
        var factory = new AiClientFactory(new AiHttp(new FakeHandler()));

        Assert.IsType<OpenAiCompatibleClient>(factory.For(AiServiceKind.Custom));
        Assert.IsType<GeminiClient>(factory.For(AiServiceKind.Gemini));
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }
    }
}
