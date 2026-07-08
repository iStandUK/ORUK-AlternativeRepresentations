namespace OrukTransformer.Cli;

/// <summary>
/// Runs the fetch → transform → report pipeline for a single ORUK endpoint.
/// </summary>
public interface IRunCommand
{
    /// <summary>
    /// Fetches, transforms and reports a single ORUK feed.
    /// </summary>
    /// <param name="orukUrl">Base URL of the ORUK v3 <c>/services</c> endpoint.</param>
    /// <param name="jsonLdFile">JSON-LD output file, or <c>null</c> to write to stdout.</param>
    /// <param name="maxRecords">Maximum services to retrieve; values &lt; 1 mean no limit.</param>
    /// <param name="verbose">When <c>true</c>, emit per-service VODIM detail.</param>
    /// <param name="dataQualityReportFile">When non-<c>null</c>, write an xHTML5 data-quality report here.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>0</c> on success, <c>1</c> if no services could be fetched.</returns>
    Task<int> ExecuteAsync(
        Uri orukUrl,
        FileInfo? jsonLdFile,
        int maxRecords,
        bool verbose,
        FileInfo? dataQualityReportFile,
        CancellationToken cancellationToken = default);
}
