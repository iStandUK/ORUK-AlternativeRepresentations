using OrukModels.SchemaOrg;

namespace OrukTransformer.Core.Mapping;

/// <summary>
/// Merges the Schema.org JSON-LD documents produced by multiple
/// <see cref="TransformationResult"/> objects into a single consolidated document.
/// </summary>
public interface IJsonLdMerger
{
    /// <summary>
    /// Merges all <c>@graph</c> nodes from every result into a single
    /// <see cref="SchemaOrgDocument"/>, deduplicating by <c>@id</c>
    /// (first occurrence wins).
    /// </summary>
    SchemaOrgDocument Merge(IEnumerable<TransformationResult> results);
}
