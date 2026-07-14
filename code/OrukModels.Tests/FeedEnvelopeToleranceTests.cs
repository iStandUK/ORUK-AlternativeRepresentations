using System.Text.Json;
using System.Text.RegularExpressions;
using OrukModels.Json;
using OrukModels.Models;

namespace OrukModels.Tests;

/// <summary>
/// Regression tests for the feed-envelope and identifier tolerance added to
/// <see cref="OrukPage{T}"/> and <see cref="TolerantStringConverter"/>.
///
/// ORUK publishers wrap paged responses in different envelopes and use different
/// identifier types. Before these converters the paginating clients silently returned
/// zero results for any feed that did not use the exact ORUK snake_case envelope with
/// string IDs (e.g. CQC, Buckinghamshire, Hull), even though those feeds pass ORUK's
/// own daily compliance validation.
///
/// Fixtures are frozen captures of the live feeds (per_page=2), stored under fixtures/.
/// These tests make no live HTTP calls.
/// </summary>
public class FeedEnvelopeToleranceTests
{
    private static string LoadFixture(string fileName) =>
        File.ReadAllText(Path.Combine("fixtures", fileName));

    private static OrukPage<OrukService>? DeserializePage(string fileName) =>
        JsonSerializer.Deserialize<OrukPage<OrukService>>(LoadFixture(fileName), OrukJson.Default);

    // ── CQC: Spring-style camelCase envelope (totalElements / totalPages / number / content) ──

    [Fact]
    public void Cqc_SpringEnvelope_PopulatesContents()
    {
        var page = DeserializePage("cqc-services-page1.json");
        Assert.NotNull(page);
        Assert.Equal(2, page.Contents.Count);
    }

    [Fact]
    public void Cqc_SpringEnvelope_MapsTotalElementsToTotalItems()
    {
        var page = DeserializePage("cqc-services-page1.json");
        Assert.NotNull(page);
        Assert.True(page.TotalItems > 0, "totalElements should map to TotalItems");
        Assert.True(page.TotalPages > 0, "totalPages should map to TotalPages");
    }

    [Fact]
    public void Cqc_SpringEnvelope_FirstItem_HasIdAndName()
    {
        var page = DeserializePage("cqc-services-page1.json");
        Assert.NotNull(page);
        var first = page.Contents[0];
        Assert.False(string.IsNullOrEmpty(first.Id));
        Assert.False(string.IsNullOrEmpty(first.Name));
    }

    // ── Buckinghamshire: camelCase envelope AND integer identifiers ──────────────────

    [Fact]
    public void Bucks_CamelCaseEnvelope_PopulatesContents()
    {
        var page = DeserializePage("bucks-services-page1.json");
        Assert.NotNull(page);
        Assert.Equal(2, page.Contents.Count);
        Assert.True(page.TotalItems > 0);
    }

    [Fact]
    public void Bucks_IntegerId_IsCoercedToNumericString()
    {
        var page = DeserializePage("bucks-services-page1.json");
        Assert.NotNull(page);
        var first = page.Contents[0];
        // The feed emits `"id": <integer>`; it must survive as its numeric string form
        // rather than throwing during deserialization.
        Assert.False(string.IsNullOrEmpty(first.Id));
        Assert.Matches(new Regex("^[0-9]+$"), first.Id);
    }

    // ── Hull: all-lowercase envelope, currently empty — must parse, not throw ────────

    [Fact]
    public void Hull_LowercaseEmptyEnvelope_ParsesToEmptyPage()
    {
        var page = DeserializePage("hull-services-page1.json");
        Assert.NotNull(page);
        Assert.Empty(page.Contents);
    }

    // ── Bristol: canonical ORUK snake_case envelope must still work through the converter ──

    [Fact]
    public void Bristol_SnakeCaseEnvelope_StillPopulatesContents()
    {
        var page = DeserializePage("bristol-services-page1.json");
        Assert.NotNull(page);
        Assert.Equal(3, page.Contents.Count);
        Assert.Equal(1, page.PageNumber);
        Assert.True(page.FirstPage);
        Assert.True(page.TotalItems > 0);
    }

    // ── TolerantStringConverter unit coverage ───────────────────────────────────────

    [Theory]
    [InlineData("{\"id\":2830}", "2830")]           // integer (Buckinghamshire)
    [InlineData("{\"id\":\"abc-123\"}", "abc-123")] // string passes through unchanged
    public void TolerantStringConverter_CoercesScalarIds(string json, string expected)
    {
        var service = JsonSerializer.Deserialize<OrukService>(json, OrukJson.Default);
        Assert.NotNull(service);
        Assert.Equal(expected, service.Id);
    }

    // ── Write path: round-trips to canonical snake_case ─────────────────────────────

    [Fact]
    public void OrukPage_Write_EmitsCanonicalSnakeCaseEnvelope()
    {
        var page = new OrukPage<OrukService>
        {
            TotalItems = 5,
            TotalPages = 2,
            PageNumber = 1,
            Contents = [new OrukService { Id = "x1", Name = "Test" }],
        };

        var json = JsonSerializer.Serialize(page, OrukJson.Default);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(5, doc.RootElement.GetProperty("total_items").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("total_pages").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("contents").GetArrayLength());
    }

    [Fact]
    public void OrukPage_RoundTrip_PreservesContents()
    {
        var original = new OrukPage<OrukService>
        {
            TotalItems = 1,
            Contents = [new OrukService { Id = "abc", Name = "Round Trip" }],
        };

        var json = JsonSerializer.Serialize(original, OrukJson.Default);
        var restored = JsonSerializer.Deserialize<OrukPage<OrukService>>(json, OrukJson.Default);

        Assert.NotNull(restored);
        Assert.Single(restored.Contents);
        Assert.Equal("abc", restored.Contents[0].Id);
        Assert.Equal("Round Trip", restored.Contents[0].Name);
    }
}
