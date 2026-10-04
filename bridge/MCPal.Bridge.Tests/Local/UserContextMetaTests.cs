using System.Text.Json.Nodes;
using MCPal.Bridge.Local;
using MCPal.Contracts;

namespace MCPal.Bridge.Tests.Local;

[TestFixture]
internal sealed class UserContextMetaTests
{
    private static readonly Guid CompanyId = Guid.Parse("7f3c6a1e-5b7d-4f0e-9a51-0c2d2f6f7a10");

    private static UserContext Anna() => new("jwt.value", "user-anna", "anna@acme.example", "Anna", ["Everyone", "hr"], CompanyId, "acme");

    [Test]
    public void Apply_User_PutsTokenAndPlainClaimsUnderTheKey()
    {
        var meta = UserContextMeta.Apply(null, Anna());

        var entry = meta?[UserContextMeta.Key]?.AsObject();
        entry.Should().NotBeNull();
        entry["token"]?.GetValue<string>().Should().Be("jwt.value");
        entry["sub"]?.GetValue<string>().Should().Be("user-anna");
        entry["email"]?.GetValue<string>().Should().Be("anna@acme.example");
        entry["name"]?.GetValue<string>().Should().Be("Anna");
        entry["groups"]?.AsArray().Select(g => g?.GetValue<string>()).Should().Equal("Everyone", "hr");
        entry["companyId"]?.GetValue<string>().Should().Be(CompanyId.ToString());
        entry["company"]?.GetValue<string>().Should().Be("acme");
    }

    [Test]
    public void Apply_SpoofedKeyAndNoUser_RemovesTheKeyAndKeepsOthers()
    {
        var incoming = new JsonObject
        {
            [UserContextMeta.Key] = new JsonObject { ["sub"] = "attacker", ["token"] = "forged" },
            ["progressToken"] = "p1",
        };

        var meta = UserContextMeta.Apply(incoming, null);

        meta.Should().NotBeNull();
        meta.ContainsKey(UserContextMeta.Key).Should().BeFalse();
        meta["progressToken"]?.GetValue<string>().Should().Be("p1");
    }

    [Test]
    public void Apply_SpoofedKeyAndUser_OverwritesTheKeyWithTheRealCaller()
    {
        var incoming = new JsonObject
        {
            [UserContextMeta.Key] = new JsonObject { ["sub"] = "attacker", ["token"] = "forged", ["extra"] = "x" },
            ["other"] = 1,
        };

        var meta = UserContextMeta.Apply(incoming, Anna());

        var entry = meta?[UserContextMeta.Key]?.AsObject();
        entry.Should().NotBeNull();
        entry["sub"]?.GetValue<string>().Should().Be("user-anna");
        entry["token"]?.GetValue<string>().Should().Be("jwt.value");
        entry.ContainsKey("extra").Should().BeFalse();
        meta?["other"]?.GetValue<int>().Should().Be(1);
    }

    [Test]
    public void Apply_NothingToSend_ReturnsNull()
    {
        UserContextMeta.Apply(null, null).Should().BeNull();
        UserContextMeta.Apply(new JsonObject { [UserContextMeta.Key] = "forged" }, null).Should().BeNull();
    }

    [Test]
    public void Apply_UserWithoutEmailOrName_SendsNulls()
    {
        var meta = UserContextMeta.Apply(null, new UserContext("t", "u", null, null, [], CompanyId, "acme"));

        var entry = meta?[UserContextMeta.Key]?.AsObject();
        entry.Should().NotBeNull();
        entry["email"].Should().BeNull();
        entry["name"].Should().BeNull();
        entry["groups"]?.AsArray().Should().BeEmpty();
    }
}
