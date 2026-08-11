using System.Text.Json;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OrukApiClient;
using OrukModels.Models;
using OrukTransformer.Mcp.Config;
using OrukTransformer.Mcp.Tools;

namespace OrukTransformer.Mcp.Tests;

public class OrukServiceEnumerationToolTests
{
    private static readonly Uri FeedUrl = new("https://example.org/services");
    private static readonly FeedDefinition Feed = new(FeedUrl, "Example");

    [Fact]
    public async Task Enumerate_FirstPage_ReturnsWindow_AndSignalsMore()
    {
        var tool = CreateTool(MakeServices(5));

        var json = await tool.EnumerateServices("example", offset: 0, pageSize: 2);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("count").GetInt32());
        Assert.True(root.GetProperty("has_more").GetBoolean());
        Assert.Equal(2, root.GetProperty("next_offset").GetInt32());
        Assert.Equal(["s0", "s1"], Ids(root));
    }

    [Fact]
    public async Task Enumerate_LastPartialPage_HasNoMore_AndNullNextOffset()
    {
        var tool = CreateTool(MakeServices(5));

        var json = await tool.EnumerateServices("example", offset: 4, pageSize: 2);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("count").GetInt32());
        Assert.False(root.GetProperty("has_more").GetBoolean());
        // next_offset is omitted (not null) when there is no further page.
        Assert.False(root.TryGetProperty("next_offset", out _));
        Assert.Equal(["s4"], Ids(root));
    }

    [Fact]
    public async Task Enumerate_PageSizeExactlyMatchesFeed_HasNoMore()
    {
        var tool = CreateTool(MakeServices(5));

        var json = await tool.EnumerateServices("example", offset: 0, pageSize: 5);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(5, root.GetProperty("count").GetInt32());
        Assert.False(root.GetProperty("has_more").GetBoolean());
    }

    [Fact]
    public async Task Enumerate_OffsetBeyondEnd_ReturnsEmpty_NoMore()
    {
        var tool = CreateTool(MakeServices(3));

        var json = await tool.EnumerateServices("example", offset: 10, pageSize: 5);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(0, root.GetProperty("count").GetInt32());
        Assert.False(root.GetProperty("has_more").GetBoolean());
    }

    [Fact]
    public async Task Enumerate_UnknownFeed_ReturnsError()
    {
        var serviceClient = Substitute.For<IOrukServiceClient>();
        var registry = Substitute.For<IFeedRegistry>();
        registry.Feeds.Returns([Feed]);
        registry.Resolve(Arg.Any<string>()).Returns((FeedDefinition?)null);
        var tool = new OrukServiceEnumerationTool(
            serviceClient, registry, Substitute.For<ILogger<OrukServiceEnumerationTool>>());

        var json = await tool.EnumerateServices("nope");
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Enumerate_PagingAcrossWholeFeed_VisitsEveryRecordExactlyOnce()
    {
        var tool = CreateTool(MakeServices(7));

        var collected = new List<string>();
        int offset = 0;
        bool more = true;
        while (more)
        {
            var json = await tool.EnumerateServices("example", offset, pageSize: 3);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            collected.AddRange(Ids(root));
            more = root.GetProperty("has_more").GetBoolean();
            if (more) offset = root.GetProperty("next_offset").GetInt32();
        }

        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"s{i}"), collected);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static OrukServiceEnumerationTool CreateTool(IReadOnlyList<OrukService> services)
    {
        var serviceClient = Substitute.For<IOrukServiceClient>();
        serviceClient
            .SearchAsync(Arg.Any<Uri>(), Arg.Any<OrukServiceQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync(services));

        var registry = Substitute.For<IFeedRegistry>();
        registry.Feeds.Returns([Feed]);
        registry.Resolve(Arg.Any<string>()).Returns(Feed);

        return new OrukServiceEnumerationTool(
            serviceClient, registry, Substitute.For<ILogger<OrukServiceEnumerationTool>>());
    }

    private static IReadOnlyList<OrukService> MakeServices(int n) =>
        Enumerable.Range(0, n).Select(i => new OrukService { Id = $"s{i}", Name = $"Service {i}" }).ToList();

    private static async IAsyncEnumerable<OrukService> ToAsync(IEnumerable<OrukService> items)
    {
        foreach (var item in items)
        {
            await Task.Yield();
            yield return item;
        }
    }

    private static string[] Ids(JsonElement root) =>
        root.GetProperty("services").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()!)
            .ToArray();
}
