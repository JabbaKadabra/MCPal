using MCPal.Server.Tunnel;
using MCPal.Contracts;

namespace MCPal.Server.Tests.Tunnel;

[TestFixture]
internal sealed class ToolListingTests
{
    private const string ObjectSchema = """{"type":"object","properties":{"temp":{"type":"number"}},"required":["temp"]}""";

    private static ToolDescriptor Descriptor(string? outputSchema) => new("weather", null, "Weather", "{\"type\":\"object\"}", null, outputSchema);

    [Test]
    public void TryCreate_ValidOutputSchema_IsKeptOnTheListedTool()
    {
        var created = ToolListing.TryCreate("wx__weather", "wx", Descriptor(ObjectSchema), out var tool, out _);

        created.Should().BeTrue();
        tool?.OutputSchema?.GetProperty("properties").GetProperty("temp").GetProperty("type").GetString().Should().Be("number");
    }

    [Test]
    public void TryCreate_NoOutputSchema_LeavesItUnset()
    {
        ToolListing.TryCreate("wx__weather", "wx", Descriptor(null), out var tool, out _).Should().BeTrue();

        tool?.OutputSchema.Should().BeNull();
    }

    [TestCase("not json")]
    [TestCase("[1,2]")]
    [TestCase("42")]
    public void TryCreate_InvalidOutputSchema_RejectsTheToolWithReason(string schema)
    {
        var created = ToolListing.TryCreate("wx__weather", "wx", Descriptor(schema), out var tool, out var error);

        created.Should().BeFalse();
        tool.Should().BeNull();
        error.Should().Contain("output schema");
    }
}
