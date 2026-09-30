using System.Text.Json;
using MCPal.Contracts;

namespace MCPal.Cloud.Tests.Tunnel;

[TestFixture]
internal sealed class ContractsSerializationTests
{
    [Test]
    public void AgentCatalog_RoundTrip_PreservesAllValues()
    {
        var catalog = new AgentCatalog(
            "hq-01",
            "1.0.0",
            ProtocolVersion.Current,
            [new ServerCatalog("kb", [new ToolDescriptor("search", "Search", "Find", "{\"type\":\"object\"}", null)])]);

        var json = JsonSerializer.Serialize(catalog);
        var copy = JsonSerializer.Deserialize<AgentCatalog>(json);

        copy.Should().NotBeNull();
        copy.AgentName.Should().Be("hq-01");
        copy.ProtocolVersion.Should().Be(ProtocolVersion.Current);
        copy.Servers.Should().ContainSingle().Which.Tools.Should().ContainSingle().Which.InputSchemaJson.Should().Be("{\"type\":\"object\"}");
    }

    [Test]
    public void RegisterResult_RoundTrip_PreservesRejections()
    {
        var result = new RegisterResult(true, [new RejectedServer("kb", "name in use")], null);

        var copy = JsonSerializer.Deserialize<RegisterResult>(JsonSerializer.Serialize(result));

        copy.Should().NotBeNull();
        copy.RejectedServers.Should().ContainSingle().Which.Reason.Should().Be("name in use");
    }

    [Test]
    public void CallToolResponse_RoundTrip_PreservesContent()
    {
        var response = new CallToolResponse(false, "[{\"type\":\"text\",\"text\":\"hi\"}]", null);

        var copy = JsonSerializer.Deserialize<CallToolResponse>(JsonSerializer.Serialize(response));

        copy.Should().Be(response);
    }
}
