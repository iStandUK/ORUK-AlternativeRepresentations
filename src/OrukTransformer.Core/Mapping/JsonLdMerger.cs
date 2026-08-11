using OrukModels.SchemaOrg;

namespace OrukTransformer.Core.Mapping;

/// <summary>
/// Merges <see cref="TransformationResult"/> documents into a single
/// <see cref="SchemaOrgDocument"/>, deduplicating graph nodes by <c>@id</c>.
/// </summary>
public sealed class JsonLdMerger : IJsonLdMerger
{
    /// <inheritdoc/>
    public SchemaOrgDocument Merge(IEnumerable<TransformationResult> results)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new List<SchemaOrgThing>();

        foreach (var result in results)
        {
            foreach (var node in result.Document.Graph)
            {
                // Nodes without an @id are always included (cannot deduplicate)
                if (node.Id is null)
                {
                    nodes.Add(node);
                }
                else if (seen.Add(node.Id))
                {
                    nodes.Add(node);
                }
                // Duplicate @id — first occurrence wins; silently skip
            }
        }

        return new SchemaOrgDocument { Graph = nodes };
    }
}
