using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Agent;
using MCPal.Agent.Config;
using MCPal.Agent.Local;
using MCPal.Agent.Status;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var commandLine = AgentCommandLine.Parse(args);
if (!commandLine.IsKnownVerb)
{
    Console.Error.WriteLine($"Unknown verb '{commandLine.Verb}'. Usage: MCPal.Agent [run|check|status] [--config <path>]");
    return 2;
}

var check = commandLine.Verb == AgentCommandLine.Check;
var builder = Host.CreateApplicationBuilder(commandLine.HostArgs);
builder.Services.AddWindowsService(options => options.ServiceName = "MCPal Agent");
builder.Services.AddSystemd();
// Traces are exported only when a collector is configured through the standard OTEL_EXPORTER_OTLP_ENDPOINT variable.
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("mcpal-agent"))
        .WithTracing(tracing => tracing.AddSource(AgentTelemetry.ActivitySourceName).AddHttpClientInstrumentation().AddOtlpExporter());
}

builder.ConfigureContainer(new AutofacServiceProviderFactory(), container => container.RegisterModule(new AgentModule(runTunnel: !check)));

try
{
    if (commandLine.Verb == AgentCommandLine.Status)
    {
        return StatusCommand.Run(AgentModule.ResolveConfigPath(builder.Configuration), AgentConfigLoader.CurrentEnvironment(), TimeProvider.System, Console.Out);
    }

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

        if (manager.HiddenTools.GetValueOrDefault(server.Name) is { Count: > 0 } hidden)
        {
            Console.WriteLine($"  hidden by config: {string.Join(", ", hidden)}");
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
