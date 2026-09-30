using System.Text.Json;
using MCPal.Contracts;

namespace MCPal.Server.Tests.Tunnel;

[TestFixture]
internal sealed class ContractsSerializationTests
{
    [Test]
    public void BridgeCatalog_RoundTrip_PreservesAllValues()
    {
        var catalog = new BridgeCatalog(
            "hq-01",
            "1.0.0",
            ProtocolVersion.Current,
            [new ServerCatalog("kb", [new ToolDescriptor("search", "Search", "Find", "{\"type\":\"object\"}", null)])]);

        var json = JsonSerializer.Serialize(catalog);
        var copy = JsonSerializer.Deserialize<BridgeCatalog>(json);

        copy.Should().NotBeNull();
        copy.BridgeName.Should().Be("hq-01");
        copy.ProtocolVersion.Should().Be(ProtocolVersion.Current);
        copy.Servers.Should().ContainSingle().Which.Tools.Should().ContainSingle().Which.InputSchemaJson.Should().Be("{\"type\":\"object\"}");
    }

    [Test]
    public void RegisterResult_RoundTrip_PreservesRejections()
    {
        var result = new RegisterResult(true, [new RejectedServer("kb", "name in use")], [new RejectedTool("wiki", "read", "invalid schema")], null);

        var copy = JsonSerializer.Deserialize<RegisterResult>(JsonSerializer.Serialize(result));

        copy.Should().NotBeNull();
        copy.RejectedServers.Should().ContainSingle().Which.Reason.Should().Be("name in use");
        copy.RejectedTools.Should().ContainSingle().Which.Should().Be(new RejectedTool("wiki", "read", "invalid schema"));
    }

    [Test]
    public void CallToolRequest_WithUserContext_RoundTripsAllMembers()
    {
        var companyId = Guid.NewGuid();
        var request = new CallToolRequest(
            "r1", "hr", "salaries", "{}", "00-abc-def-01",
            new UserContext("jwt.token.here", "user-1", "anna@acme.example", "Anna", ["Everyone", "hr"], companyId, "acme"));

        var copy = JsonSerializer.Deserialize<CallToolRequest>(JsonSerializer.Serialize(request));

        copy.Should().NotBeNull();
        copy.User.Should().NotBeNull();
        copy.User.Token.Should().Be("jwt.token.here");
        copy.User.UserId.Should().Be("user-1");
        copy.User.Groups.Should().Equal("Everyone", "hr");
        copy.User.CompanyId.Should().Be(companyId);
        copy.User.Company.Should().Be("acme");
        copy.TraceParent.Should().Be("00-abc-def-01");
    }

    [Test]
    public void CallToolRequest_FromProtocol11Sender_DeserializesWithoutUser()
    {
        // What a 1.0 or 1.1 server sends: no "user" member at all.
        const string json = """{"requestId":"r1","serverName":"hr","toolName":"salaries","argumentsJson":"{}","traceParent":null}""";

        var request = JsonSerializer.Deserialize<CallToolRequest>(json, JsonSerializerOptions.Web);

        request.Should().NotBeNull();
        request.User.Should().BeNull();
        request.ToolName.Should().Be("salaries");
    }

    [Test]
    public void CallToolRequest_WithoutUser_SerializesUserAsNull()
    {
        var request = new CallToolRequest("r1", "hr", "salaries", "{}");

        var copy = JsonSerializer.Deserialize<CallToolRequest>(JsonSerializer.Serialize(request));

        copy.Should().NotBeNull();
        copy.User.Should().BeNull();
    }

    [Test]
    public void CallToolResponse_RoundTrip_PreservesContent()
    {
        var response = new CallToolResponse(false, "[{\"type\":\"text\",\"text\":\"hi\"}]", null);

        var copy = JsonSerializer.Deserialize<CallToolResponse>(JsonSerializer.Serialize(response));

        copy.Should().Be(response);
    }

    [TestCase("1.2", 1, 2, true)]
    [TestCase("1.1", 1, 2, false)]
    [TestCase("1.1", 1, 1, true)]
    [TestCase("1.2", 1, 1, true)]
    [TestCase("2.0", 1, 1, true)]
    [TestCase("1.0", 1, 1, false)]
    [TestCase("0.9", 1, 1, false)]
    [TestCase("1", 1, 1, false)]
    [TestCase("garbage", 1, 1, false)]
    [TestCase(null, 1, 1, false)]
    public void ProtocolVersion_AtLeast_ComparesMajorThenMinor(string? version, int major, int minor, bool expected)
    {
        ProtocolVersion.AtLeast(version, major, minor).Should().Be(expected);
    }

    [Test]
    public void ToolDescriptor_WithOutputSchema_RoundTrips()
    {
        var descriptor = new ToolDescriptor("weather", null, null, "{\"type\":\"object\"}", null, "{\"type\":\"object\",\"properties\":{\"temp\":{\"type\":\"number\"}}}");

        var copy = JsonSerializer.Deserialize<ToolDescriptor>(JsonSerializer.Serialize(descriptor));

        copy.Should().Be(descriptor);
    }

    [Test]
    public void ToolDescriptor_JsonFromProtocol10Bridge_HasNoOutputSchema()
    {
        const string json = """{"Name":"echo","Title":null,"Description":"d","InputSchemaJson":"{}","AnnotationsJson":null}""";

        var copy = JsonSerializer.Deserialize<ToolDescriptor>(json);

        copy.Should().NotBeNull();
        copy.OutputSchemaJson.Should().BeNull();
    }

    [Test]
    public void CallToolResponse_WithStructuredContentAndMeta_RoundTrips()
    {
        var response = new CallToolResponse(false, "[]", null, "{\"temp\":21.5}", "{\"trace\":\"abc\"}");

        var copy = JsonSerializer.Deserialize<CallToolResponse>(JsonSerializer.Serialize(response));

        copy.Should().Be(response);
    }

    [Test]
    public void CallToolResponse_JsonFromProtocol10Bridge_HasNoStructuredContentOrMeta()
    {
        const string json = """{"IsError":false,"ContentJson":"[]","ErrorMessage":null}""";

        var copy = JsonSerializer.Deserialize<CallToolResponse>(json);

        copy.Should().NotBeNull();
        copy.StructuredContentJson.Should().BeNull();
        copy.MetaJson.Should().BeNull();
    }

    [Test]
    public void RegisterResult_WithCode_RoundTrips()
    {
        var result = new RegisterResult(false, [], [], "Unsupported protocol version '9.0'.", RegisterCodes.UnsupportedProtocol);

        var copy = JsonSerializer.Deserialize<RegisterResult>(JsonSerializer.Serialize(result));

        copy.Should().NotBeNull();
        copy.Code.Should().Be("unsupported_protocol");
        copy.Accepted.Should().BeFalse();
        copy.Message.Should().Be(result.Message);
    }

    [Test]
    public void RegisterResult_JsonFromServer10_HasNoCode()
    {
        const string json = """{"Accepted":true,"RejectedServers":[],"RejectedTools":[],"Message":null}""";

        var copy = JsonSerializer.Deserialize<RegisterResult>(json);

        copy.Should().NotBeNull();
        copy.Code.Should().BeNull();
    }
}
