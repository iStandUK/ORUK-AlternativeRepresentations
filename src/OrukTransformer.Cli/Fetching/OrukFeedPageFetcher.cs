using Microsoft.Extensions.Logging;
using OrukModels.Json;
using OrukModels.Models;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace OrukTransformer.Cli.Fetching;

/// <summary>
/// Fetches ORUK service records by iterating the paged <c>GET /services</c>
/// endpoint, using query parameters <c>page</c> and <c>per_page</c> to
/// minimise the number of HTTP round-trips while respecting endpoint load.
/// </summary>
/// <remarks>
/// Page size is capped at 100 records per request, or at
/// <paramref name="maxRecords"/> when a smaller positive limit is requested.
/// If an individual page fails to deserialise, a warning is logged and that
/// page is skipped; the fetch continues with the next page ("receive liberally").
/// If the <em>first</em> page returns a non-success HTTP status code the fetch
/// is aborted immediately.
/// </remarks>
public sealed class OrukFeedPageFetcher : IOrukFeedPageFetcher
{
    private const int MaxPageSize = 100;

    private readonly HttpClient _httpClient;
    private readonly ILogger<OrukFeedPageFetcher> _logger;

    public OrukFeedPageFetcher(HttpClient httpClient, ILogger<OrukFeedPageFetcher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<OrukService> FetchAsync(
        Uri endpointUrl,
        int maxRecords,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var noLimit = maxRecords < 1;
        var remaining = noLimit ? int.MaxValue : maxRecords;
        var pageSize = noLimit ? MaxPageSize : Math.Min(maxRecords, MaxPageSize);

        int totalPages = 1;
        int currentPage = 1;
        bool firstPage = true;

        while (currentPage <= totalPages && remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requestSize = Math.Min(pageSize, remaining);
            var url = BuildPageUrl(endpointUrl, currentPage, requestSize);

            OrukPage<OrukService>? page = null;
            try
            {
                var response = await _httpClient.GetAsync(url, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var statusCode = (int)response.StatusCode;
                    var reasonPhrase = response.ReasonPhrase ?? "Unknown";

                    // Log all HTTP errors with appropriate severity
                    if (statusCode == 429)
                    {
                        _logger.LogError(
                            "API rate limiting detected: HTTP {StatusCode} ({ReasonPhrase}) for page {Page} at {Url}. " +
                            "The endpoint may be throttling requests.",
                            statusCode, reasonPhrase, currentPage, url);
                    }
                    else if (statusCode >= 400 && statusCode < 500)
                    {
                        _logger.LogError(
                            "Client error: HTTP {StatusCode} ({ReasonPhrase}) for page {Page} at {Url}. " +
                            "This may indicate an authentication, authorization, or request format issue.",
                            statusCode, reasonPhrase, currentPage, url);
                    }
                    else if (statusCode >= 500)
                    {
                        _logger.LogError(
                            "Server error: HTTP {StatusCode} ({ReasonPhrase}) for page {Page} at {Url}. " +
                            "The endpoint may be experiencing issues.",
                            statusCode, reasonPhrase, currentPage, url);
                    }
                    else
                    {
                        _logger.LogError(
                            "HTTP error: {StatusCode} ({ReasonPhrase}) for page {Page} at {Url}.",
                            statusCode, reasonPhrase, currentPage, url);
                    }

                    if (firstPage)
                    {
                        _logger.LogError("Fatal: First page request failed. Aborting fetch.");
                        yield break;
                    }

                    _logger.LogWarning("Skipping page {Page} and continuing with next page.", currentPage);
                    currentPage++;
                    continue;
                }

                // Buffer the response to allow re-reading if the first
                // deserialization attempt fails (avoids allocating a string on
                // the happy path).
                await response.Content.LoadIntoBufferAsync(cancellationToken);

                try
                {
                    page = await response.Content.ReadFromJsonAsync<OrukPage<OrukService>>(
                        OrukJson.Default, cancellationToken: cancellationToken);
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex,
                        "JSON deserialization failed for page {Page} from {Url}. Attempting fallback.",
                        currentPage, url);

                    // Fallback: read the buffered content as a string and retry.
                    string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    try
                    {
                        page = JsonSerializer.Deserialize<OrukPage<OrukService>>(responseBody, OrukJson.Default);

                        if (page is not null)
                        {
                            _logger.LogInformation(
                                "Fallback deserialization succeeded for page {Page} from {Url}.", 
                                currentPage, url);
                        }
                    }
                    catch (Exception fallbackEx)
                    {
                        _logger.LogError(fallbackEx, 
                            "Fallback deserialization also failed for page {Page} from {Url}. Response body length: {BodyLength}", 
                            currentPage, url, responseBody.Length);
                        throw;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                                                    or System.Text.Json.JsonException)
            {
                bool isTimeout = ex is TaskCanceledException tce
                    && tce.CancellationToken != cancellationToken;

                if (firstPage)
                {
                    if (isTimeout)
                        _logger.LogError(
                            "Request timed out fetching first page from {Url}. " +
                            "Consider increasing the timeout with --timeout.",
                            url);
                    else
                        _logger.LogError(ex,
                            "Fatal error fetching first page from {Url}. Aborting.", url);
                    yield break;
                }

                if (isTimeout)
                    _logger.LogWarning(
                        "Request timed out fetching page {Page} from {Url}. " +
                        "Consider increasing the timeout with --timeout.",
                        currentPage, url);
                else
                    _logger.LogWarning(ex,
                        "Error fetching or deserialising page {Page} from {Url}. Skipping page.",
                        currentPage, url);

                currentPage++;
                continue;
            }

            if (page is null || page.Contents.Count == 0)
            {
                _logger.LogWarning(
                    "Page {Page} returned no contents (page is null: {IsNull}, page size requested: {RequestSize}). " +
                    "Stopping pagination. Total services yielded so far: {TotalYielded}.",
                    currentPage, page is null, requestSize, 
                    noLimit ? int.MaxValue - remaining : maxRecords - remaining);
                yield break;
            }

            // Update total pages from the first response
            if (firstPage)
            {
                totalPages = Math.Max(page.TotalPages, 1);
                firstPage = false;
                _logger.LogInformation(
                    "ORUK endpoint reports {TotalItems} total items across {TotalPages} pages (page size: {PageSize}).",
                    page.TotalItems, page.TotalPages, pageSize);
            }

            _logger.LogInformation(
                "Successfully fetched page {CurrentPage}/{TotalPages} with {ItemCount} services from {Url}.",
                currentPage, totalPages, page.Contents.Count, url);

            foreach (var service in page.Contents)
            {
                if (remaining <= 0) yield break;
                yield return service;
                remaining--;
            }

            currentPage++;
        }

        // Log completion summary
        var totalFetched = noLimit ? int.MaxValue - remaining : maxRecords - remaining;
        _logger.LogInformation(
            "Pagination completed. Fetched {TotalFetched} services across {PagesFetched} pages from {Url}.",
            totalFetched, currentPage - 1, endpointUrl);
    }

    private static Uri BuildPageUrl(Uri baseUrl, int page, int perPage)
    {
        var builder = new UriBuilder(baseUrl);
        var existing = System.Web.HttpUtility.ParseQueryString(builder.Query);
        existing["page"] = page.ToString();
        existing["per_page"] = perPage.ToString();
        builder.Query = existing.ToString();
        return builder.Uri;
    }
}
