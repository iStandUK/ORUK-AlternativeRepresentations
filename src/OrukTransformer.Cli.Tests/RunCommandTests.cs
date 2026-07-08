using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OrukApiClient;
using OrukModels.Models;
using OrukModels.SchemaOrg;
using OrukTransformer.Cli;
using OrukTransformer.Cli.Output;
using OrukTransformer.Core.Mapping;
using OrukTransformer.Core.Vodim;

namespace OrukTransformer.Cli.Tests;

public class RunCommandTests
{
    private static readonly Uri SourceUrl = new("https://example.org/services");

    [Fact]
    public async Task ExecuteAsync_WhenJsonLdToStdout_DoesNotWriteVodimReport()
    {
        var serviceClient = Substitute.For<IOrukServiceClient>();
        var transformer = Substitute.For<IOrukToSchemaOrgTransformer>();
        var merger = Substitute.For<IJsonLdMerger>();
        var writer = Substitute.For<IJsonLdWriter>();
        var reporter = Substitute.For<IVodimReporter>();
        var dataQualityReportWriter = Substitute.For<IDataQualityReportWriter>();

        serviceClient.SearchAsync(SourceUrl, Arg.Any<OrukServiceQuery>(), Arg.Any<CancellationToken>())
            .Returns(GetServices(new OrukService { Id = "svc-1", Name = "Service 1" }));

        transformer.Transform(Arg.Any<OrukService>(), Arg.Any<TransformationOptions>())
            .Returns(new TransformationResult
            {
                Document = new SchemaOrgDocument(),
                Report = new TransformationReport("svc-1")
            });

        merger.Merge(Arg.Any<IReadOnlyList<TransformationResult>>())
            .Returns(new SchemaOrgDocument());

        var sut = new RunCommand(
            serviceClient,
            transformer,
            merger,
            writer,
            reporter,
            dataQualityReportWriter,
            NullLogger<RunCommand>.Instance);

        var exitCode = await sut.ExecuteAsync(
            SourceUrl,
            jsonLdFile: null,
            maxRecords: 10,
            verbose: false,
            dataQualityReportFile: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(0, exitCode);
        reporter.DidNotReceive().WriteReport(
            Arg.Any<IReadOnlyList<TransformationReport>>(),
            Arg.Any<Uri>(),
            Arg.Any<bool>(),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenJsonLdToFile_WritesVodimReport()
    {
        var serviceClient = Substitute.For<IOrukServiceClient>();
        var transformer = Substitute.For<IOrukToSchemaOrgTransformer>();
        var merger = Substitute.For<IJsonLdMerger>();
        var writer = Substitute.For<IJsonLdWriter>();
        var reporter = Substitute.For<IVodimReporter>();
        var dataQualityReportWriter = Substitute.For<IDataQualityReportWriter>();

        serviceClient.SearchAsync(SourceUrl, Arg.Any<OrukServiceQuery>(), Arg.Any<CancellationToken>())
            .Returns(GetServices(new OrukService { Id = "svc-1", Name = "Service 1" }));

        transformer.Transform(Arg.Any<OrukService>(), Arg.Any<TransformationOptions>())
            .Returns(new TransformationResult
            {
                Document = new SchemaOrgDocument(),
                Report = new TransformationReport("svc-1")
            });

        merger.Merge(Arg.Any<IReadOnlyList<TransformationResult>>())
            .Returns(new SchemaOrgDocument());

        var sut = new RunCommand(
            serviceClient,
            transformer,
            merger,
            writer,
            reporter,
            dataQualityReportWriter,
            NullLogger<RunCommand>.Instance);

        var exitCode = await sut.ExecuteAsync(
            SourceUrl,
            jsonLdFile: new FileInfo(Path.Combine(Path.GetTempPath(), "output.jsonld")),
            maxRecords: 10,
            verbose: false,
            dataQualityReportFile: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(0, exitCode);
        reporter.Received(1).WriteReport(
            Arg.Any<IReadOnlyList<TransformationReport>>(),
            SourceUrl,
            false,
            true);
    }

    private static async IAsyncEnumerable<OrukService> GetServices(params OrukService[] services)
    {
        foreach (var service in services)
        {
            yield return service;
            await Task.Yield();
        }
    }
}
