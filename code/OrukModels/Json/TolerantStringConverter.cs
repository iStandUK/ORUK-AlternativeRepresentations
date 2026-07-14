using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrukModels.Json;

/// <summary>
/// Reads a JSON string, number, or boolean into a CLR <see cref="string"/>.
///
/// ORUK feeds are inconsistent about identifier types. Some publishers — notably
/// Buckinghamshire's Family Information Service API — emit integer <c>id</c> values
/// (e.g. <c>"id": 2830</c>), which the default System.Text.Json binder rejects for a
/// <see cref="string"/> property, causing the whole record (and therefore the whole
/// page) to fail deserialization and silently return no results. This converter
/// coerces scalar JSON values to their textual form so those records load instead of
/// throwing. Identifiers are already treated as opaque strings throughout the models,
/// so nothing downstream assumes a UUID shape.
/// </summary>
public sealed class TolerantStringConverter : JsonConverter<string>
{
    public override string? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ReadScalarAsString(ref reader);

    public override void Write(
        Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }

    // Dictionary keys (e.g. [JsonExtensionData]) must keep normal string semantics —
    // without these overrides a globally-registered string converter throws when used
    // for a property name.
    public override string ReadAsPropertyName(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString()!;

    public override void WriteAsPropertyName(
        Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WritePropertyName(value);

    internal static string? ReadScalarAsString(ref Utf8JsonReader reader) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Number => ReadNumberAsString(ref reader),
            _ => throw new JsonException(
                $"Cannot convert JSON token '{reader.TokenType}' to a string.")
        };

    private static string ReadNumberAsString(ref Utf8JsonReader reader)
    {
        if (reader.TryGetInt64(out var l))
            return l.ToString(CultureInfo.InvariantCulture);
        if (reader.TryGetDecimal(out var d))
            return d.ToString(CultureInfo.InvariantCulture);
        return reader.GetDouble().ToString(CultureInfo.InvariantCulture);
    }
}
