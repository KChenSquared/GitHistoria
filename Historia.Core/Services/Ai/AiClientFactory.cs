namespace Historia.Services.Ai;

/// <inheritdoc cref="IAiClientFactory"/>
public sealed class AiClientFactory(AiHttp? http = null) : IAiClientFactory
{
    private readonly AiHttp _http = http ?? AiHttp.Shared;

    public IAiChatClient For(AiServiceKind kind) => kind switch
    {
        AiServiceKind.Gemini => new GeminiClient(_http),
        _ => new OpenAiCompatibleClient(_http),
    };
}
