using MCPal.Agent.Config;

namespace MCPal.Agent.Status;

/// <summary>The <c>status</c> verb: prints the status file. Exit code 0 only when the tunnel is connected and the file is fresh.</summary>
internal static class StatusCommand
{
    /// <summary>The agent refreshes the file every 30 s; this allows a few missed refreshes.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    public static int Run(string configPath, IReadOnlyDictionary<string, string?> environment, TimeProvider timeProvider, TextWriter output)
    {
        var config = AgentConfigLoader.Load(configPath, environment, requireCloud: false);
        var path = config.StatusFile
            ?? throw new AgentConfigException("The config has no 'statusFile'. Set it to let the agent write its status.");
        var status = AgentStatusFile.Read(path);
        var age = timeProvider.GetUtcNow() - status.UpdatedAt;
        var stale = age > StaleAfter;

        output.WriteLine($"tunnel:  {status.Tunnel} ({status.CloudUrl})");
        output.WriteLine($"updated: {status.UpdatedAt.ToString("u", System.Globalization.CultureInfo.InvariantCulture)}{(stale ? $" (stale, {age.TotalMinutes:0} min ago)" : string.Empty)}");
        output.WriteLine($"registered: {(status.LastRegisteredAt is { } registered ? registered.ToString("u", System.Globalization.CultureInfo.InvariantCulture) : "never")}");
        foreach (var server in status.Servers)
        {
            output.WriteLine($"  {server.Name}: {server.State}, {server.Tools} tool(s)");
        }

        foreach (var rejected in status.Rejected)
        {
            output.WriteLine($"  rejected {rejected.Server}: {rejected.Reason}");
        }

        return status.Tunnel == "connected" && !stale ? 0 : 1;
    }
}
