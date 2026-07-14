using System.Text.Json;

namespace OrukTransformer.Cli.Feeds;

/// <summary>
/// Outcome of loading a <c>feeds.json</c> file: either a non-empty feed list or an error message.
/// </summary>
public sealed record FeedsLoadResult(IReadOnlyList<FeedEntry> Feeds, string? Error)
{
    /// <summary>True when the file was loaded and yielded at least one valid feed.</summary>
    public bool Success => Error is null;

    internal static FeedsLoadResult Failure(string error) => new([], error);
}

/// <summary>
/// Reads a project <c>feeds.json</c> file into <see cref="FeedEntry"/> records.
/// Accepts both bare string URLs and <c>{ "url", "name", "aliases" }</c> objects,
/// matching the format the MCP server consumes.
/// </summary>
public static class FeedsFileLoader
{
    /// <summary>
    /// Loads and validates the feeds file at <paramref name="path"/>.
    /// Returns a failure result (rather than throwing) for a missing, malformed,
    /// or empty file so the caller can report a clean error and exit.
    /// </summary>
    public static FeedsLoadResult Load(string path)
    {
        if (!File.Exists(path))
            return FeedsLoadResult.Failure($"Feeds file '{path}' was not found.");

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            return FeedsLoadResult.Failure($"Could not read feeds file '{path}': {ex.Message}");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return FeedsLoadResult.Failure(
                    $"Feeds file '{path}' must contain a JSON array; found {doc.RootElement.ValueKind}.");
            }

            var feeds = new List<FeedEntry>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                FeedEntry? entry = element.ValueKind switch
                {
                    JsonValueKind.String
                        when Uri.TryCreate(element.GetString(), UriKind.Absolute, out var stringUri)
                        => new FeedEntry(stringUri, stringUri.Host),

                    JsonValueKind.Object
                        when element.TryGetProperty("url", out var urlProp)
                             && Uri.TryCreate(urlProp.GetString(), UriKind.Absolute, out var objectUri)
                        => new FeedEntry(objectUri, ResolveName(element, objectUri)),

                    _ => null
                };

                if (entry is not null)
                    feeds.Add(entry);
            }

            if (feeds.Count == 0)
                return FeedsLoadResult.Failure($"Feeds file '{path}' contained no valid feed entries.");

            return new FeedsLoadResult(feeds, null);
        }
        catch (JsonException ex)
        {
            return FeedsLoadResult.Failure($"Could not parse feeds file '{path}': {ex.Message}");
        }
    }

    private static string ResolveName(JsonElement element, Uri url) =>
        element.TryGetProperty("name", out var nameProp)
        && !string.IsNullOrWhiteSpace(nameProp.GetString())
            ? nameProp.GetString()!
            : url.Host;
}
