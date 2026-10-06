namespace Historia.Models;

/// <summary>The protocol an AI integration speaks, which is also its template.</summary>
public enum AiServiceKind
{
    /// <summary>OpenAI-compatible chat completions (OpenAI, Azure's v1 endpoint, Ollama, …).</summary>
    Custom,

    /// <summary>The native Gemini REST API.</summary>
    Gemini,
}

/// <summary>The reasoning-effort choices a profile offers.</summary>
public static class AiReasoningEffort
{
    /// <summary>Sent to the service only when it is not <see cref="Unspecified"/>.</summary>
    public const string Unspecified = "unspecified";

    public static IReadOnlyList<string> Levels { get; } = [Unspecified, "none", "minimal", "low", "medium", "high"];
}

/// <summary>The per-profile Diff size limit: 0 is unlimited, n is n × 64 KB up to 5.</summary>
public static class AiDiffCap
{
    public const int MaxUnits = 5;
    public const int BytesPerUnit = 64 * 1024;

    /// <summary>Null means unlimited. Out-of-range values clamp to 0…<see cref="MaxUnits"/>.</summary>
    public static long? ToBytes(int units)
    {
        var clamped = Math.Clamp(units, 0, MaxUnits);
        return clamped == 0 ? null : (long)clamped * BytesPerUnit;
    }
}

/// <summary>
/// One AI integration used to write commit messages. <see cref="ApiKey"/> is stored in plain
/// text in <c>settings.json</c>; in environment mode it holds the variable's name instead.
/// </summary>
public sealed record AiServiceProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public AiServiceKind Kind { get; init; } = AiServiceKind.Custom;
    public string Server { get; init; } = "";
    public string Model { get; init; } = "";
    public bool AutoFetchModels { get; init; } = true;

    /// <summary>When on, <see cref="ApiKey"/> is the name of an environment variable.</summary>
    public bool ReadApiKeyFromEnv { get; init; }

    public string ApiKey { get; init; } = "";
    public string ReasoningEffort { get; init; } = AiReasoningEffort.Unspecified;
    public string AdditionalPrompt { get; init; } = "";

    /// <summary>One <c>Name: Value</c> per line.</summary>
    public string ExtraHeaders { get; init; } = "";

    /// <summary>0–5, see <see cref="AiDiffCap"/>.</summary>
    public int DiffCapUnits { get; init; }

    /// <summary>For information only. Display order is list order.</summary>
    public DateTime CreatedUtc { get; init; }
}
