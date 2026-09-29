namespace Historia.Services.Storage;

public sealed class NullableGitObjectIdJsonConverter : JsonConverter<GitObjectId?>
{
    public override bool HandleNull => true;

    public override GitObjectId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType != JsonTokenType.String)
            return null;

        var s = reader.GetString();
        if (s is not null && (s.Length == 40 || s.Length == 64))
        {
            if (GitObjectId.TryParse(s, out var id))
                return id;
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, GitObjectId? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Value.ToString());
    }
}

public sealed class GitObjectIdJsonConverter : JsonConverter<GitObjectId>
{
    public override GitObjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Expected string token for GitObjectId.");

        var s = reader.GetString();
        if (s is not null && (s.Length == 40 || s.Length == 64) && GitObjectId.TryParse(s, out var id))
        {
            return id;
        }

        throw new JsonException($"Malformed or invalid GitObjectId value: '{s}'.");
    }

    public override void Write(Utf8JsonWriter writer, GitObjectId value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
