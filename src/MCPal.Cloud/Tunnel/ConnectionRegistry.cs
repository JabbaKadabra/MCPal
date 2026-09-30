using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using MCPal.Contracts;
using ModelContextProtocol.Protocol;

namespace MCPal.Cloud.Tunnel;

/// <param name="Listing">The MCP tool as <c>tools/list</c> returns it, built and validated at registration.</param>
internal sealed record RegisteredTool(string PublicName, string ServerName, ToolDescriptor Descriptor, Tool Listing, string ConnectionId);

internal sealed record RegisteredServer(string Name, IReadOnlyList<RegisteredTool> Tools);

internal sealed record ConnectionInfo(
    Guid CompanyId,
    string ConnectionId,
    Guid ApiKeyId,
    DateTimeOffset ConnectedAt,
    Action Abort,
    string AgentName,
    IReadOnlyList<RegisteredServer> Servers,
    IReadOnlyList<RejectedServer> RejectedServers,
    IReadOnlyList<RejectedTool> RejectedTools);

/// <summary>
/// In-memory view of live agent tunnels, keyed by company first. Every lookup starts from the caller's company.
/// Immutable snapshot updated by compare-and-swap, so no locks are needed.
/// </summary>
internal sealed class ConnectionRegistry
{
    private ImmutableDictionary<Guid, ImmutableDictionary<string, ConnectionInfo>> companies =
        ImmutableDictionary<Guid, ImmutableDictionary<string, ConnectionInfo>>.Empty;

    public void Add(Guid companyId, string connectionId, Guid apiKeyId, DateTimeOffset connectedAt, Action abort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(abort);

        var info = new ConnectionInfo(companyId, connectionId, apiKeyId, connectedAt, abort, string.Empty, [], [], []);
        Update(current => (WithConnection(current, companyId, info), true));
    }

    public RegisterResult Register(Guid companyId, string connectionId, AgentCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return Update(current =>
        {
            if (!current.TryGetValue(companyId, out var connections) || !connections.TryGetValue(connectionId, out var existing))
            {
                return (current, new RegisterResult(false, [], [], "Connection is not known."));
            }

            var takenByOthers = connections
                .Where(pair => pair.Key != connectionId)
                .SelectMany(pair => pair.Value.Servers.Select(s => s.Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var publicNameOwners = connections
                .Where(pair => pair.Key != connectionId)
                .SelectMany(pair => pair.Value.Servers.SelectMany(s => s.Tools))
                .ToDictionary(t => t.PublicName, t => (t.ServerName, ToolName: t.Descriptor.Name), StringComparer.Ordinal);

            var accepted = new List<RegisteredServer>();
            var rejectedServers = new List<RejectedServer>();
            var rejectedTools = new List<RejectedTool>();
            var ownNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var server in catalog.Servers)
            {
                if (takenByOthers.Contains(server.Name))
                {
                    rejectedServers.Add(new RejectedServer(server.Name, "Server name is already registered by another agent connection of this company."));
                    continue;
                }

                if (!ownNames.Add(server.Name))
                {
                    rejectedServers.Add(new RejectedServer(server.Name, "Server name appears more than once in this agent."));
                    continue;
                }

                var tools = new List<RegisteredTool>();
                foreach (var tool in server.Tools)
                {
                    var publicName = ToolNaming.Public(server.Name, tool.Name);
                    if (publicNameOwners.TryGetValue(publicName, out var owner))
                    {
                        rejectedTools.Add(new RejectedTool(
                            server.Name,
                            tool.Name,
                            $"Public tool name '{publicName}' is already used by tool '{owner.ToolName}' of server '{owner.ServerName}'. Rename the server or the tool."));
                        continue;
                    }

                    if (!ToolListing.TryCreate(publicName, server.Name, tool, out var listing, out var error))
                    {
                        rejectedTools.Add(new RejectedTool(server.Name, tool.Name, error));
                        continue;
                    }

                    publicNameOwners.Add(publicName, (server.Name, tool.Name));
                    tools.Add(new RegisteredTool(publicName, server.Name, tool, listing, connectionId));
                }

                accepted.Add(new RegisteredServer(server.Name, tools));
            }

            var updated = existing with { AgentName = catalog.AgentName, Servers = accepted, RejectedServers = rejectedServers, RejectedTools = rejectedTools };
            return (WithConnection(current, companyId, updated), new RegisterResult(true, rejectedServers, rejectedTools, null));
        });
    }

    public void Remove(Guid companyId, string connectionId)
    {
        Update(current =>
        {
            if (!current.TryGetValue(companyId, out var connections) || !connections.ContainsKey(connectionId))
            {
                return (current, false);
            }

            var remaining = connections.Remove(connectionId);
            return (remaining.IsEmpty ? current.Remove(companyId) : current.SetItem(companyId, remaining), true);
        });
    }

    public IReadOnlyList<RegisteredTool> Tools(Guid companyId)
    {
        return Connections(companyId)
            .SelectMany(c => c.Servers.SelectMany(s => s.Tools))
            .OrderBy(t => t.PublicName, StringComparer.Ordinal)
            .ToList();
    }

    public bool TryResolve(Guid companyId, string publicName, [NotNullWhen(true)] out RegisteredTool? tool)
    {
        tool = Connections(companyId)
            .SelectMany(c => c.Servers.SelectMany(s => s.Tools))
            .FirstOrDefault(t => t.PublicName == publicName);
        return tool is not null;
    }

    public IReadOnlyList<ConnectionInfo> Connections(Guid companyId)
    {
        return companies.TryGetValue(companyId, out var connections)
            ? [.. connections.Values.OrderBy(c => c.ConnectedAt).ThenBy(c => c.ConnectionId, StringComparer.Ordinal)]
            : [];
    }

    /// <summary>Every live connection of every company. Only for housekeeping, never to answer a tenant's request.</summary>
    public IReadOnlyList<ConnectionInfo> AllConnections()
    {
        return [.. companies.Values.SelectMany(connections => connections.Values)];
    }

    public IReadOnlyList<ConnectionInfo> ConnectionsForKey(Guid companyId, Guid apiKeyId)
    {
        return [.. Connections(companyId).Where(c => c.ApiKeyId == apiKeyId)];
    }

    private static ImmutableDictionary<Guid, ImmutableDictionary<string, ConnectionInfo>> WithConnection(
        ImmutableDictionary<Guid, ImmutableDictionary<string, ConnectionInfo>> current, Guid companyId, ConnectionInfo info)
    {
        var connections = current.TryGetValue(companyId, out var existing) ? existing : ImmutableDictionary<string, ConnectionInfo>.Empty;
        return current.SetItem(companyId, connections.SetItem(info.ConnectionId, info));
    }

    private T Update<T>(Func<ImmutableDictionary<Guid, ImmutableDictionary<string, ConnectionInfo>>, (ImmutableDictionary<Guid, ImmutableDictionary<string, ConnectionInfo>> Next, T Result)> change)
    {
        while (true)
        {
            var current = companies;
            var (next, result) = change(current);
            if (ReferenceEquals(Interlocked.CompareExchange(ref companies, next, current), current))
            {
                return result;
            }
        }
    }
}
