using System.Text.Json.Serialization;
using OrukModels.Json;

namespace OrukModels.Models;

/// <summary>
/// Represents a paginated ORUK v3 API response.
/// </summary>
/// <typeparam name="T">The type of items in the <see cref="Contents"/> collection.</typeparam>
/// <remarks>
/// Deserialized via <see cref="OrukPageJsonConverterFactory"/>, which tolerates the
/// differing pagination envelopes used across ORUK publishers (snake_case, Spring
/// camelCase, and all-lowercase) so every feed populates the same shape.
/// </remarks>
[JsonConverter(typeof(OrukPageJsonConverterFactory))]
public record OrukPage<T>
{
    [JsonPropertyName("total_items")]
    public int TotalItems { get; init; }

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; init; }

    [JsonPropertyName("page_number")]
    public int PageNumber { get; init; }

    [JsonPropertyName("size")]
    public int Size { get; init; }

    [JsonPropertyName("first_page")]
    public bool FirstPage { get; init; }

    [JsonPropertyName("last_page")]
    public bool LastPage { get; init; }

    [JsonPropertyName("empty")]
    public bool Empty { get; init; }

    [JsonPropertyName("contents")]
    public IReadOnlyList<T> Contents { get; init; } = [];

    /// <summary>
    /// Number of items present in the source page that could not be deserialized and were
    /// skipped ("receive liberally"). Zero for a clean page. This is a decode-time signal,
    /// not part of the ORUK wire envelope, so callers can warn — and correctly detect
    /// end-of-data by raw item count — rather than silently dropping records or stopping
    /// a page short. See <see cref="OrukPageJsonConverter{T}"/>.
    /// </summary>
    [JsonIgnore]
    public int MalformedItemCount { get; init; }

    // ── RPDE (Realtime Paged Data Exchange) support ──────────────────────────────

    /// <summary>
    /// URL of the next page in RPDE feeds (snake_case variant used by Open Sessions
    /// and other RPDE-compliant ORUK publishers). When set, callers should follow
    /// this URL rather than incrementing the page number.
    /// </summary>
    [JsonPropertyName("next_url")]
    public string? NextUrlSnakeCase { get; init; }

    /// <summary>RPDE <c>next</c> link — used when the feed publishes the cursor as "next".</summary>
    [JsonPropertyName("next")]
    public string? NextUrlNext { get; init; }

    /// <summary>
    /// Resolved next-page URL, preferring <c>next_url</c> over <c>next</c>.
    /// Returns <see langword="null"/> when neither RPDE field is present (standard pagination).
    /// </summary>
    [JsonIgnore]
    public string? NextUrl => NextUrlSnakeCase ?? NextUrlNext;
}
