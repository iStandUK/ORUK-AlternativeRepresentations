using System.Text;

namespace OrukTransformer.Cli.Feeds;

/// <summary>
/// Derives filesystem-safe output file names from a feed's display name.
/// </summary>
public static class OutputFileNaming
{
    /// <summary>Extension for the transformed Schema.org JSON-LD document.</summary>
    public const string JsonLdExtension = ".jsonld";

    /// <summary>Extension (suffix) for the xHTML5 data-quality report.</summary>
    public const string DataQualityExtension = ".data-quality.html";

    /// <summary>
    /// Converts a feed display name to a lower-kebab-case slug safe for use as a
    /// file name (letters/digits preserved, runs of anything else collapsed to a
    /// single hyphen, leading/trailing hyphens trimmed). Falls back to <c>"feed"</c>
    /// when nothing usable remains.
    /// </summary>
    public static string Slugify(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "feed";

        var builder = new StringBuilder(name.Length);
        var lastWasHyphen = true; // start true so leading separators are skipped

        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                builder.Append('-');
                lastWasHyphen = true;
            }
        }

        var slug = builder.ToString().Trim('-');
        return slug.Length == 0 ? "feed" : slug;
    }

    /// <summary>JSON-LD output file name for a slug, e.g. <c>bristol.jsonld</c>.</summary>
    public static string JsonLdFileName(string slug) => slug + JsonLdExtension;

    /// <summary>Data-quality report file name for a slug, e.g. <c>bristol.data-quality.html</c>.</summary>
    public static string DataQualityFileName(string slug) => slug + DataQualityExtension;
}
