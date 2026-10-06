namespace Historia.Services.Ai;

/// <summary>
/// OpenAI-compatible chat completions: <c>POST {server}/chat/completions</c> and
/// <c>GET {server}/models</c>, with an optional bearer key (a local Ollama needs none).
/// </summary>
public sealed class OpenAiCompatibleClient(AiHttp http) : IAiChatClient
{
    public async Task<IReadOnlyList<string>> ListModelsAsync(AiChatRequest request, CancellationToken ct)
    {
        var body = await http.SendAsync(
            () => Create(HttpMethod.Get, $"{Base(request)}/models", request, null),
            Secrets(request), null, ct).ConfigureAwait(false);

        var root = Parse(body);
        var data = root["data"] as JsonArray
            ?? throw new AiException(AiErrorKind.BadResponse, "The AI service returned a model list in an unexpected format.");
        return data
            .Select(n => n?["id"]?.GetValue<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task<string> CompleteAsync(AiChatRequest request, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = request.Prompt }),
        };
        if (!string.Equals(request.ReasoningEffort, AiReasoningEffort.Unspecified, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(request.ReasoningEffort))
            payload["reasoning_effort"] = request.ReasoningEffort;

        var json = payload.ToJsonString();
        var body = await http.SendAsync(
            () => Create(HttpMethod.Post, $"{Base(request)}/chat/completions", request, json),
            Secrets(request), null, ct).ConfigureAwait(false);

        var root = Parse(body);
        var choice = (root["choices"] as JsonArray)?.FirstOrDefault()
            ?? throw new AiException(AiErrorKind.BadResponse, "The AI service returned a reply in an unexpected format.");

        switch (choice["finish_reason"]?.GetValue<string>())
        {
            case "length":
                throw new AiException(AiErrorKind.Truncated, "The reply was cut off at the model's output limit.");
            case "content_filter":
                throw new AiException(AiErrorKind.ContentFiltered, "The AI service withheld the reply because of a content filter.");
        }

        return ReadContent(choice["message"]?["content"]);
    }

    private static string ReadContent(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        if (content is JsonArray parts)
            return string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? ""));
        return "";
    }

    private static string Base(AiChatRequest request) => request.Server.Trim().TrimEnd('/');

    private static IReadOnlyCollection<string> Secrets(AiChatRequest request) =>
        [.. AiExtraHeaders.Values(request.ExtraHeaders), .. (request.ApiKey is { Length: > 0 } k ? new[] { k } : [])];

    private static HttpRequestMessage Create(HttpMethod method, string url, AiChatRequest request, string? json)
    {
        var message = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(request.ApiKey))
            message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {request.ApiKey}");
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
            throw new AiException(AiErrorKind.BadResponse, "The AI service returned something that isn't JSON. Check the Server address.", null, ex);
        }
    }
}
