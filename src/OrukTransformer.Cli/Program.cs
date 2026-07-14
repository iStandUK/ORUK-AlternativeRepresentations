using System.CommandLine;
using Microsoft.Extensions.Logging;
using OrukApiClient;
using OrukApiClient.Internal;
using OrukTransformer.Cli;
using OrukTransformer.Cli.Feeds;
using OrukTransformer.Cli.Output;
using OrukTransformer.Core.Mapping;

// ── Options ──────────────────────────────────────────────────────────────────

var orukUrlOption = new Option<string?>("--oruk-url")
{
    Description = "URL of the ORUK v3 GET /services endpoint. " +
                  "Mutually exclusive with --feeds; supply one or the other.",
    DefaultValueFactory = _ => null
};

var feedsOption = new Option<FileInfo?>("--feeds")
{
    Description = "Path to a feeds.json file. When supplied, every feed in the file is processed " +
                  "in batch mode: a JSON-LD document and a data-quality report are written per feed " +
                  "into the output directory, with file names derived from each feed's name. " +
                  "Mutually exclusive with --oruk-url.",
    DefaultValueFactory = _ => null
};

var outputDirOption = new Option<DirectoryInfo?>("--output-dir")
{
    Description = "Directory for generated output files (created if it does not exist). " +
                  "In batch mode (--feeds) per-feed files are written here. In single mode it is " +
                  "the base directory for --json-ld and --data-quality-report. " +
                  "Defaults to the current directory.",
    DefaultValueFactory = _ => null
};

var jsonLdOption = new Option<FileInfo?>("--json-ld")
{
    Description = "File path to write the generated JSON-LD output. " +
                  "Omit to write to stdout. Ignored in batch mode (--feeds).",
    DefaultValueFactory = _ => null
};

var maxRecordsOption = new Option<int>("--max-records")
{
    Description = "Maximum number of service records to retrieve (per feed). " +
                  "Values less than 1 mean no limit.",
    DefaultValueFactory = _ => 50
};

var verboseOption = new Option<bool>("--verbose")
{
    Description = "Emit per-service VODIM field-level data-quality detail " +
                  "in addition to the summary report.",
    DefaultValueFactory = _ => false
};

var dataQualityReportOption = new Option<FileInfo?>("--data-quality-report")
{
    Description = "Write an xHTML5 data-quality report to this file. " +
                  "When omitted the report is not generated. " +
                  "Supply a path, e.g. --data-quality-report oruk-schema_org.html. " +
                  "Ignored in batch mode (--feeds), which names reports per feed.",
    DefaultValueFactory = _ => null
};

var logLevelOption = new Option<string>("--log-level")
{
    Description = "Minimum log level for console output. " +
                  "Valid values: trace, debug, information, warning, error, critical, none. " +
                  "Defaults to 'information'.",
    DefaultValueFactory = _ => "information"
};

var quietOption = new Option<bool>("--quiet")
{
    Description = "Suppress informational log output (equivalent to --log-level warning). " +
                  "VODIM summary is always written regardless of this flag.",
    DefaultValueFactory = _ => false
};

var timeoutOption = new Option<int>("--timeout")
{
    Description = "HTTP request timeout in seconds for each page fetch. " +
                  "Defaults to 30. Values less than 1 are treated as 30.",
    DefaultValueFactory = _ => 30
};

var formatOption = new Option<string>("--format")
{
    Description = "Output format. Currently only 'json-ld' is supported. " +
                  "Defaults to 'json-ld'.",
    DefaultValueFactory = _ => "json-ld"
};

// ── Root command ──────────────────────────────────────────────────────────────

var rootCommand = new RootCommand(
    "Fetches ORUK v3 service-directory endpoint(s), transforms the services " +
    "to Schema.org JSON-LD, and reports VODIM data quality. Process a single " +
    "endpoint with --oruk-url, or every feed in a feeds.json with --feeds.")
{
    orukUrlOption,
    feedsOption,
    outputDirOption,
    jsonLdOption,
    maxRecordsOption,
    verboseOption,
    dataQualityReportOption,
    logLevelOption,
    quietOption,
    timeoutOption,
    formatOption
};

rootCommand.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
{
    var orukUrlRaw = parseResult.GetValue(orukUrlOption);
    var feedsFile = parseResult.GetValue(feedsOption);
    var outputDir = parseResult.GetValue(outputDirOption);
    var jsonLd = parseResult.GetValue(jsonLdOption);
    var maxRecords = parseResult.GetValue(maxRecordsOption);
    var verbose = parseResult.GetValue(verboseOption);
    var dataQualityReport = parseResult.GetValue(dataQualityReportOption);
    var logLevelRaw = parseResult.GetValue(logLevelOption)!;
    var quiet = parseResult.GetValue(quietOption);
    var timeoutSeconds = parseResult.GetValue(timeoutOption);
    var format = parseResult.GetValue(formatOption)!;
    var logLevelProvided = WasOptionProvided(parseResult, "--log-level");
    var quietProvided = WasOptionProvided(parseResult, "--quiet");
    var batchMode = feedsFile is not null;

    // ── Mode selection ────────────────────────────────────────────────────────

    if (batchMode && !string.IsNullOrWhiteSpace(orukUrlRaw))
    {
        Console.Error.WriteLine(
            "Error: '--oruk-url' and '--feeds' are mutually exclusive. Supply one or the other.");
        Environment.Exit(1);
        return;
    }
    if (!batchMode && string.IsNullOrWhiteSpace(orukUrlRaw))
    {
        Console.Error.WriteLine("Error: one of '--oruk-url' or '--feeds' is required.");
        Environment.Exit(1);
        return;
    }

    // ── Shared validation: --format ──────────────────────────────────────────

    if (!string.Equals(format, "json-ld", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(
            $"Error: '--format' value '{format}' is not supported. Only 'json-ld' is currently available.");
        Environment.Exit(1);
        return;
    }

    // ── Batch mode (--feeds) ──────────────────────────────────────────────────

    if (batchMode)
    {
        if (jsonLd is not null || dataQualityReport is not null)
        {
            Console.Error.WriteLine(
                "Error: '--json-ld' and '--data-quality-report' cannot be combined with '--feeds'. " +
                "Batch mode names outputs per feed; use '--output-dir' to choose where they go.");
            Environment.Exit(1);
            return;
        }

        // Batch always writes to files, so log level resolves as for file output.
        if (!quiet && !CliOutputModePolicy.TryParseLogLevel(logLevelRaw, out _))
        {
            Console.Error.WriteLine(
                $"Error: '--log-level' value '{logLevelRaw}' is invalid. " +
                "Valid values are: trace, debug, information, warning, error, critical, none.");
            Environment.Exit(1);
            return;
        }

        var load = FeedsFileLoader.Load(feedsFile!.FullName);
        if (!load.Success)
        {
            Console.Error.WriteLine($"Error: {load.Error}");
            Environment.Exit(1);
            return;
        }

        var batchLogLevel = CliOutputModePolicy.ResolveEffectiveLogLevel(
            writingJsonToStdout: false, quiet, logLevelRaw);
        var outputDirectory = outputDir ?? new DirectoryInfo(Directory.GetCurrentDirectory());

        var batchExitCode = await WithPipeline(timeoutSeconds, batchLogLevel,
            async (runner, loggerFactory) =>
            {
                var batchRunner = new FeedBatchRunner(
                    runner, loggerFactory.CreateLogger<FeedBatchRunner>());
                return await batchRunner.ExecuteAsync(
                    load.Feeds, outputDirectory, maxRecords, verbose, cancellationToken);
            });

        Environment.Exit(batchExitCode);
        return;
    }

    // ── Single mode (--oruk-url) ──────────────────────────────────────────────

    // Apply --output-dir as the base directory for the single-mode output files.
    jsonLd = ResolveUnderDirectory(jsonLd, outputDir);
    dataQualityReport = ResolveUnderDirectory(dataQualityReport, outputDir);
    if (outputDir is not null && (jsonLd is not null || dataQualityReport is not null))
        Directory.CreateDirectory(outputDir.FullName);

    var writingJsonToStdout = jsonLd is null;

    if (!Uri.TryCreate(orukUrlRaw, UriKind.Absolute, out var orukUrl))
    {
        Console.Error.WriteLine($"Error: '--oruk-url' value '{orukUrlRaw}' is not a valid absolute URI.");
        Environment.Exit(1);
        return;
    }
    if (LooksLikeDuplicatedScheme(orukUrlRaw!, orukUrl))
    {
        const string warning =
            "The supplied ORUK URL appears malformed due to a duplicated URL scheme. " +
            "Check the URL and use a single scheme prefix such as https://example.org/services.";
        Console.Error.WriteLine(
            $"Error: '--oruk-url' value '{orukUrlRaw}' appears malformed (duplicate URL scheme). " +
            "Use a URL like 'https://example.org/services'.");

        if (dataQualityReport is not null)
        {
            var mismatchReportWriter = new HtmlDataQualityReportWriter();
            await mismatchReportWriter.WriteAsync(
                [],
                orukUrl,
                dataQualityReport,
                [warning],
                cancellationToken);
            Console.Error.WriteLine($"Data-quality report written to '{dataQualityReport.FullName}'.");
        }

        Environment.Exit(1);
        return;
    }

    // Validate stdout mode constraints: when JSON-LD is written to stdout we
    // keep stdout reserved for transformed Schema.org only.
    var optionConflictError = CliOutputModePolicy.ValidateForOutputMode(
        writingJsonToStdout,
        verbose,
        logLevelProvided,
        quietProvided);
    if (optionConflictError is not null)
    {
        Console.Error.WriteLine($"Error: {optionConflictError}");
        Environment.Exit(1);
        return;
    }

    if (!writingJsonToStdout
        && !quiet
        && !CliOutputModePolicy.TryParseLogLevel(logLevelRaw, out _))
    {
        Console.Error.WriteLine(
            $"Error: '--log-level' value '{logLevelRaw}' is invalid. " +
            "Valid values are: trace, debug, information, warning, error, critical, none.");
        Environment.Exit(1);
        return;
    }

    var logLevel = CliOutputModePolicy.ResolveEffectiveLogLevel(
        writingJsonToStdout,
        quiet,
        logLevelRaw);

    var exitCode = await WithPipeline(timeoutSeconds, logLevel,
        (runner, _) => runner.ExecuteAsync(
            orukUrl, jsonLd, maxRecords, verbose, dataQualityReport, cancellationToken));

    Environment.Exit(exitCode);
});

var result = rootCommand.Parse(args);
return await result.InvokeAsync();

// ── Helpers ───────────────────────────────────────────────────────────────────

// Builds the HTTP client, logging and transform pipeline, runs the supplied body,
// and disposes the client/logger factory afterwards.
static async Task<int> WithPipeline(
    int timeoutSeconds,
    LogLevel logLevel,
    Func<RunCommand, ILoggerFactory, Task<int>> body)
{
    using var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : 30)
    };
    httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
        "OrukTransformer.Cli/1.0 (+https://github.com/iStandUK/ORUK-AlternativeRepresentations)");

    using var loggerFactory = LoggerFactory.Create(builder =>
        builder.AddConsole().SetMinimumLevel(logLevel));

    // The geocoder is only consulted for proximity queries, which the CLI never sets,
    // so it is effectively inert here; it is required by the service client's contract.
    var geocoder = new PostcodesIoGeocoder(
        httpClient, loggerFactory.CreateLogger<PostcodesIoGeocoder>());
    var serviceClient = new OrukServiceClient(
        httpClient, loggerFactory.CreateLogger<OrukServiceClient>(), geocoder);

    var runner = new RunCommand(
        serviceClient,
        new OrukToSchemaOrgTransformer(),
        new JsonLdMerger(),
        new JsonLdWriter(),
        new VodimReporter(),
        new HtmlDataQualityReportWriter(),
        loggerFactory.CreateLogger<RunCommand>());

    return await body(runner, loggerFactory);
}

// Re-homes a relative or absolute output file under a base directory (by file name),
// or returns the file unchanged when no directory was supplied.
static FileInfo? ResolveUnderDirectory(FileInfo? file, DirectoryInfo? directory) =>
    file is null || directory is null
        ? file
        : new FileInfo(Path.Combine(directory.FullName, file.Name));

static bool WasOptionProvided(ParseResult parseResult, string longAlias) =>
    parseResult.Tokens.Any(t =>
        string.Equals(t.Value, longAlias, StringComparison.OrdinalIgnoreCase)
        || t.Value.StartsWith($"{longAlias}=", StringComparison.OrdinalIgnoreCase));

static bool LooksLikeDuplicatedScheme(string rawUrl, Uri parsedUrl)
{
    if (!parsedUrl.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        && !parsedUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var raw = rawUrl.Trim();
    var schemePrefix = $"{parsedUrl.Scheme}://";
    if (!raw.StartsWith(schemePrefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    var remainder = raw[schemePrefix.Length..];
    return remainder.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || remainder.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || remainder.StartsWith("http//", StringComparison.OrdinalIgnoreCase)
           || remainder.StartsWith("https//", StringComparison.OrdinalIgnoreCase);
}
