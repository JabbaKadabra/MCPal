using Autofac;
using MCPal.Bridge.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCPal.Bridge.Tests.Infrastructure;

/// <summary>Base harness: a fresh container from the production module per call, with the given config.</summary>
internal abstract class BridgeTestBase
{
    private readonly List<IContainer> containers = [];

    protected static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    protected static LocalServerConfig TestServer(params string[] extraArgs) => new(
        "dotnet",
        [Path.Combine(AppContext.BaseDirectory, "MCPal.TestMcpServer.dll"), .. extraArgs],
        new Dictionary<string, string>(),
        null,
        new Dictionary<string, string>());

    protected static BridgeConfig ConfigWith(Dictionary<string, LocalServerConfig> servers, int callTimeoutSeconds = 30) =>
        new(new McpalConfig("http://localhost", "mcpal_test_key", "test-bridge"), servers, callTimeoutSeconds);

    protected ILifetimeScope GetServices(BridgeConfig config, Action<ContainerBuilder>? configure = null)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance<ILoggerFactory>(NullLoggerFactory.Instance);
        builder.RegisterModule(new BridgeModule());
        builder.RegisterInstance(config).AsSelf();
        configure?.Invoke(builder);
        var container = builder.Build();
        containers.Add(container);
        return container.BeginLifetimeScope();
    }

    [TearDown]
    public async Task DisposeContainers()
    {
        foreach (var container in containers)
        {
            await container.DisposeAsync();
        }

        containers.Clear();
    }
}
