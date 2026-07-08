namespace OrukTransformer.Cli.Feeds;

/// <summary>
/// A single ORUK feed loaded from a <c>feeds.json</c> file.
/// </summary>
public sealed record FeedEntry(Uri Url, string Name)
{
    /// <summary>Human-readable label, falling back to the URL host when unnamed.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Url.Host : Name;
}
