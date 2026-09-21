namespace Historia.Services.Storage;

/// <summary>
/// JSON converter for <see cref="ObjectId"/> writing the 40-char SHA hex and reading it back.
/// Malformed or non-40-character strings deserialize as null without throwing.
/// </summary>
public sealed class ObjectIdJsonConverter : JsonConverter<ObjectId>
{
    public override bool HandleNull => true;

    public override ObjectId? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType != JsonTokenType.String)
            return null;

        var s = reader.GetString();
        if (s is { Length: 40 })
        {
            try { return new ObjectId(s); }
            catch { return null; }
        }

        return null;
    }

    public override void Write(Utf8JsonWriter writer, ObjectId? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value.Sha);
    }
}
