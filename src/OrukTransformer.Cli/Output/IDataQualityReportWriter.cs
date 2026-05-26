using OrukTransformer.Core.Vodim;

namespace OrukTransformer.Cli.Output;

/// <summary>
/// Writes an HTML data-quality report derived from a batch of VODIM
/// <see cref="TransformationReport"/> objects.
/// </summary>
public interface IDataQualityReportWriter
{
    /// <summary>
    /// Writes an xHTML5 data-quality report for the given set of transformation
    /// reports to <paramref name="outputFile"/>.
    ///
    /// <para>
    /// The report lists every distinct ORUK field path found across all services
    /// as an <c>&lt;h2&gt;</c> heading.  Under each heading the VODIM metric
    /// breakdown (count per classification) is shown, together with a de-duplicated
    /// list of issue messages (notes) and their occurrence counts.
    /// Instance-specific details such as individual source-field values are
    /// deliberately omitted.
    /// </para>
    /// </summary>
    /// <param name="reports">Per-service transformation reports.</param>
    /// <param name="sourceUrl">The ORUK endpoint URL, used in the report header.</param>
    /// <param name="outputFile">File to write the HTML report to.</param>
    /// <param name="overallWarnings">
    /// Optional report-level warnings to display under the Overall VODIM Summary.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAsync(
        IReadOnlyList<TransformationReport> reports,
        Uri sourceUrl,
        FileInfo outputFile,
        IReadOnlyList<string>? overallWarnings = null,
        CancellationToken cancellationToken = default);
}
