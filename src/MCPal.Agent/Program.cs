using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Agent;
using MCPal.Agent.Config;
using MCPal.Agent.Local;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Verbs: "run" (default) connects to the cloud; "check" validates the config, starts the local servers and prints their tools.
var verb = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "run";
var hostArgs = args.Where(a => a != verb).ToArray();
if (verb is not ("run" or "check"))
{
    Console.Error.WriteLine($"Unknown verb '{verb}'. Use 'run' or 'check' (options: --config <path>).");
    return 2;
}

var check = verb == "check";
var builder = Host.CreateApplicationBuilder(hostArgs);
builder.Services.AddWindowsService(options => options.ServiceName = "MCPal Agent");
builder.Services.AddSystemd();
builder.ConfigureContainer(new AutofacServiceProviderFactory(), container => container.RegisterModule(new AgentModule(runTunnel: !check)));

try
{
    // Fail fast with a clean message before the host starts.
    AgentConfigLoader.Load(AgentModule.ResolveConfigPath(builder.Configuration), AgentConfigLoader.CurrentEnvironment(), requireCloud: !check);
    using var host = builder.Build();
    if (!check)
    {
        await host.RunAsync();
        return 0;
    }

    var config = host.Services.GetRequiredService<AgentConfig>();
    var manager = host.Services.GetRequiredService<ILocalServerManager>();
    var servers = await manager.ListServersAsync(CancellationToken.None);
    foreach (var server in servers)
    {
        Console.WriteLine($"{server.Name}: {server.Tools.Count} tool(s)");
        foreach (var tool in server.Tools)
        {
            Console.WriteLine($"  {tool.Name}{(tool.Description is null ? string.Empty : " - " + tool.Description)}");
        }
    }

    var failed = config.McpServers.Keys.Except(servers.Select(s => s.Name)).ToList();
    foreach (var name in failed)
    {
        Console.Error.WriteLine($"{name}: FAILED to start (see log)");
    }

    return failed.Count == 0 ? 0 : 1;
}
catch (Exception ex) when (ConfigProblem(ex) is { } problem)
{
    Console.Error.WriteLine($"Configuration error: {problem.Message}");
    return 2;
}

static AgentConfigException? ConfigProblem(Exception ex)
{
    for (var current = ex; current is not null; current = current.InnerException)
    {
        if (current is AgentConfigException config)
        {
            return config;
        }
    }

    return null;
}
