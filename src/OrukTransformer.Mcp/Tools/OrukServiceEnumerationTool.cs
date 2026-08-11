using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using OrukApiClient;
using OrukModels.Models;
using OrukTransformer.Core;
using OrukTransformer.Mcp.Config;

namespace OrukTransformer.Mcp.Tools;

/// <summary>
/// MCP tool for exhaustively enumerating a single ORUK feed, page by page.
///
/// Unlike the search tools (which cap results at <see cref="McpOptions.MaxResultsPerQuery"/> and
/// apply keyword/location/date filters), this tool returns every service in a feed — including
/// those with no last-modified date — so agents can audit, count, or export a feed's full
/// contents. It is deliberately scoped to one feed and paginated via <c>offset</c>/<c>pageSize</c>
/// to keep individual responses bounded.
/// </summary>
[McpServerToolType]
public sealed class OrukServiceEnumerationTool(
    IOrukServiceClient serviceClient,
    IFeedRegistry feedRegistry,
    ILogger<OrukServiceEnumerationTool> logger)
{
    private const int DefaultPageSize = 100;
    private const int MaxPageSize = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [McpServerTool]
    [Description(
        "Enumerate every service in a single Open Referral UK feed, page by page, with no keyword, " +
        "location, or date filtering — including services that have no last-modified date. Use this " +
        "for exhaustive or bulk workflows such as auditing, counting, or exporting a feed's full " +
        "contents, where the capped search tools (search_services, get_services_updated_since) would " +
        "miss records. Enumerates ONE feed at a time; call repeatedly, passing the returned " +
        "next_offset, until has_more is false. Very large feeds are bounded by a safety cap of " +
        "roughly 10,000 records.")]
    public async Task<string> EnumerateServices(
        [Description(
            "The feed to enumerate: a feed URL, name, or alias from list_feeds. Required — enumerating " +
            "every feed at once is not supported because the combined volume is unbounded.")]
        string feedUrl,
        [Description(
            "Zero-based index of the first service to return. Start at 0, then pass the next_offset " +
            "from the previous response to page forward. Default 0.")]
        [Range(0, int.MaxValue)]
        int offset = 0,
        [Description("Number of services to return in this page (1–500). Default 100.")]
        [Range(1, MaxPageSize)]
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation(
            "EnumerateServices: feedUrl='{FeedUrl}', offset={Offset}, pageSize={PageSize}.",
            feedUrl, offset, pageSize);

        if (feedRegistry.Feeds.Count == 0)
        {
            logger.LogError("EnumerateServices: no ORUK feeds configured.");
            return JsonSerializer.Serialize(new { error = "No ORUK feeds are configured." }, JsonOptions);
        }

        if (string.IsNullOrWhiteSpace(feedUrl))
            return JsonSerializer.Serialize(new
            {
                error = "feedUrl is required. Pass a feed URL, name, or alias from list_feeds."
            }, JsonOptions);

        var feed = feedRegistry.Resolve(feedUrl);
        if (feed is null)
        {
            logger.LogWarning("EnumerateServices: feed '{FeedUrl}' not found.", feedUrl);
            return JsonSerializer.Serialize(new
            {
                error = $"Feed '{feedUrl}' was not found. Use list_feeds to see the available feeds."
            }, JsonOptions);
        }

        // Clamp defensively in case the validation attributes are bypassed by a caller.
        offset = Math.Max(0, offset);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        // SearchAsync streams the feed from the start, so fetch through the requested window plus one
        // extra record to learn whether more remain. Enumeration re-reads from the feed's first page
        // on each call — acceptable for bulk/audit use; deeper offsets simply cost more fetching.
        var query = new OrukServiceQuery { MaxRecords = offset + pageSize + 1 };

        var window = new List<OrukService>(pageSize);
        var index = 0;
        var hasMore = false;
        try
        {
            await foreach (var service in serviceClient.SearchAsync(feed.Url, query, cancellationToken))
            {
                if (index >= offset + pageSize)
                {
                    hasMore = true; // at least one record exists beyond the requested window
                    break;
                }
                if (index >= offset)
                    window.Add(service);
                index++;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "EnumerateServices: fetch failed for feed {FeedUrl}.", feed.Url);
            return JsonSerializer.Serialize(new
            {
                error = $"Could not enumerate feed '{feed.DisplayName}': {ex.Message}"
            }, JsonOptions);
        }

        var summaries = window.Select(s => MapToSummary(s, feed)).ToList();

        logger.LogInformation(
            "EnumerateServices: feed {FeedUrl} returned {Count} record(s) at offset {Offset} (hasMore={HasMore}).",
            feed.Url, summaries.Count, offset, hasMore);

        return JsonSerializer.Serialize(new
        {
            feed_url = feed.Url.ToString(),
            feed_name = feed.DisplayName,
            offset,
            page_size = pageSize,
            count = summaries.Count,
            has_more = hasMore,
            next_offset = hasMore ? offset + summaries.Count : (int?)null,
            services = summaries
        }, JsonOptions);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static object MapToSummary(OrukService s, FeedDefinition feed)
    {
        var location = s.ServiceAtLocations
            .Select(sal => sal.Location)
            .FirstOrDefault(l => l is not null);

        var physicalAddr = location?.PhysicalAddresses.FirstOrDefault();

        return new
        {
            id = s.Id,
            feed_url = feed.Url.ToString(),
            feed_name = feed.DisplayName,
            name = s.Name,
            description = OrukPlainText.ToPlainTextAndTruncate(s.Description, 200),
            status = s.Status,
            last_modified = s.LastModified,
            url = s.Url,
            organization = s.Organization?.Name,
            location = location is null ? null : new
            {
                name = location.Name,
                address = FormatAddress(physicalAddr),
                postcode = physicalAddr?.PostalCode
            }
        };
    }

    private static string? FormatAddress(OrukAddress? addr)
    {
        if (addr is null) return null;
        var parts = new[] { addr.Address1, addr.City, addr.StateProvince, addr.PostalCode }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join(", ", parts);
    }
}
