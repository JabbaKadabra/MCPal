using MCPal.Bridge.Local;

namespace MCPal.Bridge.Tests.Local;

[TestFixture]
internal sealed class ToolFilterTests
{
    [Test]
    public void IsExposed_NoLists_ExposesEverything()
    {
        var filter = new ToolFilter([], []);

        filter.IsExposed("anything").Should().BeTrue();
    }

    [Test]
    public void IsExposed_IncludeOnly_ExposesOnlyMatches()
    {
        var filter = new ToolFilter(["query", "list_*"], []);

        filter.IsExposed("query").Should().BeTrue();
        filter.IsExposed("list_tables").Should().BeTrue();
        filter.IsExposed("execute").Should().BeFalse();
    }

    [Test]
    public void IsExposed_ExcludeOnly_HidesMatches()
    {
        var filter = new ToolFilter([], ["drop_*"]);

        filter.IsExposed("drop_table").Should().BeFalse();
        filter.IsExposed("query").Should().BeTrue();
    }

    [Test]
    public void IsExposed_IncludeAndExclude_ExcludeWins()
    {
        var filter = new ToolFilter(["db_*"], ["db_drop*"]);

        filter.IsExposed("db_query").Should().BeTrue();
        filter.IsExposed("db_drop_all").Should().BeFalse();
        filter.IsExposed("other").Should().BeFalse();
    }

    [Test]
    public void IsExposed_QuestionMark_MatchesExactlyOneCharacter()
    {
        var filter = new ToolFilter(["get_?"], []);

        filter.IsExposed("get_a").Should().BeTrue();
        filter.IsExposed("get_").Should().BeFalse();
        filter.IsExposed("get_ab").Should().BeFalse();
    }

    [Test]
    public void IsExposed_Patterns_AreCaseSensitive()
    {
        var filter = new ToolFilter(["Query"], []);

        filter.IsExposed("Query").Should().BeTrue();
        filter.IsExposed("query").Should().BeFalse();
    }

    [Test]
    public void IsExposed_PatternWithRegexCharacters_IsLiteral()
    {
        var filter = new ToolFilter(["a.b"], []);

        filter.IsExposed("a.b").Should().BeTrue();
        filter.IsExposed("axb").Should().BeFalse();
    }
}
