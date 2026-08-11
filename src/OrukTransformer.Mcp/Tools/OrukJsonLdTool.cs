using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using OrukApiClient;
using OrukModels.SchemaOrg;
using OrukTransformer.Core.Mapping;
using OrukTransformer.Mcp.Config;

namespace OrukTransformer.Mcp.Tools;

/// <summary>
/// MCP tool that transforms Open Referral UK services into Schema.org JSON-LD.
/// Reuses the shared Core transform (<see cref="IOrukToSchemaOrgTransformer"/>) and
/// merge (<see cref="IJsonLdMerger"/>) pipeline that also backs the CLI, so the output
/// is identical to <c>oruk-transformer --json-ld</c>.
/// </summary>
[McpServerToolType]
public sealed class OrukJsonLdTool(
    IOrukServiceClient serviceClient,
    IFeedRegistry feedRegistry,
    IOrukToSchemaOrgTransformer transformer,
    IJsonLdMerger merger,
    ILogger<OrukJsonLdTool> logger)
{
    [McpServerTool(Name = "get_service_jsonld")]
    [Description(
        "Transform Open Referral UK service-directory data into Schema.org JSON-LD — a " +
        "consolidated @graph of GovernmentService, Organization, and Place nodes suitable " +
        "for search-engine indexing or structured-data consumers. Provide a service_id " +
        "(from search_services) to convert a single service, or omit it to convert a sample " +
        "of the whole feed up to max_records.")]
    public async Task<string> GetServiceJsonLd(
        [Description("The base URL or configured feed name/alias (from list_feeds) to transform.")]
        string feedUrl,
        [Description(
            "Optional service ID (from search_services) to convert just that one service. " +
            "Omit to convert a sample of the whole feed.")]
        string? serviceId = null,
        [Description(
            "Maximum number of services to transform when converting a whole feed. " +
            "Default 50; a value less than 1 means no limit. Ignored when service_id is given.")]
        int maxRecords = 50,
        CancellationToken cancellationToken = default)
    {
        var feedUri = ResolveFeedUri(feedUrl);
        if (feedUri is null)
        {
            logger.LogWarning("GetServiceJsonLd: invalid feed_url value '{FeedUrl}'.", feedUrl);
            return """{"error":"Invalid feed_url value. Provide a configured feed name or absolute URL."}""";
        }

        var options = BuildTransformationOptions(feedUri);
        var results = new List<TransformationResult>();

        if (!string.IsNullOrWhiteSpace(serviceId))
        {
            logger.LogInformation(
                "GetServiceJsonLd: single service {ServiceId} from {FeedUrl}.", serviceId, feedUri);

            var service = await serviceClient.GetByIdAsync(feedUri, serviceId, cancellationToken);
            if (service is null)
            {
                logger.LogWarning(
                    "GetServiceJsonLd: service {ServiceId} not found in {FeedUrl}.", serviceId, feedUri);
                return JsonSerializer.Serialize(
                    new { error = $"Service '{serviceId}' was not found in feed '{feedUrl}'." });
            }

            results.Add(transformer.Transform(service, options));
        }
        else
        {
            logger.LogInformation(
                "GetServiceJsonLd: feed {FeedUrl}, maxRecords={Max}.",
                feedUri, maxRecords < 1 ? "unlimited" : maxRecords.ToString());

            var query = new OrukServiceQuery { MaxRecords = maxRecords };
            await foreach (var service in serviceClient.SearchAsync(feedUri, query, cancellationToken))
            {
                results.Add(transformer.Transform(service, options));
            }

            if (results.Count == 0)
            {
                logger.LogWarning("GetServiceJsonLd: no services retrieved from {FeedUrl}.", feedUri);
                return JsonSerializer.Serialize(
                    new { error = $"No services were retrieved from feed '{feedUrl}'." });
            }
        }

        var merged = merger.Merge(results);
        logger.LogInformation(
            "GetServiceJsonLd: transformed {Count} service(s) from {FeedUrl}.", results.Count, feedUri);

        return JsonSerializer.Serialize(merged, SchemaOrgSerializerOptions.Default);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static TransformationOptions BuildTransformationOptions(Uri feedUri) => new()
    {
        BaseUrl = $"{feedUri.Scheme}://{feedUri.Host}" +
                  (feedUri.IsDefaultPort ? string.Empty : $":{feedUri.Port}")
    };

    private Uri? ResolveFeedUri(string feedIdentifier)
    {
        var configured = feedRegistry.Resolve(feedIdentifier);
        if (configured is not null)
            return configured.Url;

        if (Uri.TryCreate(feedIdentifier, UriKind.Absolute, out var supplied))
            return supplied;

        return null;
    }
}
