using OrukTransformer.Core.Vodim;

namespace OrukTransformer.Core.Tests.Vodim;

/// <summary>
/// Tests for the shared VODIM roll-up (<see cref="VodimCounts"/> and
/// <see cref="VodimSummary"/>) used by the CLI console summary, the xHTML5 report
/// builder, and the MCP data-quality tool.
/// </summary>
public class VodimSummaryTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static readonly System.Reflection.MethodInfo AddMethod =
        typeof(TransformationReport).GetMethod("Add",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    private static TransformationReport BuildReport(string serviceId, params FieldMappingRecord[] records)
    {
        var report = new TransformationReport(serviceId);
        foreach (var rec in records)
            AddMethod.Invoke(report, [rec]);
        return report;
    }

    private static FieldMappingRecord Record(
        string sourcePath,
        VodimClassification classification,
        string targetPath = "—",
        string? note = null) =>
        new()
        {
            SourcePath = sourcePath,
            TargetPath = targetPath,
            Classification = classification,
            Note = note
        };

    // ── VodimCounts ──────────────────────────────────────────────────────────────

    [Fact]
    public void Counts_Total_IsSumOfAllClassifications()
    {
        var counts = new VodimCounts(1, 2, 3, 4, 5, 6);
        Assert.Equal(21, counts.Total);
    }

    [Fact]
    public void Counts_Indexer_ReturnsPerClassificationCount()
    {
        var counts = new VodimCounts(1, 2, 3, 4, 5, 6);

        Assert.Equal(1, counts[VodimClassification.Valid]);
        Assert.Equal(2, counts[VodimClassification.Other]);
        Assert.Equal(3, counts[VodimClassification.Default]);
        Assert.Equal(4, counts[VodimClassification.Invalid]);
        Assert.Equal(5, counts[VodimClassification.Missing]);
        Assert.Equal(6, counts[VodimClassification.Unmapped]);
    }

    [Fact]
    public void Counts_PercentOf_UsesTruncatingIntegerArithmetic()
    {
        // 1 of 3 = 33.33% → truncates to 33
        var counts = new VodimCounts(1, 1, 1, 0, 0, 0);
        Assert.Equal(33, counts.PercentOf(VodimClassification.Valid));
    }

    [Fact]
    public void Counts_PercentOf_EmptyIsZero()
    {
        var counts = new VodimCounts(0, 0, 0, 0, 0, 0);
        Assert.Equal(0, counts.PercentOf(VodimClassification.Valid));
    }

    [Fact]
    public void Counts_FromReports_SumsAcrossReports()
    {
        var r1 = BuildReport("svc-1",
            Record("service.name", VodimClassification.Valid),
            Record("service.url", VodimClassification.Invalid));
        var r2 = BuildReport("svc-2",
            Record("service.name", VodimClassification.Valid),
            Record("service.status", VodimClassification.Other));

        var counts = VodimCounts.FromReports([r1, r2]);

        Assert.Equal(2, counts.Valid);
        Assert.Equal(1, counts.Other);
        Assert.Equal(1, counts.Invalid);
        Assert.Equal(4, counts.Total);
    }

    [Fact]
    public void Counts_FromRecords_CountsByClassification()
    {
        var counts = VodimCounts.FromRecords([
            Record("a", VodimClassification.Valid),
            Record("b", VodimClassification.Valid),
            Record("c", VodimClassification.Unmapped)
        ]);

        Assert.Equal(2, counts.Valid);
        Assert.Equal(1, counts.Unmapped);
        Assert.Equal(3, counts.Total);
    }

    // ── VodimSummary.Aggregate ───────────────────────────────────────────────────

    [Fact]
    public void Aggregate_ServiceCount_IsReportCount()
    {
        var summary = VodimSummary.Aggregate([
            BuildReport("svc-1", Record("service.name", VodimClassification.Valid)),
            BuildReport("svc-2", Record("service.name", VodimClassification.Valid))
        ]);

        Assert.Equal(2, summary.ServiceCount);
    }

    [Fact]
    public void Aggregate_Empty_HasZeroOverallAndNoFields()
    {
        var summary = VodimSummary.Aggregate([]);

        Assert.Equal(0, summary.ServiceCount);
        Assert.Equal(0, summary.Overall.Total);
        Assert.Empty(summary.Fields);
    }

    [Fact]
    public void Aggregate_GroupsBySourcePath_OrderedByPath()
    {
        var report = BuildReport("svc-1",
            Record("service.url", VodimClassification.Valid),
            Record("service.name", VodimClassification.Valid),
            Record("service.name", VodimClassification.Valid));

        var summary = VodimSummary.Aggregate([report]);

        Assert.Equal(2, summary.Fields.Count);
        // Ordered alphabetically: service.name before service.url
        Assert.Equal("service.name", summary.Fields[0].SourcePath);
        Assert.Equal("service.url", summary.Fields[1].SourcePath);
        Assert.Equal(2, summary.Fields[0].Counts.Valid);
    }

    [Fact]
    public void Aggregate_TargetPath_UsesMostCommonNonPlaceholder()
    {
        var report = BuildReport("svc-1",
            Record("service.url", VodimClassification.Valid, targetPath: "GovernmentService.url"),
            Record("service.url", VodimClassification.Missing, targetPath: "—"),
            Record("service.url", VodimClassification.Valid, targetPath: "GovernmentService.url"));

        var summary = VodimSummary.Aggregate([report]);

        Assert.Equal("GovernmentService.url", Assert.Single(summary.Fields).TargetPath);
    }

    [Fact]
    public void Aggregate_TargetPath_IsPlaceholderWhenAllUnmapped()
    {
        var report = BuildReport("svc-1",
            Record("service.extra", VodimClassification.Unmapped, targetPath: "—"));

        var summary = VodimSummary.Aggregate([report]);

        Assert.Equal("—", Assert.Single(summary.Fields).TargetPath);
    }

    [Fact]
    public void Aggregate_Notes_DeduplicatedWithCountAndClassification()
    {
        var report1 = BuildReport("svc-1",
            Record("service.url", VodimClassification.Invalid, note: "Not a valid URL"));
        var report2 = BuildReport("svc-2",
            Record("service.url", VodimClassification.Invalid, note: "Not a valid URL"));

        var summary = VodimSummary.Aggregate([report1, report2]);

        var note = Assert.Single(Assert.Single(summary.Fields).Notes);
        Assert.Equal("Not a valid URL", note.Text);
        Assert.Equal(2, note.Count);
        Assert.Equal(VodimClassification.Invalid, note.Classification);
    }

    [Fact]
    public void Aggregate_Overall_MatchesRecordTotals()
    {
        var report = BuildReport("svc-1",
            Record("a", VodimClassification.Valid),
            Record("b", VodimClassification.Other),
            Record("c", VodimClassification.Unmapped));

        var summary = VodimSummary.Aggregate([report]);

        Assert.Equal(1, summary.Overall.Valid);
        Assert.Equal(1, summary.Overall.Other);
        Assert.Equal(1, summary.Overall.Unmapped);
        Assert.Equal(3, summary.Overall.Total);
    }
}
