using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrukModels.Json;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for deserializing ORUK feed responses.
///
/// ORUK publishers vary in how they shape their JSON, so a single tolerant options
/// instance is used everywhere feeds are read:
/// <list type="bullet">
///   <item><description><see cref="JsonSerializerOptions.PropertyNameCaseInsensitive"/> — feeds differ in casing.</description></item>
///   <item><description><see cref="TolerantStringConverter"/> — accepts integer identifiers (e.g. Buckinghamshire) into string properties.</description></item>
///   <item><description><see cref="JsonNumberHandling.AllowReadingFromString"/> — accepts numeric fields quoted as strings.</description></item>
/// </list>
/// Envelope tolerance for paged responses is applied via the
/// <see cref="OrukPageJsonConverterFactory"/> attribute on <c>OrukPage&lt;T&gt;</c>, so it
/// works regardless of which options instance is used.
/// </summary>
public static class OrukJson
{
    /// <summary>Tolerant options for reading ORUK feed JSON. Safe to share (immutable after first use).</summary>
    public static JsonSerializerOptions Default { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        options.Converters.Add(new TolerantStringConverter());
        return options;
    }
}
