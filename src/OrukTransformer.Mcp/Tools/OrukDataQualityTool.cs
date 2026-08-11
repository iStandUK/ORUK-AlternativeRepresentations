using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using OrukApiClient;
using OrukTransformer.Core.Mapping;
using OrukTransformer.Core.Reporting;
using OrukTransformer.Core.Vodim;
using OrukTransformer.Mcp.Config;

namespace OrukTransformer.Mcp.Tools;

/// <summary>
/// MCP tool that assesses the VODIM data quality of an Open Referral UK feed by running
/// the shared Core transform and rolling the per-field VODIM classifications up via
/// <see cref="VodimSummary"/>. Returns either a structured JSON breakdown (default) or
/// the same xHTML5 report the CLI writes with <c>--data-quality-report</c>.
/// </summary>
[McpServerToolType]
public sealed class OrukDataQualityTool(
    IOrukServiceClient serviceClient,
    IFeedRegistry feedRegistry,
    IOrukToSchemaOrgTransformer transformer,
    ILogger<OrukDataQualityTool> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [McpServerTool(Name = "get_data_quality_report")]
    [Description(
        "Assess the data quality of an Open Referral UK feed using the VODIM model " +
        "(Valid / Other / Default / Invalid / Missing / Unmapped), computed while " +
        "transforming its services to Schema.org. Returns a structured JSON breakdown " +
        "(overall totals plus a per-field summary) by default, or the full branded xHTML5 " +
        "report when format is 'html'.")]
    public async Task<string> GetDataQualityReport(
        [Description("The base URL or configured feed name/alias (from list_feeds) to assess.")]
        string feedUrl,
        [Description(
            "Maximum number of services to assess. Default 50; a value less than 1 means no limit.")]
        int maxRecords = 50,
        [Description("Output format: 'json' for a structured breakdown (default), or 'html' for the xHTML5 report.")]
        [AllowedValues("json", "html")]
        string format = "json",
        CancellationToken cancellationToken = default)
    {
        var feedUri = ResolveFeedUri(feedUrl);
        if (feedUri is null)
        {
            logger.LogWarning("GetDataQualityReport: invalid feed_url value '{FeedUrl}'.", feedUrl);
            return """{"error":"Invalid feed_url value. Provide a configured feed name or absolute URL."}""";
        }

        logger.LogInformation(
            "GetDataQualityReport: feed {FeedUrl}, maxRecords={Max}, format={Format}.",
            feedUri, maxRecords < 1 ? "unlimited" : maxRecords.ToString(), format);

        var options = new TransformationOptions
        {
            BaseUrl = $"{feedUri.Scheme}://{feedUri.Host}" +
                      (feedUri.IsDefaultPort ? string.Empty : $":{feedUri.Port}")
        };

        var reports = new List<TransformationReport>();
        var query = new OrukServiceQuery { MaxRecords = maxRecords };
        await foreach (var service in serviceClient.SearchAsync(feedUri, query, cancellationToken))
        {
            reports.Add(transformer.Transform(service, options).Report);
        }

        logger.LogInformation(
            "GetDataQualityReport: assessed {Count} service(s) from {FeedUrl}.", reports.Count, feedUri);

        if (string.Equals(format, "html", StringComparison.OrdinalIgnoreCase))
        {
            return HtmlDataQualityReportBuilder.Build(reports, feedUri);
        }

        var summary = VodimSummary.Aggregate(reports);
        var result = new
        {
            feed_url = feedUri.ToString(),
            feed_name = feedRegistry.GetDisplayName(feedUri),
            services_assessed = summary.ServiceCount,
            overall = CountsObject(summary.Overall),
            overall_percent = PercentObject(summary.Overall),
            fields = summary.Fields.Select(f => new
            {
                source_path = f.SourcePath,
                target_path = f.TargetPath,
                counts = CountsObject(f.Counts),
                notes = f.Notes.Count == 0 ? null : f.Notes.Select(n => new
                {
                    text = n.Text,
                    count = n.Count,
                    classification = n.Classification.ToString().ToLowerInvariant()
                })
            })
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static object CountsObject(VodimCounts c) => new
    {
        valid = c.Valid,
        other = c.Other,
        @default = c.Default,
        invalid = c.Invalid,
        missing = c.Missing,
        unmapped = c.Unmapped,
        total = c.Total
    };

    private static object PercentObject(VodimCounts c) => new
    {
        valid = c.PercentOf(VodimClassification.Valid),
        other = c.PercentOf(VodimClassification.Other),
        @default = c.PercentOf(VodimClassification.Default),
        invalid = c.PercentOf(VodimClassification.Invalid),
        missing = c.PercentOf(VodimClassification.Missing),
        unmapped = c.PercentOf(VodimClassification.Unmapped)
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
