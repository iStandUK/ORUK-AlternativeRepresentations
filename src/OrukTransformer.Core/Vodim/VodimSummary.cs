namespace OrukTransformer.Core.Vodim;

/// <summary>
/// VODIM classification counts (V/O/D/I/M/U) plus their total. Immutable value type
/// shared by every VODIM presenter (CLI console summary, xHTML5 report, MCP tool) so
/// the roll-up is computed in exactly one place.
/// </summary>
public sealed record VodimCounts(
    int Valid, int Other, int Default, int Invalid, int Missing, int Unmapped)
{
    /// <summary>Total number of classified fields.</summary>
    public int Total => Valid + Other + Default + Invalid + Missing + Unmapped;

    /// <summary>Count for a single classification.</summary>
    public int this[VodimClassification classification] => classification switch
    {
        VodimClassification.Valid => Valid,
        VodimClassification.Other => Other,
        VodimClassification.Default => Default,
        VodimClassification.Invalid => Invalid,
        VodimClassification.Missing => Missing,
        VodimClassification.Unmapped => Unmapped,
        _ => 0
    };

    /// <summary>
    /// Integer percentage (0–100) of fields with the given classification,
    /// or <c>0</c> when there are no fields. Matches the truncating integer
    /// arithmetic used by the console and HTML presenters.
    /// </summary>
    public int PercentOf(VodimClassification classification) =>
        Total == 0 ? 0 : this[classification] * 100 / Total;

    /// <summary>Sums the per-service counts across every report.</summary>
    public static VodimCounts FromReports(IEnumerable<TransformationReport> reports)
    {
        int v = 0, o = 0, d = 0, i = 0, m = 0, u = 0;
        foreach (var r in reports)
        {
            v += r.ValidCount;
            o += r.OtherCount;
            d += r.DefaultCount;
            i += r.InvalidCount;
            m += r.MissingCount;
            u += r.UnmappedCount;
        }
        return new VodimCounts(v, o, d, i, m, u);
    }

    /// <summary>Counts individual field-mapping records by classification.</summary>
    public static VodimCounts FromRecords(IEnumerable<FieldMappingRecord> records)
    {
        int v = 0, o = 0, d = 0, i = 0, m = 0, u = 0;
        foreach (var rec in records)
        {
            switch (rec.Classification)
            {
                case VodimClassification.Valid: v++; break;
                case VodimClassification.Other: o++; break;
                case VodimClassification.Default: d++; break;
                case VodimClassification.Invalid: i++; break;
                case VodimClassification.Missing: m++; break;
                case VodimClassification.Unmapped: u++; break;
            }
        }
        return new VodimCounts(v, o, d, i, m, u);
    }
}

/// <summary>
/// A de-duplicated issue note for a field path: the note text, how many times it
/// occurred, and the VODIM classification it was recorded under.
/// </summary>
public sealed record VodimNote(string Text, int Count, VodimClassification Classification);

/// <summary>
/// VODIM roll-up for a single ORUK source field path, aggregated across every service
/// assessed. <see cref="TargetPath"/> is the most common non-<c>"—"</c> Schema.org target
/// mapped from this source field (or <c>"—"</c> when the field is unmapped).
/// </summary>
public sealed record VodimFieldSummary(
    string SourcePath,
    string TargetPath,
    VodimCounts Counts,
    IReadOnlyList<VodimNote> Notes);

/// <summary>
/// The complete VODIM data-quality roll-up for a batch of transformations: the overall
/// counts plus a per-source-field breakdown. This is the single shared aggregation
/// consumed by the CLI console summary, the xHTML5 report builder, and the MCP
/// data-quality tool — no presenter re-implements the counting.
/// </summary>
public sealed record VodimSummary(
    int ServiceCount,
    VodimCounts Overall,
    IReadOnlyList<VodimFieldSummary> Fields)
{
    /// <summary>Path shown when an ORUK field has no Schema.org target.</summary>
    public const string NoTarget = "—";

    /// <summary>
    /// Aggregates the given transformation reports into overall totals and a
    /// per-source-field breakdown (grouped case-insensitively by source path,
    /// ordered by path).
    /// </summary>
    public static VodimSummary Aggregate(IReadOnlyList<TransformationReport> reports)
    {
        var overall = VodimCounts.FromReports(reports);

        var fields = reports
            .SelectMany(r => r.Records)
            .GroupBy(r => r.SourcePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(BuildField)
            .ToList();

        return new VodimSummary(reports.Count, overall, fields);
    }

    private static VodimFieldSummary BuildField(IGrouping<string, FieldMappingRecord> group)
    {
        var records = group.ToList();

        // Use the most common non-placeholder target path for this source field.
        var targetPath = records
            .Select(r => r.TargetPath)
            .Where(p => p != NoTarget)
            .GroupBy(p => p)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? NoTarget;

        // De-duplicate issue notes, keeping the classification they were recorded under.
        var notes = records
            .Where(r => r.Note is not null)
            .GroupBy(r => r.Note!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => new VodimNote(
                g.Key,
                g.Count(),
                records.First(r =>
                    string.Equals(r.Note, g.Key, StringComparison.OrdinalIgnoreCase)).Classification))
            .ToList();

        return new VodimFieldSummary(
            group.Key, targetPath, VodimCounts.FromRecords(records), notes);
    }
}
