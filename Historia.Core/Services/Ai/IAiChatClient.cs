namespace Historia.Services.Ai;

/// <summary>Everything one request to an AI service needs. <see cref="ApiKey"/> may be null for a
/// keyless server such as a local Ollama.</summary>
public sealed record AiChatRequest(
    string Server,
    string? ApiKey,
    string Model,
    string ReasoningEffort,
    string ExtraHeaders,
    string Prompt);

/// <summary>One protocol's client. Every call is made only because the user clicked something;
/// nothing here runs at startup. Failures are <see cref="AiException"/>; caller cancellation
/// surfaces as <see cref="OperationCanceledException"/>.</summary>
public interface IAiChatClient
{
    /// <summary>The model ids the service offers for chat.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(AiChatRequest request, CancellationToken ct);

    /// <summary>Sends <see cref="AiChatRequest.Prompt"/> as one user message and returns the reply text.</summary>
    Task<string> CompleteAsync(AiChatRequest request, CancellationToken ct);
}
