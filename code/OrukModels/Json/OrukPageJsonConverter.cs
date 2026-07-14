using System.Text.Json;
using System.Text.Json.Serialization;
using OrukModels.Models;

namespace OrukModels.Json;

/// <summary>
/// Creates <see cref="OrukPageJsonConverter{T}"/> instances for any closed
/// <see cref="OrukPage{T}"/> type.
/// </summary>
public sealed class OrukPageJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType &&
        typeToConvert.GetGenericTypeDefinition() == typeof(OrukPage<>);

    public override JsonConverter CreateConverter(
        Type typeToConvert, JsonSerializerOptions options)
    {
        var itemType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)Activator.CreateInstance(
            typeof(OrukPageJsonConverter<>).MakeGenericType(itemType))!;
    }
}

/// <summary>
/// Envelope-tolerant reader for ORUK paged responses. Different ORUK publishers wrap
/// their result pages in different envelopes:
/// <list type="bullet">
///   <item><description>ORUK / OpenPlace Directory: <c>total_items</c>, <c>total_pages</c>, <c>page_number</c>, <c>contents</c></description></item>
///   <item><description>Spring Data (CQC, Buckinghamshire): <c>totalElements</c>, <c>totalPages</c>, <c>number</c>, <c>content</c></description></item>
///   <item><description>Lowercase variants (Hull): <c>totalelements</c>, <c>totalpages</c>, <c>content</c></description></item>
/// </list>
/// Property names are matched case-insensitively and ignoring underscores, so all three
/// envelopes populate the same <see cref="OrukPage{T}"/> instead of silently
/// deserializing to an empty page (which the paginating clients read as "no more
/// results" and stop). Item elements are deserialized with the supplied options, so a
/// registered <see cref="TolerantStringConverter"/> still applies to their identifiers.
/// </summary>
internal sealed class OrukPageJsonConverter<T> : JsonConverter<OrukPage<T>>
{
    public override OrukPage<T> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected StartObject when reading an OrukPage.");

        int totalItems = 0, totalPages = 0, pageNumber = 0, size = 0;
        bool firstPage = false, lastPage = false, empty = false;
        string? nextUrlSnake = null, nextUrlNext = null;
        IReadOnlyList<T> contents = [];

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var name = Normalize(reader.GetString());
            reader.Read(); // advance to the value token

            switch (name)
            {
                case "totalitems":
                case "totalelements":
                    totalItems = ReadInt(ref reader);
                    break;
                case "totalpages":
                    totalPages = ReadInt(ref reader);
                    break;
                case "pagenumber":
                case "number":
                    pageNumber = ReadInt(ref reader);
                    break;
                case "size":
                case "perpage":
                    size = ReadInt(ref reader);
                    break;
                case "firstpage":
                case "first":
                    firstPage = ReadBool(ref reader);
                    break;
                case "lastpage":
                case "last":
                    lastPage = ReadBool(ref reader);
                    break;
                case "empty":
                    empty = ReadBool(ref reader);
                    break;
                case "contents":
                case "content":
                    contents = JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? [];
                    break;
                case "nexturl":
                    nextUrlSnake = ReadStringOrNull(ref reader);
                    break;
                case "next":
                    nextUrlNext = ReadStringOrNull(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new OrukPage<T>
        {
            TotalItems = totalItems,
            TotalPages = totalPages,
            PageNumber = pageNumber,
            Size = size,
            FirstPage = firstPage,
            LastPage = lastPage,
            Empty = empty,
            Contents = contents,
            NextUrlSnakeCase = nextUrlSnake,
            NextUrlNext = nextUrlNext,
        };
    }

    public override void Write(
        Utf8JsonWriter writer, OrukPage<T> value, JsonSerializerOptions options)
    {
        // Round-trips to the canonical ORUK snake_case envelope.
        writer.WriteStartObject();
        writer.WriteNumber("total_items", value.TotalItems);
        writer.WriteNumber("total_pages", value.TotalPages);
        writer.WriteNumber("page_number", value.PageNumber);
        writer.WriteNumber("size", value.Size);
        writer.WriteBoolean("first_page", value.FirstPage);
        writer.WriteBoolean("last_page", value.LastPage);
        writer.WriteBoolean("empty", value.Empty);
        if (value.NextUrlSnakeCase is not null)
            writer.WriteString("next_url", value.NextUrlSnakeCase);
        if (value.NextUrlNext is not null)
            writer.WriteString("next", value.NextUrlNext);
        writer.WritePropertyName("contents");
        JsonSerializer.Serialize(writer, value.Contents, options);
        writer.WriteEndObject();
    }

    private static string Normalize(string? name) =>
        name is null ? string.Empty : name.Replace("_", string.Empty).ToLowerInvariant();

    private static int ReadInt(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value)
            ? value
            : 0;

    private static bool ReadBool(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.True => true,
        JsonTokenType.False => false,
        JsonTokenType.String => bool.TryParse(reader.GetString(), out var b) && b,
        _ => false
    };

    private static string? ReadStringOrNull(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString();
        reader.Skip(); // consume nested structure if the feed sends an object/array
        return null;
    }
}
