using OrukTransformer.Cli.Feeds;

namespace OrukTransformer.Cli.Tests;

public class FeedsFileLoaderTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"feeds-{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_ObjectEntries_ParsesUrlAndName()
    {
        File.WriteAllText(_path, """
        [
          { "url": "https://bristol.example.org/v3", "name": "Bristol", "aliases": ["bristol"] },
          { "url": "https://cqc.example.org/api", "name": "Care Quality Commission" }
        ]
        """);

        var result = FeedsFileLoader.Load(_path);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Equal(2, result.Feeds.Count);
        Assert.Equal("Bristol", result.Feeds[0].Name);
        Assert.Equal(new Uri("https://bristol.example.org/v3"), result.Feeds[0].Url);
        Assert.Equal("Care Quality Commission", result.Feeds[1].Name);
    }

    [Fact]
    public void Load_StringEntry_UsesHostAsName()
    {
        File.WriteAllText(_path, """[ "https://southampton.example.org/api" ]""");

        var result = FeedsFileLoader.Load(_path);

        Assert.True(result.Success);
        var feed = Assert.Single(result.Feeds);
        Assert.Equal("southampton.example.org", feed.Name);
    }

    [Fact]
    public void Load_ObjectEntryWithoutName_FallsBackToHost()
    {
        File.WriteAllText(_path, """[ { "url": "https://dorset.example.org/aggregator" } ]""");

        var result = FeedsFileLoader.Load(_path);

        var feed = Assert.Single(result.Feeds);
        Assert.Equal("dorset.example.org", feed.Name);
    }

    [Fact]
    public void Load_SkipsInvalidEntriesButKeepsValidOnes()
    {
        File.WriteAllText(_path, """
        [
          { "url": "https://valid.example.org/v3", "name": "Valid" },
          { "name": "No URL" },
          "not-an-absolute-uri",
          42
        ]
        """);

        var result = FeedsFileLoader.Load(_path);

        Assert.True(result.Success);
        var feed = Assert.Single(result.Feeds);
        Assert.Equal("Valid", feed.Name);
    }

    [Fact]
    public void Load_MissingFile_ReturnsFailure()
    {
        var result = FeedsFileLoader.Load(_path); // never created

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(result.Feeds);
    }

    [Fact]
    public void Load_NotAnArray_ReturnsFailure()
    {
        File.WriteAllText(_path, """{ "url": "https://x.example.org" }""");

        var result = FeedsFileLoader.Load(_path);

        Assert.False(result.Success);
        Assert.Contains("must contain a JSON array", result.Error);
    }

    [Fact]
    public void Load_EmptyOrAllInvalid_ReturnsFailure()
    {
        File.WriteAllText(_path, "[]");

        var result = FeedsFileLoader.Load(_path);

        Assert.False(result.Success);
        Assert.Contains("no valid feed entries", result.Error);
    }

    [Fact]
    public void Load_MalformedJson_ReturnsFailure()
    {
        File.WriteAllText(_path, "[ { not json ");

        var result = FeedsFileLoader.Load(_path);

        Assert.False(result.Success);
        Assert.Contains("Could not parse", result.Error);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
