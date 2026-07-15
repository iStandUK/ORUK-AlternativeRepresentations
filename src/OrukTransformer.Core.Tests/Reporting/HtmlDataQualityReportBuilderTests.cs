using OrukTransformer.Core.Reporting;
using OrukTransformer.Core.Vodim;

namespace OrukTransformer.Core.Tests.Reporting;

/// <summary>
/// Tests for <see cref="HtmlDataQualityReportBuilder.Build"/>.
///
/// Tests drive the builder directly so that HTML structure can be verified without
/// writing to disk.
/// </summary>
public class HtmlDataQualityReportBuilderTests
{
    private static readonly Uri SourceUrl = new("https://example.org/services");

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static TransformationReport BuildReport(
        string serviceId,
        IEnumerable<FieldMappingRecord> records)
    {
        var report = new TransformationReport(serviceId);
        var addMethod = typeof(TransformationReport)
            .GetMethod("Add", System.Reflection.BindingFlags.NonPublic
                             | System.Reflection.BindingFlags.Instance)!;
        foreach (var rec in records)
            addMethod.Invoke(report, [rec]);
        return report;
    }

    private static FieldMappingRecord MakeRecord(
        string sourcePath,
        string targetPath,
        VodimClassification classification,
        string? sourceValue = null,
        string? note = null) =>
        new()
        {
            SourcePath = sourcePath,
            TargetPath = targetPath,
            Classification = classification,
            SourceValue = sourceValue,
            Note = note
        };

    // ── Tests ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ProducesValidXhtmlDoctype()
    {
        var html = HtmlDataQualityReportBuilder.Build([], SourceUrl);

        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("<html xmlns=\"http://www.w3.org/1999/xhtml\"", html);
        Assert.Contains("<head>", html);
        Assert.Contains("<body>", html);
        Assert.Contains("</html>", html);
    }

    [Fact]
    public void Build_EmbedsCss()
    {
        var html = HtmlDataQualityReportBuilder.Build([], SourceUrl);

        Assert.Contains("<style>", html);
        Assert.Contains("--istanduk-blue", html);
        Assert.Contains("</style>", html);
    }

    [Fact]
    public void Build_IncludesSourceUrl()
    {
        var html = HtmlDataQualityReportBuilder.Build([], SourceUrl);

        Assert.Contains(SourceUrl.ToString(), html);
    }

    [Fact]
    public void Build_OverallWarnings_AppearInOverallSummary()
    {
        var warning = "The supplied ORUK URL appears malformed due to a duplicated URL scheme.";
        var html = HtmlDataQualityReportBuilder.Build([], SourceUrl, [warning]);

        Assert.Contains("Overall VODIM Summary", html);
        Assert.Contains("Input warnings", html);
        Assert.Contains(warning, html);

        var summaryHeadingIndex = html.IndexOf("<h2>Overall VODIM Summary</h2>", StringComparison.Ordinal);
        var summaryEndIndex = html.IndexOf("</section>", summaryHeadingIndex, StringComparison.Ordinal);
        var summarySection = html.Substring(summaryHeadingIndex, summaryEndIndex - summaryHeadingIndex);
        Assert.Contains("Input warnings", summarySection);
        Assert.Contains(warning, summarySection);
    }

    [Fact]
    public void Build_IncludesServiceCount()
    {
        var report = BuildReport("svc-1", [
            MakeRecord("service.name", "GovernmentService.name", VodimClassification.Valid)
        ]);

        var html = HtmlDataQualityReportBuilder.Build([report], SourceUrl);

        Assert.Contains("1", html); // services assessed
    }

    [Fact]
    public void Build_EmptyReports_ShowsNoFieldsMessage()
    {
        var html = HtmlDataQualityReportBuilder.Build([], SourceUrl);

        Assert.Contains("No fields recorded", html);
    }

    [Fact]
    public void Build_FieldPath_AppearsAsHeading()
    {
        var report = BuildReport("svc-1", [
            MakeRecord("service.name", "GovernmentService.name", VodimClassification.Valid)
        ]);

        var html = HtmlDataQualityReportBuilder.Build([report], SourceUrl);

        Assert.Contains("service.name", html);
        Assert.Contains("<h2 class=\"field-heading\">", html);
    }

    [Fact]
    public void Build_CountsAggregatedAcrossServices()
    {
        // Two services, each with an invalid record for the same field
        var report1 = BuildReport("svc-1", [
            MakeRecord("service.url", "GovernmentService.url", VodimClassification.Invalid,
                sourceValue: "not-a-url", note: "Not a valid URL")
        ]);
        var report2 = BuildReport("svc-2", [
            MakeRecord("service.url", "GovernmentService.url", VodimClassification.Invalid,
                sourceValue: "also-bad", note: "Not a valid URL")
        ]);

        var html = HtmlDataQualityReportBuilder.Build([report1, report2], SourceUrl);

        // The note should appear once (deduplicated)
        var firstOccurrence = html.IndexOf("Not a valid URL", StringComparison.Ordinal);
        var lastOccurrence = html.LastIndexOf("Not a valid URL", StringComparison.Ordinal);
        Assert.Equal(firstOccurrence, lastOccurrence);

        // The occurrence count (2) should appear somewhere near the note
        Assert.Contains(">2<", html);
    }

    [Fact]
    public void Build_SourceValueOmittedFromReport()
    {
        const string sensitiveValue = "https://www.second-step.co.uk/";
        var report = BuildReport("svc-1", [
            MakeRecord("organization.website", "Organization.url",
                VodimClassification.Other,
                sourceValue: sensitiveValue,
                note: "Non-standard 'website' field used as fallback for 'url'")
        ]);

        var html = HtmlDataQualityReportBuilder.Build([report], SourceUrl);

        Assert.DoesNotContain(sensitiveValue, html);
        Assert.Contains("Non-standard &#39;website&#39; field used as fallback for &#39;url&#39;", html);
    }

    [Fact]
    public void Build_VodimClassificationCodesPresent()
    {
        var report = BuildReport("svc-1", [
            MakeRecord("service.name", "GovernmentService.name", VodimClassification.Valid),
            MakeRecord("service.status", "GovernmentService.additionalProperty",
                VodimClassification.Other, note: "Unrecognised status"),
            MakeRecord("service.fees", "GovernmentService.offers",
                VodimClassification.Default, note: "Defaulted to free"),
            MakeRecord("service.url", "GovernmentService.url",
                VodimClassification.Invalid, note: "Not a URL"),
            MakeRecord("service.description", "GovernmentService.description",
                VodimClassification.Missing),
            MakeRecord("service.extra", "—",
                VodimClassification.Unmapped)
        ]);

        var html = HtmlDataQualityReportBuilder.Build([report], SourceUrl);

        Assert.Contains(">V<", html);
        Assert.Contains(">O<", html);
        Assert.Contains(">D<", html);
        Assert.Contains(">I<", html);
        Assert.Contains(">M<", html);
        Assert.Contains(">U<", html);
    }

    [Fact]
    public void Build_MultipleDistinctFieldPaths_EachHasSection()
    {
        var report = BuildReport("svc-1", [
            MakeRecord("service.name", "GovernmentService.name", VodimClassification.Valid),
            MakeRecord("service.url", "GovernmentService.url", VodimClassification.Invalid)
        ]);

        var html = HtmlDataQualityReportBuilder.Build([report], SourceUrl);

        Assert.Contains("service.name", html);
        Assert.Contains("service.url", html);
        // Two field headings
        Assert.Equal(2, CountOccurrences(html, "field-service-"));
    }

    [Fact]
    public void Build_NoteWithoutSourceValue_RenderedCleanly()
    {
        var report = BuildReport("svc-1", [
            MakeRecord("service.status", "GovernmentService.additionalProperty",
                VodimClassification.Other,
                sourceValue: null,
                note: "Unrecognised ORUK status")
        ]);

        var html = HtmlDataQualityReportBuilder.Build([report], SourceUrl);

        Assert.Contains("Unrecognised ORUK status", html);
    }

    [Fact]
    public void Build_NoNotes_NotesSectionAbsent()
    {
        var report = BuildReport("svc-1", [
            MakeRecord("service.name", "GovernmentService.name", VodimClassification.Valid)
        ]);

        var html = HtmlDataQualityReportBuilder.Build([report], SourceUrl);

        Assert.DoesNotContain("Issue Notes", html);
    }

    [Fact]
    public void Build_IncludesSvgPieChart()
    {
        var report = BuildReport("svc-1", [
            MakeRecord("field1", "Target.field1", VodimClassification.Valid),
            MakeRecord("field2", "Target.field2", VodimClassification.Invalid),
            MakeRecord("field3", "Target.field3", VodimClassification.Missing)
        ]);

        var html = HtmlDataQualityReportBuilder.Build([report], SourceUrl);

        Assert.Contains("<svg", html);
        Assert.Contains("viewBox=\"-60 0 360 200\"", html);
        Assert.Contains("<path", html);
        // Check that the chart is in the summary section
        Assert.Contains("pie-chart", html);
        // Check that percentage labels are present
        Assert.Contains("<text", html);
    }

    [Fact]
    public void Build_PieChart_OnlyShowsNonZeroSegments()
    {
        // Only Valid and Invalid have counts
        var report = BuildReport("svc-1", [
            MakeRecord("field1", "Target.field1", VodimClassification.Valid),
            MakeRecord("field2", "Target.field2", VodimClassification.Invalid)
        ]);

        var html = HtmlDataQualityReportBuilder.Build([report], SourceUrl);

        // Should have SVG with paths
        Assert.Contains("<svg", html);
        // We have: 1 summary chart with 2 segments (Valid + Invalid),
        // plus 2 field charts each with 1 segment = 2 + 1 + 1 = 4 total paths
        var pathCount = CountOccurrences(html, "<path");
        Assert.Equal(4, pathCount);
    }

    [Fact]
    public void Build_IncludesPieChartsForEachField()
    {
        var report = BuildReport("svc-1", [
            MakeRecord("field1", "Target.field1", VodimClassification.Valid),
            MakeRecord("field2", "Target.field2", VodimClassification.Invalid),
            MakeRecord("field3", "Target.field3", VodimClassification.Missing)
        ]);

        var html = HtmlDataQualityReportBuilder.Build([report], SourceUrl);

        // Should have multiple pie charts: 1 overall + 3 per field = 4 total
        var pieChartCount = CountOccurrences(html, "class=\"pie-chart\"");
        Assert.Equal(4, pieChartCount); // 1 summary + 3 fields

        // Each field should have a field-container
        var fieldContainerCount = CountOccurrences(html, "field-container");
        Assert.True(fieldContainerCount >= 3, $"Expected at least 3 field-containers but found {fieldContainerCount}");
    }

    [Fact]
    public void Build_PieChart_FullCircleFor100PercentSingleSegment()
    {
        // Create a field with 100% invalid (single classification)
        var report = BuildReport("svc-1", [
            MakeRecord("field1", "Target.field1", VodimClassification.Invalid),
            MakeRecord("field1", "Target.field1", VodimClassification.Invalid),
            MakeRecord("field1", "Target.field1", VodimClassification.Invalid)
        ]);

        var html = HtmlDataQualityReportBuilder.Build([report], SourceUrl);

        // The field-level chart should have a single path drawing a full circle
        // Check that it contains the two-arc circle pattern (not a wedge)
        Assert.Contains("<path", html);
        // A full circle will have two 180-degree arcs
        Assert.Contains("A 80,80 0 0,1", html); // First semicircle arc
    }

    [Fact]
    public void Build_PieChart_SmallSegments_ShowInLegend()
    {
        // Create a scenario with a very small segment (< 3%)
        // 872 Valid, 1 Invalid, 1 Missing = 874 total
        // Invalid: 1/874 = 0.11% (below 3% threshold)
        // Missing: 1/874 = 0.11% (below 3% threshold)
        var records = new List<FieldMappingRecord>();

        // Add 872 valid records
        for (int i = 0; i < 872; i++)
        {
            records.Add(MakeRecord("service.url", "GovernmentService.url", VodimClassification.Valid));
        }

        // Add 1 invalid record (0.11%)
        records.Add(MakeRecord("service.url", "GovernmentService.url", VodimClassification.Invalid));

        // Add 1 missing record (0.11%)
        records.Add(MakeRecord("service.url", "GovernmentService.url", VodimClassification.Missing));

        var report = BuildReport("svc-1", records);
        var html = HtmlDataQualityReportBuilder.Build([report], SourceUrl);

        // Should contain a chart-legend div for small segments
        Assert.Contains("chart-legend", html);
        Assert.Contains("Small segments:", html);

        // The legend should list Invalid and Missing
        Assert.Contains("Invalid", html);
        Assert.Contains("Missing", html);

        // Check that percentages are shown with 1 decimal place (P1 format)
        // 1/874 = 0.114%, which rounds to 0.1%
        Assert.Contains("0.1%", html);
    }

    [Fact]
    public void Build_PieChart_LargeSegments_ShowOnChart()
    {
        // Create a scenario where all segments are >= 3%
        // 50 Valid (50%), 30 Invalid (30%), 20 Missing (20%)
        var records = new List<FieldMappingRecord>();

        for (int i = 0; i < 50; i++)
        {
            records.Add(MakeRecord("service.name", "GovernmentService.name", VodimClassification.Valid));
        }

        for (int i = 0; i < 30; i++)
        {
            records.Add(MakeRecord("service.name", "GovernmentService.name", VodimClassification.Invalid));
        }

        for (int i = 0; i < 20; i++)
        {
            records.Add(MakeRecord("service.name", "GovernmentService.name", VodimClassification.Missing));
        }

        var report = BuildReport("svc-1", records);
        var html = HtmlDataQualityReportBuilder.Build([report], SourceUrl);

        // Should NOT contain a chart-legend (all segments are above threshold)
        Assert.DoesNotContain("Small segments:", html);

        // All segments should have text labels on the chart
        Assert.Contains(">Valid:", html);
        Assert.Contains(">Invalid:", html);
        Assert.Contains(">Missing:", html);
    }

    // ── Utility ───────────────────────────────────────────────────────────────────

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
