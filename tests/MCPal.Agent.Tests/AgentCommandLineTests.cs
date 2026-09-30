namespace MCPal.Agent.Tests;

[TestFixture]
internal sealed class AgentCommandLineTests
{
    [Test]
    public void Parse_NoArguments_DefaultsToRun()
    {
        var parsed = AgentCommandLine.Parse([]);

        parsed.Verb.Should().Be("run");
        parsed.HostArgs.Should().BeEmpty();
    }

    [Test]
    public void Parse_OnlyConfigOption_DefaultsToRunAndKeepsOption()
    {
        var parsed = AgentCommandLine.Parse(["--config", "/etc/mcpal/mcpal.json"]);

        parsed.Verb.Should().Be("run");
        parsed.HostArgs.Should().Equal("--config", "/etc/mcpal/mcpal.json");
    }

    [Test]
    public void Parse_VerbFollowedByOptionWithSameValue_KeepsOptionValue()
    {
        var parsed = AgentCommandLine.Parse(["check", "--config", "check"]);

        parsed.Verb.Should().Be("check");
        parsed.HostArgs.Should().Equal("--config", "check");
    }

    [Test]
    public void Parse_UnknownFirstWord_ReturnsItAsVerb()
    {
        var parsed = AgentCommandLine.Parse(["start", "--config", "x.json"]);

        parsed.Verb.Should().Be("start");
        parsed.IsKnownVerb.Should().BeFalse();
    }

    [Test]
    public void Parse_StatusVerb_IsKnown()
    {
        var parsed = AgentCommandLine.Parse(["status", "--config", "x.json"]);

        parsed.Verb.Should().Be("status");
        parsed.IsKnownVerb.Should().BeTrue();
    }
}
