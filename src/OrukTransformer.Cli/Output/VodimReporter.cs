using OrukTransformer.Core.Vodim;

namespace OrukTransformer.Cli.Output;

/// <summary>
/// Formats and writes VODIM data-quality reports to stdout or stderr.
/// </summary>
public sealed class VodimReporter : IVodimReporter
{
    /// <inheritdoc/>
    public void WriteReport(
        IReadOnlyList<TransformationReport> reports,
        Uri sourceUrl,
        bool verbose,
        bool jsonLdToFile)
    {
        var writer = jsonLdToFile ? Console.Out : Console.Error;

        WriteSummary(writer, reports, sourceUrl);

        if (verbose)
        {
            WriteVerboseDetail(writer, reports);
        }
    }

    // ── Summary ──────────────────────────────────────────────────────────────────

    internal static string BuildSummaryText(
        IReadOnlyList<TransformationReport> reports, Uri sourceUrl)
    {
        var counts = VodimCounts.FromReports(reports);
        int total = counts.Total;

        static string Pct(int count, int tot) =>
            tot == 0 ? " 0%" : $"{count * 100 / tot,2}%";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"VODIM Summary — {reports.Count} service(s) transformed from {sourceUrl}");
        sb.AppendLine($"  V Valid     : {counts.Valid,6}  ({Pct(counts.Valid, total)})");
        sb.AppendLine($"  O Other     : {counts.Other,6}  ({Pct(counts.Other, total)})");
        sb.AppendLine($"  D Default   : {counts.Default,6}  ({Pct(counts.Default, total)})");
        sb.AppendLine($"  I Invalid   : {counts.Invalid,6}  ({Pct(counts.Invalid, total)})");
        sb.AppendLine($"  M Missing   : {counts.Missing,6}  ({Pct(counts.Missing, total)})");
        sb.AppendLine($"  U Unmapped  : {counts.Unmapped,6}  ({Pct(counts.Unmapped, total)})");
        sb.Append($"  Total fields: {total,6}");
        return sb.ToString();
    }

    private static void WriteSummary(
        TextWriter writer,
        IReadOnlyList<TransformationReport> reports,
        Uri sourceUrl)
    {
        writer.WriteLine(BuildSummaryText(reports, sourceUrl));
    }

    // ── Verbose detail ───────────────────────────────────────────────────────────

    private static void WriteVerboseDetail(
        TextWriter writer, IReadOnlyList<TransformationReport> reports)
    {
        foreach (var report in reports)
        {
            bool hasIssues = report.OtherCount > 0 || report.InvalidCount > 0
                || report.UnmappedCount > 0;

            // Identify the service name from the first record's source path, or
            // fall back to the service ID for the heading
            writer.WriteLine();
            writer.WriteLine($"--- Service {report.ServiceId} ---");

            if (!hasIssues)
            {
                writer.WriteLine($"  All {report.TotalCount} field(s) mapped cleanly.");
                continue;
            }

            writer.WriteLine(report.Summary());

            // Emit each record that has issues or is intentionally unmapped
            foreach (var rec in report.Records.Where(
                r => r.Classification is VodimClassification.Other
                    or VodimClassification.Invalid
                    or VodimClassification.Unmapped))
            {
                writer.WriteLine($"  {rec}");
            }
        }
    }
}
