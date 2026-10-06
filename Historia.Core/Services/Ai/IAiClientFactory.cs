namespace Historia.Services.Ai;

public interface IAiClientFactory
{
    IAiChatClient For(AiServiceKind kind);
}
