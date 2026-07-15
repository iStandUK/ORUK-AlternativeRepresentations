using System.Text;
using OrukTransformer.Core.Reporting;
using OrukTransformer.Core.Vodim;

namespace OrukTransformer.Cli.Output;

/// <summary>
/// Writes the xHTML5 VODIM data-quality report to a file. The report itself is built by
/// the shared <see cref="HtmlDataQualityReportBuilder"/> in Core (also used by the MCP
/// data-quality tool); this writer only handles the file I/O.
/// </summary>
public sealed class HtmlDataQualityReportWriter : IDataQualityReportWriter
{
    /// <inheritdoc/>
    public async Task WriteAsync(
        IReadOnlyList<TransformationReport> reports,
        Uri sourceUrl,
        FileInfo outputFile,
        IReadOnlyList<string>? overallWarnings = null,
        CancellationToken cancellationToken = default)
    {
        var html = HtmlDataQualityReportBuilder.Build(reports, sourceUrl, overallWarnings);
        await File.WriteAllTextAsync(outputFile.FullName, html, Encoding.UTF8, cancellationToken);
    }
}
