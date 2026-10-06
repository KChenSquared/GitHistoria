namespace Historia.Services.Ai;

/// <summary>What went wrong talking to an AI service, so the UI can say something useful.</summary>
public enum AiErrorKind
{
    Unknown,

    /// <summary>No AI integration is active.</summary>
    NotConfigured,
    MissingApiKey,

    /// <summary>Nothing is staged, so there is nothing to describe.</summary>
    NothingStaged,

    /// <summary>A Custom profile has no model chosen.</summary>
    NoModel,

    /// <summary>401 or 403.</summary>
    Unauthorized,

    /// <summary>404 for the server or path (not a retired model).</summary>
    NotFound,

    /// <summary>The service reports that the model does not exist or is retired.</summary>
    ModelNotFound,

    /// <summary>The model is gone and no replacement could be found or used.</summary>
    ModelUnavailable,
    RateLimited,

    /// <summary>The prompt is larger than the model or the server accepts.</summary>
    ContextTooLarge,
    Server,
    Network,
    Timeout,

    /// <summary>The response was not the JSON shape the protocol promises.</summary>
    BadResponse,

    /// <summary>The service rejected a request option (today, the reasoning effort).</summary>
    UnsupportedOption,

    /// <summary>The reply was cut off at the model's output limit.</summary>
    Truncated,
    ContentFiltered,
    EmptyResponse,
}

/// <summary>
/// A classified AI failure. <see cref="Exception.Message"/> is the short, user-facing summary;
/// <see cref="Details"/> is the status line and the response body truncated to 2 KB with every
/// secret replaced, for a Details expander.
/// </summary>
public sealed class AiException(AiErrorKind kind, string summary, string? details = null, Exception? inner = null)
    : Exception(summary, inner)
{
    public AiErrorKind Kind { get; } = kind;

    public string? Details { get; } = details;
}
