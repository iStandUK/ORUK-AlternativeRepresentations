using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OrukApiClient.Internal;
using OrukModels.Json;
using OrukModels.Models;

namespace OrukApiClient;

/// <summary>
/// Queries an ORUK v3 /services endpoint with pagination, optional server-side
/// filtering, and client-side fallback filtering for criteria the endpoint may
/// not support natively.
/// </summary>
/// <remarks>
/// Supports both standard page-number pagination and RPDE (Realtime Paged Data
/// Exchange) feeds that return a <c>next_url</c> cursor link.  The mode is
/// detected automatically from the first response.
/// UK postcodes passed as proximity are geocoded via postcodes.io before being
/// forwarded to the ORUK endpoint, which requires lat,long coordinates.
/// </remarks>
public sealed class OrukServiceClient : IOrukServiceClient
{
    private const int MaxPageSize = 100;
    private const int MaxPages = 100;

    private readonly HttpClient _httpClient;
    private readonly ILogger<OrukServiceClient> _logger;
    private readonly IPostcodeGeocoder _geocoder;

    public OrukServiceClient(
        HttpClient httpClient,
        ILogger<OrukServiceClient> logger,
        IPostcodeGeocoder geocoder)
    {
        _httpClient = httpClient;
        _logger = logger;
        _geocoder = geocoder;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<OrukService> SearchAsync(
        Uri feedBaseUrl,
        OrukServiceQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var noLimit = query.MaxRecords < 1;
        var remaining = noLimit ? int.MaxValue : query.MaxRecords;
        var pageSize = MaxPageSize;

        // Geocode the proximity string to lat,long — ORUK endpoints require coordinates,
        // not postcodes or place names. Passing a raw postcode returns zero results.
        // geoPoint is retained for client-side proximity filtering (see IsWithinRadius).
        GeoPoint? geoPoint = null;
        string? resolvedProximity = null;
        if (!string.IsNullOrWhiteSpace(query.Proximity))
        {
            geoPoint = await _geocoder.LookupAsync(query.Proximity, cancellationToken);
            if (geoPoint is not null)
                resolvedProximity = geoPoint.ToString();
            else
                _logger.LogWarning(
                    "Could not geocode '{Location}' to coordinates. " +
                    "The proximity filter will be omitted — results will not be geographically filtered.",
                    query.Proximity);
        }

        // serverProximity is what we send to the ORUK API. Some feeds (e.g. LA directories)
        // return zero results when a proximity parameter is sent even though they have matching
        // services. On first-page zero results we retry without it and rely on client-side
        // filtering instead.
        string? serverProximity = resolvedProximity;
        bool retriedWithoutServerProximity = false;
        bool appendServicesPath = false;
        bool triedServicesSuffixFallback = feedBaseUrl.AbsolutePath.TrimEnd('/')
            .EndsWith("/services", StringComparison.OrdinalIgnoreCase);

        // RPDE mode: when a feed returns next_url we follow cursor links instead
        // of incrementing page numbers.
        Uri? rpdeNextUrl = null;
        int currentPage = 1;
        int pagesFetched = 0;
        bool firstPage = true;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (pagesFetched >= MaxPages)
            {
                _logger.LogWarning(
                    "Reached {MaxPages}-page safety cap at {BaseUrl}. Stopping pagination.",
                    MaxPages, OrukUrlBuilder.EnsureBase(feedBaseUrl));
                break;
            }

            Uri url;
            if (rpdeNextUrl is not null)
            {
                url = rpdeNextUrl;
            }
            else
            {
                url = OrukUrlBuilder.BuildServicesUrl(
                    feedBaseUrl, query, currentPage, pageSize, serverProximity, appendServicesPath);
            }

            OrukPage<OrukService>? page = null;
            try
            {
                var response = await _httpClient.GetAsync(url, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    LogHttpError(response, currentPage, url);
                    if (firstPage && !triedServicesSuffixFallback)
                    {
                        _logger.LogInformation(
                            "First-page request to {Url} failed. Retrying feed {BaseUrl} with '/services' suffix.",
                            url, feedBaseUrl);
                        appendServicesPath = true;
                        triedServicesSuffixFallback = true;
                        currentPage = 1;
                        pagesFetched = 0;
                        rpdeNextUrl = null;
                        continue;
                    }
                    if (firstPage) yield break;
                    if (rpdeNextUrl is not null) break;
                    currentPage++;
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                page = TryDeserialise(body, currentPage, url);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (firstPage && !triedServicesSuffixFallback)
                {
                    _logger.LogWarning(ex,
                        "First-page request to {Url} failed. Retrying feed {BaseUrl} with '/services' suffix.",
                        url, feedBaseUrl);
                    appendServicesPath = true;
                    triedServicesSuffixFallback = true;
                    currentPage = 1;
                    pagesFetched = 0;
                    rpdeNextUrl = null;
                    continue;
                }
                if (firstPage)
                {
                    _logger.LogError(ex, "Fatal error fetching first page from {Url}. Aborting.", url);
                    yield break;
                }
                _logger.LogWarning(ex, "Error fetching page {Page} from {Url}. Skipping.", currentPage, url);
                if (rpdeNextUrl is not null) break;
                currentPage++;
                continue;
            }

            pagesFetched++;

            if (page is null || page.Contents.Count == 0)
            {
                if (firstPage && !triedServicesSuffixFallback)
                {
                    _logger.LogInformation(
                        "Feed {BaseUrl} returned no results from {Url}. Retrying with '/services' suffix.",
                        feedBaseUrl, url);
                    appendServicesPath = true;
                    triedServicesSuffixFallback = true;
                    currentPage = 1;
                    pagesFetched = 0;
                    rpdeNextUrl = null;
                    continue;
                }

                // RPDE feeds may return an empty page with a next_url — stop only
                // when the next_url is also absent (end of feed).
                if (page?.NextUrl is not null)
                    goto advance;

                // First-page zero results with proximity sent: some ORUK feeds do not
                // support the proximity parameter and return empty instead of ignoring it.
                // Retry without server-side proximity and apply client-side filtering instead.
                if (firstPage && serverProximity is not null && !retriedWithoutServerProximity)
                {
                    _logger.LogInformation(
                        "Feed {BaseUrl} returned 0 results with server-side proximity — " +
                        "retrying without it. Client-side proximity filter will be applied.",
                        OrukUrlBuilder.EnsureBase(feedBaseUrl));
                    serverProximity = null;
                    retriedWithoutServerProximity = true;
                    pagesFetched = 0;
                    continue;
                }

                _logger.LogDebug(
                    "Page {Page} returned no contents after {Fetched} page(s). Stopping.",
                    currentPage, pagesFetched);
                yield break;
            }

            if (firstPage)
            {
                firstPage = false;

                if (page!.NextUrl is not null)
                {
                    _logger.LogInformation(
                        "Feed at {BaseUrl} uses RPDE pagination (next_url present).",
                        OrukUrlBuilder.EnsureBase(feedBaseUrl));
                }
                else
                {
                    // Log reported total_pages but do not trust it — some feeds always
                    // report total_pages: 1 regardless of actual dataset size.
                    _logger.LogInformation(
                        "Feed at {BaseUrl} reports {Total} total items, {Pages} page(s) " +
                        "(using full-page heuristic to detect end of data).",
                        OrukUrlBuilder.EnsureBase(feedBaseUrl), page.TotalItems, page.TotalPages);
                }
            }

            foreach (var service in page!.Contents)
            {
                if (remaining <= 0) yield break;

                if (!MatchesClientSideFilters(service, query))
                    continue;

                // Client-side proximity filter — applied whether or not server-side proximity
                // was sent. This handles feeds that ignore the proximity parameter entirely.
                if (geoPoint is not null && query.RadiusKm.HasValue &&
                    !IsWithinRadius(service, geoPoint, query.RadiusKm.Value))
                    continue;

                yield return service;
                remaining--;
            }

            advance:
            // Advance to next page
            if (page!.NextUrl is not null)
            {
                if (!Uri.TryCreate(page.NextUrl, UriKind.Absolute, out var nextUri))
                {
                    _logger.LogWarning(
                        "RPDE next_url '{NextUrl}' is not a valid absolute URI. Stopping.", page.NextUrl);
                    yield break;
                }
                rpdeNextUrl = nextUri;
            }
            else
            {
                rpdeNextUrl = null;

                // Use the "full page" heuristic to detect end-of-data.
                // Some feeds always report total_pages: 1 (e.g. Open Sessions) so we cannot
                // trust the reported value. A partial page (fewer items than pageSize) is the
                // reliable end-of-feed signal.
                if (page.Contents.Count < pageSize) break;
                currentPage++;
            }
        }

        _logger.LogDebug(
            "Completed pagination for {BaseUrl}: {PagesFetched} page(s) fetched.",
            OrukUrlBuilder.EnsureBase(feedBaseUrl), pagesFetched);
    }

    /// <inheritdoc/>
    public async Task<OrukService?> GetByIdAsync(
        Uri feedBaseUrl,
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        var url = OrukUrlBuilder.BuildServiceByIdUrl(feedBaseUrl, serviceId);

        try
        {
            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Service {Id} not found at {Url}.", serviceId, url);
                return null;
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // Single-service endpoints return the object directly, not wrapped in a page.
            return JsonSerializer.Deserialize<OrukService>(body, OrukJson.Default);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogError(ex, "Failed to retrieve service {Id} from {Url}.", serviceId, url);
            return null;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private OrukPage<OrukService>? TryDeserialise(string body, int page, Uri url)
    {
        try
        {
            return JsonSerializer.Deserialize<OrukPage<OrukService>>(body, OrukJson.Default);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Deserialisation failed for page {Page} from {Url}.", page, url);
            return null;
        }
    }

    private void LogHttpError(HttpResponseMessage response, int page, Uri url)
    {
        var code = (int)response.StatusCode;
        var reason = response.ReasonPhrase ?? "Unknown";
        _logger.LogError(
            "HTTP {Code} ({Reason}) fetching page {Page} from {Url}.",
            code, reason, page, url);
    }

    /// <summary>
    /// Applies filter criteria that may not be supported as server-side query
    /// parameters by all ORUK endpoint implementations.
    /// </summary>
    private static bool MatchesClientSideFilters(OrukService service, OrukServiceQuery query)
    {
        // Client-side keyword fallback — applied when no taxonomy terms were resolved.
        // Many ORUK endpoints do not implement the `text` query parameter; this ensures
        // the keyword still filters results even if the server returned unfiltered data.
        if (!string.IsNullOrWhiteSpace(query.Keyword) && query.TaxonomyTermIds.Count == 0)
        {
            var kw = query.Keyword;
            var inName = service.Name?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false;
            var inDesc = service.Description?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false;
            var inOrg  = service.Organization?.Name?.Contains(kw, StringComparison.OrdinalIgnoreCase) ?? false;
            if (!inName && !inDesc && !inOrg)
                return false;
        }

        // Age range
        // Guard: treat maximum_age == 0 as "no maximum" — some feeds (e.g. Open Sessions)
        // translate an absent upper bound as 0 rather than null, which would otherwise
        // cause all services to be excluded from minimum-age queries.
        if (query.MinimumAge.HasValue &&
            service.MaximumAge.HasValue &&
            service.MaximumAge.Value > 0 &&
            service.MaximumAge.Value < query.MinimumAge.Value)
            return false;

        if (query.MaximumAge.HasValue &&
            service.MinimumAge.HasValue &&
            service.MinimumAge.Value > query.MaximumAge.Value)
            return false;

        // Free only
        if (query.FreeOnly)
        {
            var hasCost = service.CostOptions.Any(c => c.Amount.HasValue && c.Amount.Value > 0m);
            if (hasCost) return false;
        }

        // Taxonomy term filter (client-side fallback)
        if (query.TaxonomyTermIds.Count > 0)
        {
            var serviceTermIds = service.Attributes
                .Where(a => a.TaxonomyTerm is not null)
                .Select(a => a.TaxonomyTerm!.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!query.TaxonomyTermIds.Any(id => serviceTermIds.Contains(id)))
                return false;
        }

        // Language filter
        if (query.Language is not null)
        {
            var lang = query.Language;
            var hasLanguage = service.Languages.Any(l =>
                (l.Name?.Contains(lang, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (l.Code?.Equals(lang, StringComparison.OrdinalIgnoreCase) ?? false));
            if (!hasLanguage) return false;
        }

        // Accessibility feature filter
        if (query.AccessibilityFeature is not null)
        {
            var feature = query.AccessibilityFeature;
            var hasFeature = service.ServiceAtLocations
                .Select(sal => sal.Location)
                .Where(l => l is not null)
                .SelectMany(l => l!.Accessibility)
                .Any(a => a.Description?.Contains(feature, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!hasFeature) return false;
        }

        // Delivery type filter
        // Primary: match OrukLocation.LocationType (e.g. "physical", "virtual", "postal")
        // Fallback: keyword match on description/name when LocationType is not populated,
        // which is common in many ORUK feed implementations.
        if (query.DeliveryType is not null)
        {
            var type = query.DeliveryType;

            var hasLocationType = service.ServiceAtLocations
                .Select(sal => sal.Location)
                .Any(l => l?.LocationType?.Equals(type, StringComparison.OrdinalIgnoreCase) ?? false);

            if (!hasLocationType)
                hasLocationType = MatchesDeliveryTypeFallback(service, type);

            if (!hasLocationType) return false;
        }

        // Updated since filter
        if (query.UpdatedSince.HasValue)
        {
            if (string.IsNullOrWhiteSpace(service.LastModified))
                return false;

            if (!DateTimeOffset.TryParse(service.LastModified, out var modified) ||
                modified < query.UpdatedSince.Value)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Fallback delivery-type check for when <c>location_type</c> is not populated.
    /// Uses description/name keywords and structural signals (no physical address + has URL).
    /// </summary>
    private static bool MatchesDeliveryTypeFallback(OrukService service, string deliveryType)
    {
        var isVirtual = deliveryType.Equals("virtual", StringComparison.OrdinalIgnoreCase)
                     || deliveryType.Equals("online", StringComparison.OrdinalIgnoreCase)
                     || deliveryType.Equals("remote", StringComparison.OrdinalIgnoreCase);

        if (!isVirtual) return false;

        // Signal 1: service description or name mentions online delivery
        var text = $"{service.Name} {service.Description}";
        string[] virtualKeywords = ["online", "virtual", "remote", "zoom", "teams", "webinar", "digital", "telephone", "phone"];
        if (virtualKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Signal 2: service has a URL but no service-at-location with a physical address
        if (service.Url is not null && service.ServiceAtLocations.Count == 0)
            return true;

        return false;
    }

    /// <summary>
    /// Returns true when the service has at least one geocoded location within
    /// <paramref name="radiusKm"/> of <paramref name="queryPoint"/>.
    /// Services with no geocoded locations are included (receive-liberally policy).
    /// </summary>
    private static bool IsWithinRadius(OrukService service, GeoPoint queryPoint, double radiusKm)
    {
        var geoLocations = service.ServiceAtLocations
            .Select(sal => sal.Location)
            .Where(l => l is not null && l.Latitude.HasValue && l.Longitude.HasValue)
            .ToList();

        // No geocoded locations — include rather than exclude (data may be incomplete).
        if (geoLocations.Count == 0) return true;

        return geoLocations.Any(loc =>
            HaversineKm(queryPoint.Latitude, queryPoint.Longitude,
                        loc!.Latitude!.Value, loc!.Longitude!.Value) <= radiusKm);
    }

    /// <summary>
    /// Haversine great-circle distance in kilometres between two WGS84 coordinates.
    /// </summary>
    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
