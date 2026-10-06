namespace Historia.Services.Ai;

/// <summary>
/// The native Gemini REST API: <c>POST {server}/models/{model}:generateContent</c> and
/// <c>GET {server}/models</c>. The key travels in the <c>x-goog-api-key</c> header, never in the
/// query string, so it can't end up in a logged URL.
/// </summary>
public sealed class GeminiClient(AiHttp http) : IAiChatClient
{
    private const int MaxListPages = 5;

    public async Task<IReadOnlyList<string>> ListModelsAsync(AiChatRequest request, CancellationToken ct)
    {
        var ids = new List<string>();
        string? pageToken = null;
        for (var page = 0; page < MaxListPages; page++)
        {
            var url = $"{Base(request)}/models?pageSize=1000"
                + (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            var body = await http.SendAsync(() => Create(HttpMethod.Get, url, request, null), Secrets(request), null, ct)
                .ConfigureAwait(false);

            var root = Parse(body);
            foreach (var model in root["models"] as JsonArray ?? [])
            {
                var name = model?["name"]?.GetValue<string>();
                var methods = model?["supportedGenerationMethods"] as JsonArray;
                if (string.IsNullOrWhiteSpace(name)
                    || methods is null
                    || !methods.Any(m => m?.GetValue<string>() == "generateContent"))
                    continue;
                ids.Add(StripPrefix(name));
            }

            pageToken = root["nextPageToken"]?.GetValue<string>();
            if (string.IsNullOrEmpty(pageToken))
                break;
        }
        return ids.Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task<string> CompleteAsync(AiChatRequest request, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = request.Prompt }),
            }),
        };
        if (ThinkingConfig(request.ReasoningEffort) is { } thinking)
            payload["generationConfig"] = new JsonObject { ["thinkingConfig"] = thinking };

        var url = $"{Base(request)}/models/{Uri.EscapeDataString(StripPrefix(request.Model))}:generateContent";
        var json = payload.ToJsonString();
        var body = await http.SendAsync(
            () => Create(HttpMethod.Post, url, request, json),
            Secrets(request), RefineModelNotFound, ct).ConfigureAwait(false);

        var root = Parse(body);

        if (root["promptFeedback"]?["blockReason"]?.GetValue<string>() is { Length: > 0 })
            throw new AiException(AiErrorKind.ContentFiltered, "Gemini declined the request because of its safety filters.");

        var candidate = (root["candidates"] as JsonArray)?.FirstOrDefault();
        var finish = candidate?["finishReason"]?.GetValue<string>();
        if (finish is "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII")
            throw new AiException(AiErrorKind.ContentFiltered, "Gemini withheld the reply because of its safety filters.");
        if (finish == "MAX_TOKENS")
            throw new AiException(AiErrorKind.Truncated, "The reply was cut off at the model's output limit.");

        var parts = candidate?["content"]?["parts"] as JsonArray;
        if (candidate is null || parts is null)
            return "";

        return string.Concat(parts
            .Where(p => p?["thought"]?.GetValue<bool>() != true)
            .Select(p => p?["text"]?.GetValue<string>() ?? ""));
    }

    /// <summary>
    /// A retired or unknown model reports <c>NOT_FOUND</c> in the JSON error body, or a 400 that
    /// says the model isn't found or isn't supported for <c>generateContent</c>. A 404 that isn't
    /// that JSON is a wrong server address and stays a plain not-found.
    /// </summary>
    internal static AiErrorKind? RefineModelNotFound(int status, string body)
    {
        if (status is not (400 or 404))
            return null;

        JsonNode? error;
        try
        {
            error = JsonNode.Parse(body)?["error"];
        }
        catch (JsonException)
        {
            return null;
        }
        if (error is null)
            return null;

        var code = error["code"]?.ToString();
        var statusText = error["status"]?.GetValue<string>();
        var message = error["message"]?.GetValue<string>() ?? "";

        if (status == 404 && (statusText == "NOT_FOUND" || code == "model_not_found"))
            return AiErrorKind.ModelNotFound;

        if (status == 400
            && message.Contains("model", StringComparison.OrdinalIgnoreCase)
            && (message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || message.Contains("not supported", StringComparison.OrdinalIgnoreCase)))
            return AiErrorKind.ModelNotFound;

        return null;
    }

    /// <summary>None switches thinking off; the other levels use <c>thinkingLevel</c>.
    /// Unspecified sends nothing.</summary>
    private static JsonObject? ThinkingConfig(string effort)
    {
        if (string.IsNullOrWhiteSpace(effort)
            || string.Equals(effort, AiReasoningEffort.Unspecified, StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.Equals(effort, "none", StringComparison.OrdinalIgnoreCase))
            return new JsonObject { ["thinkingBudget"] = 0 };
        return new JsonObject { ["thinkingLevel"] = effort.ToLowerInvariant() };
    }

    private static string StripPrefix(string name) =>
        name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name;

    private static string Base(AiChatRequest request) => request.Server.Trim().TrimEnd('/');

    private static IReadOnlyCollection<string> Secrets(AiChatRequest request) =>
        [.. AiExtraHeaders.Values(request.ExtraHeaders), .. (request.ApiKey is { Length: > 0 } k ? new[] { k } : [])];

    private static HttpRequestMessage Create(HttpMethod method, string url, AiChatRequest request, string? json)
    {
        var message = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(request.ApiKey))
            message.Headers.TryAddWithoutValidation("x-goog-api-key", request.ApiKey);
        AiExtraHeaders.Apply(message, request.ExtraHeaders);
        if (json is not null)
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return message;
    }

    private static JsonNode Parse(string body)
    {
        try
        {
            return JsonNode.Parse(body) ?? throw new JsonException();
        }
        catch (JsonException ex)
        {
            throw new AiException(AiErrorKind.BadResponse, "Gemini returned something that isn't JSON. Check the Server address.", null, ex);
        }
    }
}
