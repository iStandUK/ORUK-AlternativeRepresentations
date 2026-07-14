using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OrukTransformer.Cli.Feeds;

namespace OrukTransformer.Cli.Tests;

public class FeedBatchRunnerTests : IDisposable
{
    private readonly DirectoryInfo _outputDir = new(
        Path.Combine(Path.GetTempPath(), $"batch-{Guid.NewGuid():N}"));

    private static FeedEntry Feed(string name, string url) => new(new Uri(url), name);

    private static IRunCommand StubRunner(int defaultExitCode = 0)
    {
        var runner = Substitute.For<IRunCommand>();
        runner.ExecuteAsync(Arg.Any<Uri>(), Arg.Any<FileInfo?>(), Arg.Any<int>(),
            Arg.Any<bool>(), Arg.Any<FileInfo?>(), Arg.Any<CancellationToken>())
            .Returns(defaultExitCode);
        return runner;
    }

    [Fact]
    public async Task ExecuteAsync_WritesPerFeedFilesAndReturnsZero_WhenAllSucceed()
    {
        var runner = StubRunner();
        var feeds = new List<FeedEntry>
        {
            Feed("Bristol", "https://bristol.example.org/v3"),
            Feed("Care Quality Commission", "https://cqc.example.org/api")
        };

        var sut = new FeedBatchRunner(runner, NullLogger<FeedBatchRunner>.Instance);
        var exit = await sut.ExecuteAsync(feeds, _outputDir, maxRecords: 25, verbose: false);

        Assert.Equal(0, exit);
        Assert.True(Directory.Exists(_outputDir.FullName));

        // The feed base URL is passed straight through; the service client resolves /services.
        await runner.Received(1).ExecuteAsync(
            feeds[0].Url,
            Arg.Is<FileInfo?>(f => f!.Name == "bristol.jsonld"),
            25, false,
            Arg.Is<FileInfo?>(f => f!.Name == "bristol.data-quality.html"),
            Arg.Any<CancellationToken>());

        await runner.Received(1).ExecuteAsync(
            feeds[1].Url,
            Arg.Is<FileInfo?>(f => f!.Name == "care-quality-commission.jsonld"),
            25, false,
            Arg.Is<FileInfo?>(f => f!.Name == "care-quality-commission.data-quality.html"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WritesUnderOutputDirectory()
    {
        var runner = StubRunner();
        var feeds = new List<FeedEntry> { Feed("Bristol", "https://bristol.example.org/v3") };
        var sut = new FeedBatchRunner(runner, NullLogger<FeedBatchRunner>.Instance);

        await sut.ExecuteAsync(feeds, _outputDir, maxRecords: 1, verbose: false);

        await runner.Received(1).ExecuteAsync(
            Arg.Any<Uri>(),
            Arg.Is<FileInfo?>(f => f!.DirectoryName == _outputDir.FullName),
            Arg.Any<int>(), Arg.Any<bool>(),
            Arg.Is<FileInfo?>(f => f!.DirectoryName == _outputDir.FullName),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsOne_WhenAnyFeedFailsButRunsAll()
    {
        var runner = StubRunner();
        var down = Feed("Down", "https://down.example.org/v3");
        runner.ExecuteAsync(down.Url, Arg.Any<FileInfo?>(), Arg.Any<int>(),
            Arg.Any<bool>(), Arg.Any<FileInfo?>(), Arg.Any<CancellationToken>()).Returns(1);

        var feeds = new List<FeedEntry>
        {
            Feed("Ok", "https://ok.example.org/v3"),
            down,
            Feed("AlsoOk", "https://also.example.org/v3")
        };

        var sut = new FeedBatchRunner(runner, NullLogger<FeedBatchRunner>.Instance);
        var exit = await sut.ExecuteAsync(feeds, _outputDir, maxRecords: 10, verbose: false);

        Assert.Equal(1, exit);
        await runner.Received(3).ExecuteAsync(
            Arg.Any<Uri>(), Arg.Any<FileInfo?>(), Arg.Any<int>(),
            Arg.Any<bool>(), Arg.Any<FileInfo?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesAndReturnsOne_WhenAFeedThrows()
    {
        var runner = StubRunner();
        var boom = Feed("Boom", "https://boom.example.org/v3");
        runner.ExecuteAsync(boom.Url, Arg.Any<FileInfo?>(), Arg.Any<int>(),
            Arg.Any<bool>(), Arg.Any<FileInfo?>(), Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new HttpRequestException("network down"));

        var last = Feed("Last", "https://last.example.org/v3");
        var feeds = new List<FeedEntry> { boom, last };

        var sut = new FeedBatchRunner(runner, NullLogger<FeedBatchRunner>.Instance);
        var exit = await sut.ExecuteAsync(feeds, _outputDir, maxRecords: 10, verbose: false);

        Assert.Equal(1, exit);
        await runner.Received(1).ExecuteAsync(
            last.Url, Arg.Any<FileInfo?>(), Arg.Any<int>(),
            Arg.Any<bool>(), Arg.Any<FileInfo?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_DisambiguatesCollidingSlugs()
    {
        var runner = StubRunner();
        var feeds = new List<FeedEntry>
        {
            Feed("Hull", "https://hull-a.example.org/v3"),
            Feed("Hull", "https://hull-b.example.org/v3")
        };

        var sut = new FeedBatchRunner(runner, NullLogger<FeedBatchRunner>.Instance);
        await sut.ExecuteAsync(feeds, _outputDir, maxRecords: 10, verbose: false);

        await runner.Received(1).ExecuteAsync(
            feeds[0].Url, Arg.Is<FileInfo?>(f => f!.Name == "hull.jsonld"),
            Arg.Any<int>(), Arg.Any<bool>(),
            Arg.Any<FileInfo?>(), Arg.Any<CancellationToken>());
        await runner.Received(1).ExecuteAsync(
            feeds[1].Url, Arg.Is<FileInfo?>(f => f!.Name == "hull-2.jsonld"),
            Arg.Any<int>(), Arg.Any<bool>(),
            Arg.Any<FileInfo?>(), Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDir.FullName))
            Directory.Delete(_outputDir.FullName, recursive: true);
    }
}
