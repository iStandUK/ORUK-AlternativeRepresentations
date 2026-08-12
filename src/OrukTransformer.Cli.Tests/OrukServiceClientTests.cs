using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OrukApiClient;
using OrukModels.Models;
using RichardSzalay.MockHttp;

namespace OrukTransformer.Cli.Tests;

public class OrukServiceClientTests
{
    [Fact]
    public async Task SearchAsync_WhenRootEndpointReturnsResults_DoesNotRequireServicesSuffix()
    {
        var feedBaseUrl = new Uri("https://example.org/o/OpenReferralService/v3");
        var mock = new MockHttpMessageHandler();
        mock.Fallback.Respond(HttpStatusCode.NotFound);

        var rootRequest = mock
            .When(HttpMethod.Get, "https://example.org/o/OpenReferralService/v3")
            .WithQueryString("page", "1")
            .WithQueryString("per_page", "100")
            .Respond("application/json", MakePage(1, 1, [new OrukService { Id = "svc-1", Name = "Service One" }]));

        var client = CreateClient(mock.ToHttpClient());
        var query = new OrukServiceQuery { MaxRecords = 20 };

        var results = new List<OrukService>();
        await foreach (var service in client.SearchAsync(feedBaseUrl, query))
            results.Add(service);

        Assert.Single(results);
        Assert.Equal("svc-1", results[0].Id);
        Assert.Equal(1, mock.GetMatchCount(rootRequest));
    }

    [Fact]
    public async Task SearchAsync_WhenRootEndpointReturnsNoResults_RetriesWithServicesSuffix()
    {
        var feedBaseUrl = new Uri("https://example.org/o/OpenReferralService/v3");
        var mock = new MockHttpMessageHandler();
        mock.Fallback.Respond(HttpStatusCode.NotFound);

        var rootRequest = mock
            .When(HttpMethod.Get, "https://example.org/o/OpenReferralService/v3")
            .WithQueryString("page", "1")
            .WithQueryString("per_page", "100")
            .Respond("application/json", MakePage(1, 1, []));

        var servicesRequest = mock
            .When(HttpMethod.Get, "https://example.org/o/OpenReferralService/v3/services")
            .WithQueryString("page", "1")
            .WithQueryString("per_page", "100")
            .Respond("application/json", MakePage(1, 1, [new OrukService { Id = "svc-2", Name = "Service Two" }]));

        var client = CreateClient(mock.ToHttpClient());
        var query = new OrukServiceQuery { MaxRecords = 20 };

        var results = new List<OrukService>();
        await foreach (var service in client.SearchAsync(feedBaseUrl, query))
            results.Add(service);

        Assert.Single(results);
        Assert.Equal("svc-2", results[0].Id);
        Assert.Equal(1, mock.GetMatchCount(rootRequest));
        Assert.Equal(1, mock.GetMatchCount(servicesRequest));
    }

    [Fact]
    public async Task SearchAsync_WhenRootReturnsHtml_RecoversViaServicesSuffix()
    {
        // A feed whose base URL redirects to an HTML page (e.g. its website) must not
        // throw; the client should fall back to the /services endpoint and yield results.
        var feedBaseUrl = new Uri("https://example.org/o/OpenReferralService/v3");
        var mock = new MockHttpMessageHandler();
        mock.Fallback.Respond(HttpStatusCode.NotFound);

        var rootRequest = mock
            .When(HttpMethod.Get, "https://example.org/o/OpenReferralService/v3")
            .WithQueryString("page", "1")
            .WithQueryString("per_page", "100")
            .Respond("text/html", "<!DOCTYPE html><html><body>Not an API</body></html>");

        var servicesRequest = mock
            .When(HttpMethod.Get, "https://example.org/o/OpenReferralService/v3/services")
            .WithQueryString("page", "1")
            .WithQueryString("per_page", "100")
            .Respond("application/json", MakePage(1, 1, [new OrukService { Id = "svc-3", Name = "Service Three" }]));

        var client = CreateClient(mock.ToHttpClient());
        var query = new OrukServiceQuery { MaxRecords = 20 };

        var results = new List<OrukService>();
        await foreach (var service in client.SearchAsync(feedBaseUrl, query))
            results.Add(service);

        Assert.Single(results);
        Assert.Equal("svc-3", results[0].Id);
        Assert.Equal(1, mock.GetMatchCount(rootRequest));
        Assert.Equal(1, mock.GetMatchCount(servicesRequest));
    }

    [Fact]
    public async Task SearchAsync_CleanCompletion_LogsInformationalComplete_NoWarning()
    {
        var feedBaseUrl = new Uri("https://example.org/services");
        var mock = new MockHttpMessageHandler();
        mock.Fallback.Respond(HttpStatusCode.NotFound);
        // A partial first page (2 < per_page) is the end-of-data signal — a clean completion.
        mock.When(HttpMethod.Get, "https://example.org/services")
            .Respond("application/json", MakePage(1, 1,
                [new OrukService { Id = "svc-1", Name = "One" }, new OrukService { Id = "svc-2", Name = "Two" }]));

        var log = new CapturingLogger<OrukServiceClient>();
        var client = CreateClient(mock.ToHttpClient(), log);

        var results = new List<OrukService>();
        await foreach (var s in client.SearchAsync(feedBaseUrl, new OrukServiceQuery { MaxRecords = 0 }))
            results.Add(s);

        Assert.Equal(2, results.Count);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("complete"));
    }

    [Fact]
    public async Task SearchAsync_ParseFailureMidStream_YieldsGoodPage_AndWarnsOfTruncation()
    {
        var feedBaseUrl = new Uri("https://example.org/services");
        var mock = new MockHttpMessageHandler();
        mock.Fallback.Respond(HttpStatusCode.NotFound);

        // A full first page (== per_page) makes the harvester request a second page.
        var fullPage = Enumerable.Range(1, 100)
            .Select(i => new OrukService { Id = $"svc-{i}", Name = $"S{i}" }).ToList();
        mock.When(HttpMethod.Get, "https://example.org/services").WithQueryString("page", "1")
            .Respond("application/json", MakePage(1, 2, fullPage));
        // The second page is unparseable — a mid-stream parse failure, not end-of-data.
        mock.When(HttpMethod.Get, "https://example.org/services").WithQueryString("page", "2")
            .Respond("application/json", "{ this is not valid json ]");

        var log = new CapturingLogger<OrukServiceClient>();
        var client = CreateClient(mock.ToHttpClient(), log);

        var results = new List<OrukService>();
        await foreach (var s in client.SearchAsync(feedBaseUrl, new OrukServiceQuery { MaxRecords = 0 }))
            results.Add(s);

        // The good page survived; the truncation is surfaced as a warning, not a clean completion.
        Assert.Equal(100, results.Count);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Data may be incomplete"));
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("complete"));
    }

    [Fact]
    public async Task SearchAsync_ServerCapsPageSizeBelowRequest_StillPagesToTheEnd()
    {
        // The client requests per_page=100, but this server caps its page size at 2 (like
        // Dorset, which returns 50 for a requested 100). The end-of-data heuristic must key off
        // the server's observed page size, not the requested one — otherwise the first short
        // page is mistaken for the last and the feed is silently truncated.
        var feedBaseUrl = new Uri("https://example.org/aggregator/services");
        var mock = new MockHttpMessageHandler();
        mock.Fallback.Respond(HttpStatusCode.NotFound);

        mock.When(HttpMethod.Get, feedBaseUrl.ToString()).WithQueryString("page", "1")
            .Respond("application/json", MakePage(1, 3,
                [new OrukService { Id = "s0" }, new OrukService { Id = "s1" }]));
        mock.When(HttpMethod.Get, feedBaseUrl.ToString()).WithQueryString("page", "2")
            .Respond("application/json", MakePage(2, 3,
                [new OrukService { Id = "s2" }, new OrukService { Id = "s3" }]));
        // A short final page (fewer than the server's own page size) is the real end signal.
        mock.When(HttpMethod.Get, feedBaseUrl.ToString()).WithQueryString("page", "3")
            .Respond("application/json", MakePage(3, 3, [new OrukService { Id = "s4" }]));

        var client = CreateClient(mock.ToHttpClient());

        var results = new List<OrukService>();
        await foreach (var s in client.SearchAsync(feedBaseUrl, new OrukServiceQuery { MaxRecords = 0 }))
            results.Add(s);

        Assert.Equal(["s0", "s1", "s2", "s3", "s4"], results.Select(r => r.Id));
    }

    private static OrukServiceClient CreateClient(HttpClient httpClient, ILogger<OrukServiceClient>? logger = null)
    {
        var geocoder = Substitute.For<IPostcodeGeocoder>();
        return new OrukServiceClient(
            httpClient,
            logger ?? NullLogger<OrukServiceClient>.Instance,
            geocoder);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static string MakePage(
        int pageNumber,
        int totalPages,
        IReadOnlyList<OrukService> services)
    {
        var page = new
        {
            total_items = services.Count,
            total_pages = totalPages,
            page_number = pageNumber,
            size = services.Count,
            first_page = pageNumber == 1,
            last_page = pageNumber == totalPages,
            empty = services.Count == 0,
            contents = services
        };

        return JsonSerializer.Serialize(page);
    }
}
