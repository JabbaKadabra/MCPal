using System.Text.RegularExpressions;
using MCPal.Server.Tunnel;

namespace MCPal.Server.Tests.Tunnel;

[TestFixture]
internal sealed partial class ToolNamingTests
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex ValidName();

    [Test]
    public void Public_SimpleNames_JoinsWithDoubleUnderscore()
    {
        ToolNaming.Public("kb", "search").Should().Be("kb__search");
    }

    [TestCase("my server", "do.it", "my_server__do_it")]
    [TestCase("wiki/v2", "get:page", "wiki_v2__get_page")]
    [TestCase("kb-1", "a_b", "kb-1__a_b")]
    public void Public_InvalidCharacters_AreReplacedByUnderscore(string server, string tool, string expected)
    {
        ToolNaming.Public(server, tool).Should().Be(expected);
    }

    [Test]
    public void Public_UnicodeCharacters_AreReplacedAndResultIsValid()
    {
        var name = ToolNaming.Public("wissen", "größe-äöü-日本");

        name.Should().MatchRegex(ValidName());
    }

    [Test]
    public void Public_ExactlyMaxLength_IsNotTruncated()
    {
        var server = new string('s', 30);
        var tool = new string('t', 32);

        var name = ToolNaming.Public(server, tool);

        name.Should().HaveLength(64).And.Be($"{server}__{tool}");
    }

    [Test]
    public void Public_TooLong_IsTruncatedToMaxLengthWithHashSuffix()
    {
        var name = ToolNaming.Public(new string('s', 40), new string('t', 40));

        name.Should().HaveLength(64).And.MatchRegex(ValidName());
        name.Should().MatchRegex("_[0-9a-f]{6}$");
    }

    [Test]
    public void Public_TooLongWithSamePrefix_ProducesDifferentNames()
    {
        var server = new string('s', 40);
        var first = ToolNaming.Public(server, new string('t', 40) + "a");
        var second = ToolNaming.Public(server, new string('t', 40) + "b");

        first.Should().NotBe(second);
    }

    [Test]
    public void Public_SameInput_IsDeterministic()
    {
        var server = new string('s', 50);
        var tool = new string('t', 50);

        ToolNaming.Public(server, tool).Should().Be(ToolNaming.Public(server, tool));
    }

    [TestCase("", "tool")]
    [TestCase("server", " ")]
    public void Public_BlankPart_Throws(string server, string tool)
    {
        var act = () => ToolNaming.Public(server, tool);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Public_EmptyAfterSanitizing_StillProducesValidName()
    {
        ToolNaming.Public("...", "???").Should().MatchRegex(ValidName());
    }
}
