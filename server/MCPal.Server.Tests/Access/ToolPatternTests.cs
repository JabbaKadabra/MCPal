using MCPal.Contracts;

namespace MCPal.Server.Tests.Access;

[TestFixture]
internal sealed class ToolPatternTests
{
    [TestCase("*", "anything", true)]
    [TestCase("list_*", "list_tables", true)]
    [TestCase("list_*", "get_tables", false)]
    [TestCase("get_?", "get_a", true)]
    [TestCase("get_?", "get_ab", false)]
    [TestCase("a.b", "a.b", true)]
    [TestCase("a.b", "axb", false)]
    public void Matches_Patterns_FollowGlobRules(string pattern, string name, bool expected)
    {
        ToolPattern.Matches(pattern, name, ignoreCase: false).Should().Be(expected);
    }

    [Test]
    public void Matches_IgnoreCaseFalse_IsCaseSensitive()
    {
        ToolPattern.Matches("Query", "query", ignoreCase: false).Should().BeFalse();
        ToolPattern.Matches("Query", "Query", ignoreCase: false).Should().BeTrue();
    }

    [Test]
    public void Matches_IgnoreCaseTrue_IgnoresCase()
    {
        ToolPattern.Matches("HR*", "hr_portal", ignoreCase: true).Should().BeTrue();
    }

    [TestCase("*", true)]
    [TestCase("hr__*", true)]
    [TestCase("get-page_1.x?", true)]
    [TestCase("", false)]
    [TestCase("a b", false)]
    [TestCase("a/b", false)]
    [TestCase("[abc]", false)]
    [TestCase("a\\b", false)]
    public void IsValid_Pattern_AcceptsOnlyToolNameCharactersAndWildcards(string pattern, bool expected)
    {
        ToolPattern.IsValid(pattern).Should().Be(expected);
    }

    [Test]
    public void IsValid_TooLongPattern_IsRejected()
    {
        ToolPattern.IsValid(new string('a', 129)).Should().BeFalse();
    }

    [Test]
    public void IsValid_Null_IsRejected()
    {
        ToolPattern.IsValid(null).Should().BeFalse();
    }
}
