using MCPal.Server.OAuth;

namespace MCPal.Server.Tests.OAuth;

[TestFixture]
internal sealed class RedirectUriPolicyTests
{
    [TestCase("https://claude.ai/api/mcp/auth_callback")]
    [TestCase("http://localhost/callback")]
    [TestCase("http://localhost:54321/callback")]
    [TestCase("http://127.0.0.1:8123/oauth/cb")]
    public void IsAllowedForRegistration_AllowedUri_ReturnsTrue(string uri)
    {
        RedirectUriPolicy.IsAllowedForRegistration(uri).Should().BeTrue();
    }

    [TestCase("https://evil.example/cb")]
    [TestCase("https://claude.ai/other")]
    [TestCase("http://claude.ai/api/mcp/auth_callback")]
    [TestCase("http://evil.example:80/cb")]
    [TestCase("http://localhost.evil.example/cb")]
    [TestCase("javascript:alert(1)")]
    [TestCase("/relative")]
    [TestCase("https://claude.ai/api/mcp/auth_callback#frag")]
    [TestCase("")]
    public void IsAllowedForRegistration_ForbiddenUri_ReturnsFalse(string uri)
    {
        RedirectUriPolicy.IsAllowedForRegistration(uri).Should().BeFalse();
    }

    [Test]
    public void Matches_LoopbackWithDifferentPort_ReturnsTrue()
    {
        RedirectUriPolicy.Matches(["http://localhost:1111/cb"], "http://localhost:2222/cb").Should().BeTrue();
    }

    [Test]
    public void Matches_LoopbackWithDifferentPath_ReturnsFalse()
    {
        RedirectUriPolicy.Matches(["http://localhost:1111/cb"], "http://localhost:1111/other").Should().BeFalse();
    }

    [Test]
    public void Matches_LoopbackHostSwitchedBetweenLocalhostAnd127_ReturnsFalse()
    {
        RedirectUriPolicy.Matches(["http://localhost:1111/cb"], "http://127.0.0.1:1111/cb").Should().BeFalse();
    }

    [Test]
    public void Matches_HttpsUri_RequiresExactMatch()
    {
        RedirectUriPolicy.Matches(["https://claude.ai/api/mcp/auth_callback"], "https://claude.ai/api/mcp/auth_callback").Should().BeTrue();
        RedirectUriPolicy.Matches(["https://claude.ai/api/mcp/auth_callback"], "https://claude.ai/api/mcp/auth_callback?x=1").Should().BeFalse();
    }
}
