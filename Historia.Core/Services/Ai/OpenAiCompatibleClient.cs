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
            () => AiHttp.CreateJsonRequest(HttpMethod.Get, $"{Base(request)}/models", request, null, m => { if (!string.IsNullOrEmpty(request.ApiKey)) m.Headers.TryAddWithoutValidation("Authorization", $"Bearer {request.ApiKey}"); }),
            AiHttp.Secrets(request), null, ct).ConfigureAwait(false);

        var root = AiHttp.ParseJson(body, "The AI service");
        try
        {
            var data = root["data"] as JsonArray
                ?? throw new AiException(AiErrorKind.BadResponse, "The AI service returned a model list in an unexpected format.");
            return data
                .Select(n => n?["id"]?.GetValue<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (InvalidOperationException ex)
        {
            throw new AiException(AiErrorKind.BadResponse, "The AI service returned a model list in an unexpected format.", null, ex);
        }
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
            () => AiHttp.CreateJsonRequest(HttpMethod.Post, $"{Base(request)}/chat/completions", request, json, m => { if (!string.IsNullOrEmpty(request.ApiKey)) m.Headers.TryAddWithoutValidation("Authorization", $"Bearer {request.ApiKey}"); }),
            AiHttp.Secrets(request), RefineError, ct).ConfigureAwait(false);

        try
        {
            return ReadReply(body);
        }
        catch (InvalidOperationException ex)
        {
            throw new AiException(AiErrorKind.BadResponse, "The AI service returned a reply in an unexpected format.", null, ex);
        }
    }

    /// <summary>A 400 that names the reasoning effort means the model doesn't take that level.</summary>
    internal static AiErrorKind? RefineError(int status, string body) =>
        status == 400
        && (body.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase)
            || body.Contains("reasoning effort", StringComparison.OrdinalIgnoreCase))
            ? AiErrorKind.UnsupportedOption
            : null;

    private static string ReadReply(string body)
    {
        var root = AiHttp.ParseJson(body, "The AI service");
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


}
