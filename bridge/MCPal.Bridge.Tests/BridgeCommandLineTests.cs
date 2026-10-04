namespace MCPal.Bridge.Tests;

[TestFixture]
internal sealed class BridgeCommandLineTests
{
    [Test]
    public void Parse_NoArguments_DefaultsToRun()
    {
        var parsed = BridgeCommandLine.Parse([]);

        parsed.Verb.Should().Be("run");
        parsed.HostArgs.Should().BeEmpty();
    }

    [Test]
    public void Parse_OnlyConfigOption_DefaultsToRunAndKeepsOption()
    {
        var parsed = BridgeCommandLine.Parse(["--config", "/etc/mcpal/mcpal.json"]);

        parsed.Verb.Should().Be("run");
        parsed.HostArgs.Should().Equal("--config", "/etc/mcpal/mcpal.json");
    }

    [Test]
    public void Parse_VerbFollowedByOptionWithSameValue_KeepsOptionValue()
    {
        var parsed = BridgeCommandLine.Parse(["check", "--config", "check"]);

        parsed.Verb.Should().Be("check");
        parsed.HostArgs.Should().Equal("--config", "check");
    }

    [Test]
    public void Parse_UnknownFirstWord_ReturnsItAsVerb()
    {
        var parsed = BridgeCommandLine.Parse(["start", "--config", "x.json"]);

        parsed.Verb.Should().Be("start");
        parsed.IsKnownVerb.Should().BeFalse();
    }

    [Test]
    public void Parse_StatusVerb_IsKnown()
    {
        var parsed = BridgeCommandLine.Parse(["status", "--config", "x.json"]);

        parsed.Verb.Should().Be("status");
        parsed.IsKnownVerb.Should().BeTrue();
    }

    [Test]
    public void Parse_EnrollVerb_IsKnownAndKeepsItsOptions()
    {
        var parsed = BridgeCommandLine.Parse(["enroll", "--url", "https://mcpal.example.com", "--code", "mcpale_x"]);

        parsed.IsKnownVerb.Should().BeTrue();
        parsed.Verb.Should().Be("enroll");
        parsed.HostArgs.Should().Equal("--url", "https://mcpal.example.com", "--code", "mcpale_x");
    }
}
