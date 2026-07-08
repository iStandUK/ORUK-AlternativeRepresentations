using System.Net;
using System.Text.Json;
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

    private static OrukServiceClient CreateClient(HttpClient httpClient)
    {
        var geocoder = Substitute.For<IPostcodeGeocoder>();
        return new OrukServiceClient(
            httpClient,
            NullLogger<OrukServiceClient>.Instance,
            geocoder);
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
