using Microsoft.Extensions.Logging;
using OrukTransformer.Cli.Fetching;
using OrukTransformer.Cli.Output;
using OrukTransformer.Core.Mapping;
using OrukTransformer.Core.Vodim;

namespace OrukTransformer.Cli;

/// <summary>
/// Orchestrates the full fetch → transform → report pipeline for the CLI.
/// </summary>
public sealed class RunCommand
{
    private readonly IOrukFeedPageFetcher _fetcher;
    private readonly IOrukToSchemaOrgTransformer _transformer;
    private readonly IJsonLdMerger _merger;
    private readonly IJsonLdWriter _writer;
    private readonly IVodimReporter _reporter;
    private readonly IDataQualityReportWriter _dataQualityReportWriter;
    private readonly ILogger<RunCommand> _logger;

    public RunCommand(
        IOrukFeedPageFetcher fetcher,
        IOrukToSchemaOrgTransformer transformer,
        IJsonLdMerger merger,
        IJsonLdWriter writer,
        IVodimReporter reporter,
        IDataQualityReportWriter dataQualityReportWriter,
        ILogger<RunCommand> logger)
    {
        _fetcher = fetcher;
        _transformer = transformer;
        _merger = merger;
        _writer = writer;
        _reporter = reporter;
        _dataQualityReportWriter = dataQualityReportWriter;
        _logger = logger;
    }

    /// <summary>
    /// Runs the CLI pipeline.
    /// </summary>
    /// <param name="orukUrl">Base URL of the ORUK v3 <c>/services</c> endpoint.</param>
    /// <param name="jsonLdFile">
    /// Output file path for JSON-LD, or <c>null</c> to write to stdout.
    /// </param>
    /// <param name="maxRecords">
    /// Maximum services to retrieve. Values less than 1 mean no limit.
    /// </param>
    /// <param name="verbose">
    /// When <c>true</c>, per-service field-level VODIM detail is written in
    /// addition to the summary.
    /// </param>
    /// <param name="dataQualityReportFile">
    /// When non-<c>null</c>, an xHTML5 data-quality report is written to this
    /// file in addition to the standard VODIM console output.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>0</c> on success, <c>1</c> if no services could be fetched.
    /// </returns>
    public async Task<int> ExecuteAsync(
        Uri orukUrl,
        FileInfo? jsonLdFile,
        int maxRecords,
        bool verbose,
        FileInfo? dataQualityReportFile,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Fetching ORUK services from {Url} (max records: {Max})",
            orukUrl, maxRecords < 1 ? "unlimited" : maxRecords);

        var options = new TransformationOptions
        {
            BaseUrl = $"{orukUrl.Scheme}://{orukUrl.Host}" +
                      (orukUrl.IsDefaultPort ? string.Empty : $":{orukUrl.Port}")
        };

        // 1. Fetch
        var results = new List<TransformationResult>();
        await foreach (var service in _fetcher.FetchAsync(orukUrl, maxRecords, cancellationToken))
        {
            // 2. Transform (one service at a time to keep memory footprint small)
            var result = _transformer.Transform(service, options);
            results.Add(result);
        }

        if (results.Count == 0)
        {
            _logger.LogError("No services were retrieved from {Url}.", orukUrl);
            return 1;
        }

        _logger.LogInformation("Transformed {Count} service(s).", results.Count);

        // 3. Merge JSON-LD documents
        var merged = _merger.Merge(results);

        // 4. Write JSON-LD output
        await _writer.WriteAsync(merged, jsonLdFile, cancellationToken);

        // 5. Write VODIM console report when JSON-LD is written to file
        var reports = results.Select(r => r.Report).ToList();
        if (jsonLdFile is not null)
        {
            _reporter.WriteReport(reports, orukUrl, verbose, jsonLdToFile: true);
        }

        // 6. Optionally write HTML data-quality report
        if (dataQualityReportFile is not null)
        {
            await _dataQualityReportWriter.WriteAsync(reports, orukUrl, dataQualityReportFile,
                cancellationToken: cancellationToken);
            _logger.LogInformation("Data-quality report written to {File}.",
                dataQualityReportFile.FullName);
        }

        return 0;
    }
}
