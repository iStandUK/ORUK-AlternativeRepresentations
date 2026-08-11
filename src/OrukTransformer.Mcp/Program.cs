using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrukApiClient;
using OrukTransformer.Core.Mapping;
using OrukTransformer.Mcp;
using OrukTransformer.Mcp.Config;
using OrukTransformer.Mcp.Taxonomy;
using OrukTransformer.Mcp.Tools;

var builder = Host.CreateApplicationBuilder(args);

// ── Logging ──────────────────────────────────────────────────────────────────
// MCP stdio transport owns stdout for JSON-RPC. ALL log output MUST go to stderr.
builder.Logging.AddConsole(options =>
    options.LogToStandardErrorThreshold = LogLevel.Trace);

// ── Configuration ─────────────────────────────────────────────────────────────
builder.Services.Configure<McpOptions>(builder.Configuration.GetSection("Mcp"));

// Load feed URLs from feeds.json — check alongside the executable, then the CWD.
var feedsJsonPaths = new[]
{
    Path.Combine(AppContext.BaseDirectory, "feeds.json"),
    Path.Combine(Directory.GetCurrentDirectory(), "feeds.json")
};

var feedDefinitions = feedsJsonPaths
    .Select(FeedsLoader.LoadFeeds)
    .FirstOrDefault(feeds => feeds.Count > 0) ?? [];

// Register the feed registry so tools can resolve friendly names and URLs consistently.
builder.Services.AddSingleton<IFeedRegistry>(_ => new FeedRegistry(feedDefinitions));

// ── HTTP clients (typed) ───────────────────────────────────────────────────────
// OrukServiceClient and OrukTaxonomyClient take HttpClient in their constructor.
builder.Services
    .AddHttpClient<IOrukServiceClient, OrukServiceClient>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OrukTransformer.Mcp/1.0");
    });

builder.Services
    .AddHttpClient<IOrukTaxonomyClient, OrukTaxonomyClient>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OrukTransformer.Mcp/1.0");
    });

builder.Services
    .AddHttpClient<IOrukOrganizationClient, OrukOrganizationClient>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OrukTransformer.Mcp/1.0");
    });

// Geocoder: converts UK postcodes to lat,long before passing to the ORUK proximity
// parameter.  Uses the free postcodes.io API (no key required).
builder.Services
    .AddHttpClient<IPostcodeGeocoder, PostcodesIoGeocoder>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OrukTransformer.Mcp/1.0");
        client.BaseAddress = new Uri("https://api.postcodes.io/");
    });

// ── Application services ───────────────────────────────────────────────────────
builder.Services.AddSingleton<ITaxonomyCache, TaxonomyCache>();

// Shared Core transform + merge pipeline (also used by the CLI). Both are stateless.
builder.Services.AddSingleton<IOrukToSchemaOrgTransformer, OrukToSchemaOrgTransformer>();
builder.Services.AddSingleton<IJsonLdMerger, JsonLdMerger>();

// ── MCP server ────────────────────────────────────────────────────────────────
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<OrukServiceSearchTool>()
    .WithTools<OrukServiceDetailTool>()
    .WithTools<OrukTaxonomyTool>()
    .WithTools<OrukFeedInfoTool>()
    .WithTools<OrukScheduleTool>()
    .WithTools<OrukRequiredDocumentsTool>()
    .WithTools<OrukServiceFilterTool>()
    .WithTools<OrukRecentlyUpdatedTool>()
    .WithTools<OrukServiceEnumerationTool>()
    .WithTools<OrukOrganizationTool>()
    .WithTools<OrukJsonLdTool>()
    .WithTools<OrukDataQualityTool>();

var host = builder.Build();

var logger = host.Services.GetRequiredService<ILogger<Program>>();
var opts = host.Services.GetRequiredService<IOptions<McpOptions>>().Value;

logger.LogInformation(
    "ORUK MCP Server starting. {FeedCount} feed(s) configured. MaxResults={Max}, TaxonomyTtl={Ttl}min.",
    feedDefinitions.Count, opts.MaxResultsPerQuery, opts.TaxonomyCacheTtlMinutes);

if (feedDefinitions.Count == 0)
{
    logger.LogWarning(
        "No feed URLs loaded. Add ORUK endpoint URLs to feeds.json in the working directory.");
}

await host.RunAsync();
