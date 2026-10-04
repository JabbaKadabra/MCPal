using Autofac;
using Autofac.Extensions.DependencyInjection;
using MCPal.Bridge;
using MCPal.Bridge.Config;
using MCPal.Bridge.Diagnostics;
using MCPal.Bridge.Local;
using MCPal.Bridge.Status;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var commandLine = BridgeCommandLine.Parse(args);
if (!commandLine.IsKnownVerb)
{
    Console.Error.WriteLine($"Unknown verb '{commandLine.Verb}'. Usage: MCPal.Bridge [run|check|status] [--config <path>]");
    return 2;
}

var check = commandLine.Verb == BridgeCommandLine.Check;
var builder = Host.CreateApplicationBuilder(commandLine.HostArgs);
builder.Services.AddWindowsService(options => options.ServiceName = "MCPal Bridge");
builder.Services.AddSystemd();
// Traces are exported only when a collector is configured through the standard OTEL_EXPORTER_OTLP_ENDPOINT variable.
if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("mcpal-bridge"))
        .WithTracing(tracing => tracing.AddSource(BridgeTelemetry.ActivitySourceName).AddHttpClientInstrumentation().AddOtlpExporter());
}

builder.ConfigureContainer(new AutofacServiceProviderFactory(), container => container.RegisterModule(new BridgeModule(runTunnel: !check)));

try
{
    if (commandLine.Verb == BridgeCommandLine.Status)
    {
        return StatusCommand.Run(BridgeModule.ResolveConfigPath(builder.Configuration), BridgeConfigLoader.CurrentEnvironment(), TimeProvider.System, Console.Out);
    }

    // Fail fast with a clean message before the host starts.
    BridgeConfigLoader.Load(BridgeModule.ResolveConfigPath(builder.Configuration), BridgeConfigLoader.CurrentEnvironment(), requireMcpal: !check);
    using var host = builder.Build();
    if (!check)
    {
        await host.RunAsync();
        return 0;
    }

    var config = host.Services.GetRequiredService<BridgeConfig>();
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

static BridgeConfigException? ConfigProblem(Exception ex)
{
    for (var current = ex; current is not null; current = current.InnerException)
    {
        if (current is BridgeConfigException config)
        {
            return config;
        }
    }

    return null;
}
