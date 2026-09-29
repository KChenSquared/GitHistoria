namespace Historia.Services.Storage;

/// <summary>Shared JSON serialization settings for all of Historia's persistent files.</summary>
public static class HistoriaJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            new NullableGitObjectIdJsonConverter(),
            new GitObjectIdJsonConverter()
        },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
