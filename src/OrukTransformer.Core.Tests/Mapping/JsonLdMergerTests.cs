using OrukModels.Models;
using OrukModels.SchemaOrg;
using OrukTransformer.Core.Mapping;
using OrukTransformer.Core.Vodim;

namespace OrukTransformer.Core.Tests.Mapping;

/// <summary>
/// Tests for <see cref="JsonLdMerger"/>.
/// </summary>
public class JsonLdMergerTests
{
    private static readonly JsonLdMerger _sut = new();
    private static readonly TransformationOptions _opts = new() { BaseUrl = "https://test.example.org" };
    private static readonly OrukToSchemaOrgTransformer _transformer = new();

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static TransformationResult Transform(string id, string name) =>
        _transformer.Transform(new OrukService { Id = id, Name = name }, _opts);

    private static TransformationResult WrapNodes(params SchemaOrgThing[] nodes) =>
        new()
        {
            Document = new SchemaOrgDocument { Graph = nodes },
            Report = new TransformationReport("test")
        };

    // ── Tests ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_EmptyInput_ReturnsEmptyDocument()
    {
        var doc = _sut.Merge([]);

        Assert.NotNull(doc);
        Assert.Empty(doc.Graph);
    }

    [Fact]
    public void Merge_SingleResult_IncludesAllNodes()
    {
        var result = Transform("svc-1", "Service One");

        var doc = _sut.Merge([result]);

        Assert.NotEmpty(doc.Graph);
        Assert.Single(doc.Graph.OfType<SchemaOrgGovernmentService>());
    }

    [Fact]
    public void Merge_MultipleDistinctServices_IncludesAllServices()
    {
        var r1 = Transform("svc-1", "Service One");
        var r2 = Transform("svc-2", "Service Two");

        var doc = _sut.Merge([r1, r2]);

        var services = doc.Graph.OfType<SchemaOrgGovernmentService>().ToList();
        Assert.Equal(2, services.Count);
        Assert.Contains(services, s => s.Id!.EndsWith("svc-1"));
        Assert.Contains(services, s => s.Id!.EndsWith("svc-2"));
    }

    [Fact]
    public void Merge_DuplicateServiceId_FirstOccurrenceWins()
    {
        var r1 = WrapNodes(new SchemaOrgGovernmentService
        {
            Id = "https://test.example.org/services/svc-1",
            Name = "First"
        });
        var r2 = WrapNodes(new SchemaOrgGovernmentService
        {
            Id = "https://test.example.org/services/svc-1",
            Name = "Second"
        });

        var doc = _sut.Merge([r1, r2]);

        var service = Assert.Single(doc.Graph.OfType<SchemaOrgGovernmentService>());
        Assert.Equal("First", service.Name);
    }

    [Fact]
    public void Merge_NodesWithNullId_AlwaysIncluded()
    {
        // Two nodes with no @id should both be included
        var r1 = WrapNodes(new SchemaOrgGovernmentService { Id = null, Name = "A" });
        var r2 = WrapNodes(new SchemaOrgGovernmentService { Id = null, Name = "B" });

        var doc = _sut.Merge([r1, r2]);

        Assert.Equal(2, doc.Graph.Count);
    }

    [Fact]
    public void Merge_SharedOrganization_DeduplicatedAcrossServices()
    {
        // Two services sharing the same organisation @id
        var orgId = "https://test.example.org/organisations/org-1";
        var r1 = WrapNodes(
            new SchemaOrgGovernmentService { Id = "https://test.example.org/services/svc-1", Name = "S1" },
            new SchemaOrgOrganization { Id = orgId, Name = "Shared Org" });
        var r2 = WrapNodes(
            new SchemaOrgGovernmentService { Id = "https://test.example.org/services/svc-2", Name = "S2" },
            new SchemaOrgOrganization { Id = orgId, Name = "Shared Org" });

        var doc = _sut.Merge([r1, r2]);

        var orgs = doc.Graph.OfType<SchemaOrgOrganization>().ToList();
        Assert.Single(orgs);
    }

    [Fact]
    public void Merge_ContextIsAlwaysSchemaOrg()
    {
        var result = Transform("svc-1", "Test");

        var doc = _sut.Merge([result]);

        Assert.Equal("https://schema.org", doc.Context);
    }
}
