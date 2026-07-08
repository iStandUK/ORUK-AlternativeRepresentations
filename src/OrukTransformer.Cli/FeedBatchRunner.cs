using Microsoft.Extensions.Logging;
using OrukTransformer.Cli.Feeds;

namespace OrukTransformer.Cli;

/// <summary>
/// Runs every feed from a <c>feeds.json</c> file through the single-feed
/// <see cref="IRunCommand"/> pipeline, writing a JSON-LD document and an
/// xHTML5 data-quality report per feed into an output directory. File names are
/// derived from each feed's name (see <see cref="OutputFileNaming"/>).
/// </summary>
public sealed class FeedBatchRunner
{
    private readonly IRunCommand _runner;
    private readonly ILogger<FeedBatchRunner> _logger;

    public FeedBatchRunner(IRunCommand runner, ILogger<FeedBatchRunner> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    /// <summary>
    /// Processes all <paramref name="feeds"/> into <paramref name="outputDirectory"/>.
    /// Individual feed failures are logged and do not abort the batch.
    /// </summary>
    /// <returns><c>0</c> only when every feed produced output; <c>1</c> if any feed failed.</returns>
    public async Task<int> ExecuteAsync(
        IReadOnlyList<FeedEntry> feeds,
        DirectoryInfo outputDirectory,
        int maxRecords,
        bool verbose,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory.FullName);

        _logger.LogInformation(
            "Processing {Count} feed(s) into '{Directory}'.",
            feeds.Count, outputDirectory.FullName);

        var usedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var succeeded = 0;
        var failed = new List<string>();

        foreach (var feed in feeds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var slug = UniqueSlug(feed.DisplayName, usedSlugs);
            var jsonLdFile = new FileInfo(
                Path.Combine(outputDirectory.FullName, OutputFileNaming.JsonLdFileName(slug)));
            var dataQualityFile = new FileInfo(
                Path.Combine(outputDirectory.FullName, OutputFileNaming.DataQualityFileName(slug)));

            // feeds.json holds feed base URLs; the service client resolves the
            // /services endpoint (and RPDE cursors) itself.
            _logger.LogInformation(
                "[{Feed}] {Url} → {JsonLd} + {Report}",
                feed.DisplayName, feed.Url, jsonLdFile.Name, dataQualityFile.Name);

            try
            {
                var exitCode = await _runner.ExecuteAsync(
                    feed.Url, jsonLdFile, maxRecords, verbose, dataQualityFile, cancellationToken);

                if (exitCode == 0)
                {
                    succeeded++;
                }
                else
                {
                    failed.Add(feed.DisplayName);
                    _logger.LogWarning(
                        "[{Feed}] produced no output (exit code {ExitCode}).",
                        feed.DisplayName, exitCode);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed.Add(feed.DisplayName);
                _logger.LogError(ex, "[{Feed}] failed: {Message}", feed.DisplayName, ex.Message);
            }
        }

        _logger.LogInformation(
            "Batch complete: {Succeeded} succeeded, {Failed} failed.",
            succeeded, failed.Count);

        if (failed.Count > 0)
            _logger.LogWarning("Failed feed(s): {Feeds}", string.Join(", ", failed));

        // Strict: success only when every feed produced output.
        return failed.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Slugifies <paramref name="name"/> and disambiguates collisions by appending
    /// <c>-2</c>, <c>-3</c>, … so no two feeds overwrite each other's files.
    /// </summary>
    private static string UniqueSlug(string name, HashSet<string> used)
    {
        var baseSlug = OutputFileNaming.Slugify(name);
        var slug = baseSlug;
        var suffix = 2;

        while (!used.Add(slug))
        {
            slug = $"{baseSlug}-{suffix}";
            suffix++;
        }

        return slug;
    }
}
