using MCPal.Agent.Config;

namespace MCPal.Agent.Tests.Config;

[TestFixture]
internal sealed class EnvironmentExpanderTests
{
    private static readonly IReadOnlyDictionary<string, string?> Environment = new Dictionary<string, string?>
    {
        ["TOKEN"] = "s3cret",
        ["EMPTY"] = string.Empty,
    };

    [Test]
    public void Expand_ValueWithoutVariable_IsUnchanged()
    {
        EnvironmentExpander.Expand("plain value $HOME {x}", Environment, "Server 'a'").Should().Be("plain value $HOME {x}");
    }

    [Test]
    public void Expand_SetVariable_IsReplaced()
    {
        EnvironmentExpander.Expand("Bearer ${TOKEN}!", Environment, "Server 'a'").Should().Be("Bearer s3cret!");
    }

    [Test]
    public void Expand_TwoVariables_AreBothReplaced()
    {
        EnvironmentExpander.Expand("${TOKEN}:${TOKEN}", Environment, "Server 'a'").Should().Be("s3cret:s3cret");
    }

    [Test]
    public void Expand_UnsetVariableWithDefault_UsesDefault()
    {
        EnvironmentExpander.Expand("${MISSING:-fallback}", Environment, "Server 'a'").Should().Be("fallback");
    }

    [Test]
    public void Expand_EmptyVariableWithDefault_UsesDefault()
    {
        EnvironmentExpander.Expand("${EMPTY:-fallback}", Environment, "Server 'a'").Should().Be("fallback");
    }

    [Test]
    public void Expand_SetVariableWithDefault_IgnoresDefault()
    {
        EnvironmentExpander.Expand("${TOKEN:-fallback}", Environment, "Server 'a'").Should().Be("s3cret");
    }

    [Test]
    public void Expand_EmptyDefault_GivesEmptyString()
    {
        EnvironmentExpander.Expand("a${MISSING:-}b", Environment, "Server 'a'").Should().Be("ab");
    }

    [Test]
    public void Expand_DoubleDollar_GivesLiteralDollar()
    {
        EnvironmentExpander.Expand("cost $$5 and $${TOKEN}", Environment, "Server 'a'").Should().Be("cost $5 and ${TOKEN}");
    }

    [Test]
    public void Expand_UnsetVariableWithoutDefault_ThrowsWithContextAndVariableName()
    {
        var act = () => EnvironmentExpander.Expand("Bearer ${JIRA_TOKEN}", Environment, "Server 'jira'");

        act.Should().Throw<AgentConfigException>().WithMessage("Server 'jira': environment variable 'JIRA_TOKEN' is not set.");
    }

    [Test]
    public void Expand_ExpandedValueContainingDollarBrace_IsNotExpandedAgain()
    {
        var environment = new Dictionary<string, string?> { ["A"] = "${B}", ["B"] = "boom" };

        EnvironmentExpander.Expand("${A}", environment, "Server 'a'").Should().Be("${B}");
    }
}
