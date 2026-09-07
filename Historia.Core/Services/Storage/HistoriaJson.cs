using System.Text.Json;
using System.Text.Json.Serialization;

namespace Historia.Services.Storage;

/// <summary>Shared JSON serialization settings for all of Historia's persistent files (§9.3).</summary>
public static class HistoriaJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
