using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OrukApiClient.Internal;
using OrukModels.Json;
using OrukModels.Models;

namespace OrukApiClient;

/// <summary>
/// Queries an ORUK v3 /organizations endpoint with pagination and tolerant error handling.
/// </summary>
public sealed class OrukOrganizationClient : IOrukOrganizationClient
{
    private const int MaxPageSize = 100;

    private readonly HttpClient _httpClient;
    private readonly ILogger<OrukOrganizationClient> _logger;

    public OrukOrganizationClient(HttpClient httpClient, ILogger<OrukOrganizationClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<OrukOrganization> SearchAsync(
        Uri feedBaseUrl,
        string? keyword = null,
        int maxRecords = 20,
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
            var url = OrukUrlBuilder.BuildOrganizationsUrl(feedBaseUrl, keyword, currentPage, requestSize);

            OrukPage<OrukOrganization>? page = null;
            try
            {
                var response = await _httpClient.GetAsync(url, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError(
                        "HTTP {Code} ({Reason}) fetching organizations page {Page} from {Url}.",
                        (int)response.StatusCode, response.ReasonPhrase ?? "Unknown", currentPage, url);
                    if (firstPage) yield break;
                    currentPage++;
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                page = TryDeserialise(body, currentPage, url);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (firstPage)
                {
                    _logger.LogError(ex, "Fatal error fetching first organizations page from {Url}. Aborting.", url);
                    yield break;
                }
                _logger.LogWarning(ex, "Error fetching organizations page {Page} from {Url}. Skipping.", currentPage, url);
                currentPage++;
                continue;
            }

            if (page is null || page.Contents.Count == 0)
            {
                _logger.LogDebug("Organizations page {Page} returned no contents. Stopping pagination.", currentPage);
                yield break;
            }

            if (firstPage)
            {
                totalPages = Math.Max(page.TotalPages, 1);
                firstPage = false;
                _logger.LogInformation(
                    "Feed reports {Total} organisations across {Pages} page(s) at {BaseUrl}.",
                    page.TotalItems, page.TotalPages, OrukUrlBuilder.EnsureBase(feedBaseUrl));
            }

            foreach (var org in page.Contents)
            {
                if (remaining <= 0) yield break;
                yield return org;
                remaining--;
            }

            currentPage++;
        }
    }

    /// <inheritdoc/>
    public async Task<OrukOrganization?> GetByIdAsync(
        Uri feedBaseUrl,
        string organizationId,
        CancellationToken cancellationToken = default)
    {
        var url = OrukUrlBuilder.BuildOrganizationByIdUrl(feedBaseUrl, organizationId);

        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Organisation {Id} not found at {Url}.", organizationId, url);
                return null;
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return JsonSerializer.Deserialize<OrukOrganization>(body, OrukJson.Default);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogError(ex, "Failed to retrieve organisation {Id} from {Url}.", organizationId, url);
            return null;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private OrukPage<OrukOrganization>? TryDeserialise(string body, int page, Uri url)
    {
        try
        {
            return JsonSerializer.Deserialize<OrukPage<OrukOrganization>>(body, OrukJson.Default);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex,
                "Deserialisation failed for organizations page {Page} from {Url}.", page, url);
            return null;
        }
    }
}
