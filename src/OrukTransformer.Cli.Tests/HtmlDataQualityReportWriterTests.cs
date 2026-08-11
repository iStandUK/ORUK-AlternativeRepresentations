using OrukTransformer.Cli.Output;

namespace OrukTransformer.Cli.Tests;

/// <summary>
/// Tests for the thin file-writing wrapper <see cref="HtmlDataQualityReportWriter"/>.
///
/// The HTML content itself is covered by <c>HtmlDataQualityReportBuilderTests</c> in
/// <c>OrukTransformer.Core.Tests</c>; this only verifies the wrapper persists the built
/// report to disk.
/// </summary>
public class HtmlDataQualityReportWriterTests
{
    [Fact]
    public async Task WriteAsync_WritesReportToFile()
    {
        var writer = new HtmlDataQualityReportWriter();
        var sourceUrl = new Uri("https://example.org/services");
        var path = Path.Combine(Path.GetTempPath(), $"dq-{Guid.NewGuid():N}.html");
        var outputFile = new FileInfo(path);

        try
        {
            await writer.WriteAsync([], sourceUrl, outputFile);

            Assert.True(File.Exists(path));
            var content = await File.ReadAllTextAsync(path);
            Assert.Contains("<!DOCTYPE html>", content);
            Assert.Contains("ORUK Data Quality Report", content);
            Assert.Contains(sourceUrl.ToString(), content);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
