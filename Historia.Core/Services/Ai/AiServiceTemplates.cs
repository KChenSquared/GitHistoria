namespace Historia.Services.Ai;

/// <summary>
/// The templates an AI integration can start from. Custom is blank; Gemini fills the connection
/// for Google's native API and never touches the API key.
/// </summary>
public static class AiServiceTemplates
{
    public const string DefaultName = "Unnamed Service";
    public const string GeminiName = "Gemini";
    public const string GeminiServer = "https://generativelanguage.googleapis.com/v1beta";

    public static AiServiceProfile NewCustom(DateTime now) => new()
    {
        Name = DefaultName,
        Kind = AiServiceKind.Custom,
        CreatedUtc = now,
    };

    /// <summary>Switches to the Gemini protocol and fills Server and Model. Name is replaced only
    /// when it is blank or still the default. The API key is left alone.</summary>
    public static AiServiceProfile ApplyGemini(AiServiceProfile p) => p with
    {
        Kind = AiServiceKind.Gemini,
        Name = IsDefaultName(p.Name) ? GeminiName : p.Name,
        Server = GeminiServer,
        Model = "",
        AutoFetchModels = true,
    };

    /// <summary>Switches the protocol only; every field is kept.</summary>
    public static AiServiceProfile ApplyCustom(AiServiceProfile p) => p with { Kind = AiServiceKind.Custom };

    /// <summary>True when applying the template would replace a Server or Model the user set.</summary>
    public static bool WouldOverwrite(AiServiceProfile p, AiServiceKind kind)
    {
        if (kind != AiServiceKind.Gemini)
            return false;
        var serverDiffers = !string.IsNullOrWhiteSpace(p.Server)
            && !string.Equals(p.Server.Trim(), GeminiServer, StringComparison.Ordinal);
        return serverDiffers || !string.IsNullOrWhiteSpace(p.Model);
    }

    private static bool IsDefaultName(string name) =>
        string.IsNullOrWhiteSpace(name) || string.Equals(name, DefaultName, StringComparison.Ordinal);
}
