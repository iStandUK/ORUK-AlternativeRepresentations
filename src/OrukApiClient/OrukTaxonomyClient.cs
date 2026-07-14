using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OrukApiClient.Internal;
using OrukModels.Json;
using OrukModels.Models;

namespace OrukApiClient;

/// <summary>
/// Retrieves taxonomy terms from an ORUK v3 /taxonomy_terms endpoint and
/// provides in-memory label resolution.
/// </summary>
public sealed class OrukTaxonomyClient : IOrukTaxonomyClient
{
    private const int PageSize = 200;

    private readonly HttpClient _httpClient;
    private readonly ILogger<OrukTaxonomyClient> _logger;

    public OrukTaxonomyClient(HttpClient httpClient, ILogger<OrukTaxonomyClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OrukTaxonomyTerm>> GetAllTermsAsync(
        Uri feedBaseUrl,
        CancellationToken cancellationToken = default)
    {
        var allTerms = new List<OrukTaxonomyTerm>();
        int currentPage = 1;
        int totalPages = 1;

        while (currentPage <= totalPages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = OrukUrlBuilder.BuildTaxonomyTermsUrl(feedBaseUrl, currentPage, PageSize);

            try
            {
                var response = await _httpClient.GetAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning(
                        "HTTP {Code} fetching taxonomy_terms page {Page} from {Url}.",
                        (int)response.StatusCode, currentPage, url);
                    break;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var page = TryDeserialise(body);

                if (page is null || page.Contents.Count == 0)
                    break;

                if (currentPage == 1)
                {
                    totalPages = Math.Max(page.TotalPages, 1);
                    _logger.LogInformation(
                        "Fetching {Total} taxonomy terms across {Pages} pages from {Base}.",
                        page.TotalItems, page.TotalPages,
                        OrukUrlBuilder.EnsureBase(feedBaseUrl));
                }

                allTerms.AddRange(page.Contents);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                _logger.LogWarning(ex, "Error fetching taxonomy_terms page {Page}. Stopping.", currentPage);
                break;
            }

            currentPage++;
        }

        _logger.LogInformation("Loaded {Count} taxonomy terms.", allTerms.Count);
        return allTerms.AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> ResolveByLabel(string label, IReadOnlyList<OrukTaxonomyTerm> terms)
    {
        if (string.IsNullOrWhiteSpace(label) || terms.Count == 0)
            return [];

        var normalised = label.Trim().ToLowerInvariant();

        // 1. Exact match on name
        var exact = terms
            .Where(t => string.Equals(t.Name, normalised, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Id)
            .ToList();
        if (exact.Count > 0) return exact.AsReadOnly();

        // 2. Partial match — term name contains the label or vice versa
        var partial = terms
            .Where(t => t.Name is not null &&
                        (t.Name.Contains(normalised, StringComparison.OrdinalIgnoreCase) ||
                         normalised.Contains(t.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.Id)
            .ToList();
        if (partial.Count > 0) return partial.AsReadOnly();

        // 3. Description match
        var descMatch = terms
            .Where(t => t.Description is not null &&
                        t.Description.Contains(normalised, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Id)
            .ToList();
        if (descMatch.Count > 0) return descMatch.AsReadOnly();

        // 4. No match — caller falls back to keyword search
        _logger.LogDebug("No taxonomy match found for label '{Label}'.", label);
        return [];
    }

    private OrukPage<OrukTaxonomyTerm>? TryDeserialise(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<OrukPage<OrukTaxonomyTerm>>(body, OrukJson.Default);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
