using MCPal.Cloud.Tunnel;
using MCPal.Contracts;

namespace MCPal.Cloud.Tests.Tunnel;

[TestFixture]
internal sealed class ConnectionRegistryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static AgentCatalog Catalog(string agent, params (string Server, string[] Tools)[] servers) =>
        new(agent, "1.0", ProtocolVersion.Current, [.. servers.Select(s => new ServerCatalog(s.Server, [.. s.Tools.Select(t => new ToolDescriptor(t, null, "d", "{\"type\":\"object\"}", null))]))]);

    private static ConnectionRegistry NewRegistry() => new();

    [Test]
    public void Register_UnknownConnection_IsNotAccepted()
    {
        var registry = NewRegistry();

        var result = registry.Register(Guid.NewGuid(), "c1", Catalog("a", ("kb", ["search"])));

        result.Accepted.Should().BeFalse();
    }

    [Test]
    public void Register_NewServer_ExposesToolsUnderPublicName()
    {
        var registry = NewRegistry();
        var company = Guid.NewGuid();
        registry.Add(company, "c1", Guid.NewGuid(), Now, () => { });

        var result = registry.Register(company, "c1", Catalog("a", ("kb", ["search", "get"])));

        result.Accepted.Should().BeTrue();
        result.RejectedServers.Should().BeEmpty();
        registry.Tools(company).Select(t => t.PublicName).Should().Equal("kb__get", "kb__search");
    }

    [Test]
    public void Register_ServerNameHeldByOtherConnection_RejectsOnlyThatServer()
    {
        var registry = NewRegistry();
        var company = Guid.NewGuid();
        registry.Add(company, "c1", Guid.NewGuid(), Now, () => { });
        registry.Add(company, "c2", Guid.NewGuid(), Now, () => { });
        registry.Register(company, "c1", Catalog("a", ("kb", ["search"])));

        var result = registry.Register(company, "c2", Catalog("b", ("kb", ["other"]), ("wiki", ["read"])));

        result.Accepted.Should().BeTrue();
        result.RejectedServers.Should().ContainSingle().Which.ServerName.Should().Be("kb");
        registry.TryResolve(company, "kb__search", out var kb).Should().BeTrue();
        kb.Should().NotBeNull();
        kb.ConnectionId.Should().Be("c1");
        registry.TryResolve(company, "wiki__read", out var wiki).Should().BeTrue();
        wiki.Should().NotBeNull();
        wiki.ConnectionId.Should().Be("c2");
    }

    [Test]
    public void Register_SameConnectionAgain_ReplacesItsOwnServers()
    {
        var registry = NewRegistry();
        var company = Guid.NewGuid();
        registry.Add(company, "c1", Guid.NewGuid(), Now, () => { });
        registry.Register(company, "c1", Catalog("a", ("kb", ["search"]), ("old", ["x"])));

        var result = registry.Register(company, "c1", Catalog("a", ("kb", ["search", "get"])));

        result.RejectedServers.Should().BeEmpty();
        registry.Tools(company).Select(t => t.PublicName).Should().Equal("kb__get", "kb__search");
    }

    [Test]
    public void Remove_Connection_DropsItsToolsAndFreesServerNames()
    {
        var registry = NewRegistry();
        var company = Guid.NewGuid();
        registry.Add(company, "c1", Guid.NewGuid(), Now, () => { });
        registry.Add(company, "c2", Guid.NewGuid(), Now, () => { });
        registry.Register(company, "c1", Catalog("a", ("kb", ["search"])));

        registry.Remove(company, "c1");
        var result = registry.Register(company, "c2", Catalog("b", ("kb", ["search"])));

        result.RejectedServers.Should().BeEmpty();
        registry.Connections(company).Should().ContainSingle().Which.ConnectionId.Should().Be("c2");
    }

    [Test]
    public void Tools_OtherCompany_SeesNothing()
    {
        var registry = NewRegistry();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        registry.Add(owner, "c1", Guid.NewGuid(), Now, () => { });
        registry.Register(owner, "c1", Catalog("a", ("kb", ["search"])));

        registry.Tools(other).Should().BeEmpty();
        registry.TryResolve(other, "kb__search", out _).Should().BeFalse();
        registry.Connections(other).Should().BeEmpty();
    }

    [Test]
    public void Register_SameServerNameInDifferentCompanies_IsAccepted()
    {
        var registry = NewRegistry();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        registry.Add(first, "c1", Guid.NewGuid(), Now, () => { });
        registry.Add(second, "c2", Guid.NewGuid(), Now, () => { });
        registry.Register(first, "c1", Catalog("a", ("kb", ["search"])));

        var result = registry.Register(second, "c2", Catalog("b", ("kb", ["search"])));

        result.RejectedServers.Should().BeEmpty();
        registry.TryResolve(second, "kb__search", out var tool).Should().BeTrue();
        tool.Should().NotBeNull();
        tool.ConnectionId.Should().Be("c2");
    }

    [Test]
    public void Register_DuplicateServerNameInsideCatalog_RejectsSecond()
    {
        var registry = NewRegistry();
        var company = Guid.NewGuid();
        registry.Add(company, "c1", Guid.NewGuid(), Now, () => { });

        var result = registry.Register(company, "c1", Catalog("a", ("kb", ["one"]), ("kb", ["two"])));

        result.RejectedServers.Should().ContainSingle().Which.ServerName.Should().Be("kb");
        registry.Tools(company).Select(t => t.PublicName).Should().Equal("kb__one");
    }

    [Test]
    public void ConnectionsForKey_ReturnsOnlyConnectionsOfThatKey()
    {
        var registry = NewRegistry();
        var company = Guid.NewGuid();
        var key = Guid.NewGuid();
        registry.Add(company, "c1", key, Now, () => { });
        registry.Add(company, "c2", Guid.NewGuid(), Now, () => { });

        registry.ConnectionsForKey(company, key).Should().ContainSingle().Which.ConnectionId.Should().Be("c1");
    }

    [Test]
    public async Task Register_ConcurrentConnections_NeverLosesUpdates()
    {
        var registry = NewRegistry();
        var company = Guid.NewGuid();
        var ids = Enumerable.Range(0, 50).Select(i => $"c{i}").ToArray();
        foreach (var id in ids)
        {
            registry.Add(company, id, Guid.NewGuid(), Now, () => { });
        }

        await Task.WhenAll(ids.Select(id => Task.Run(() => registry.Register(company, id, Catalog(id, ($"srv{id}", ["t"]))))));

        registry.Tools(company).Should().HaveCount(50);
    }
}
