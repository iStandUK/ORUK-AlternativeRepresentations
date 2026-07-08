using OrukTransformer.Cli.Feeds;

namespace OrukTransformer.Cli.Tests;

public class OutputFileNamingTests
{
    [Theory]
    [InlineData("Bristol", "bristol")]
    [InlineData("Care Quality Commission", "care-quality-commission")]
    [InlineData("North Lincolnshire", "north-lincolnshire")]
    [InlineData("Open Sessions (OpenActive)", "open-sessions-openactive")]
    [InlineData("Pennine   Lancashire", "pennine-lancashire")]
    [InlineData("  Trimmed  ", "trimmed")]
    [InlineData("A/B\\C:D", "a-b-c-d")]
    [InlineData("", "feed")]
    [InlineData("   ", "feed")]
    [InlineData("***", "feed")]
    public void Slugify_ProducesLowerKebabCase(string input, string expected)
    {
        Assert.Equal(expected, OutputFileNaming.Slugify(input));
    }

    [Fact]
    public void JsonLdFileName_AppendsExtension()
    {
        Assert.Equal("bristol.jsonld", OutputFileNaming.JsonLdFileName("bristol"));
    }

    [Fact]
    public void DataQualityFileName_AppendsExtension()
    {
        Assert.Equal("bristol.data-quality.html", OutputFileNaming.DataQualityFileName("bristol"));
    }
}
