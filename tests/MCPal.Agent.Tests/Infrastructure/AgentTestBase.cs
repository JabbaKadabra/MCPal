using Autofac;
using MCPal.Agent.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCPal.Agent.Tests.Infrastructure;

/// <summary>Base harness: a fresh container from the production module per call, with the given config.</summary>
internal abstract class AgentTestBase
{
    private readonly List<IContainer> containers = [];

    protected static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    protected static LocalServerConfig TestServer(params string[] extraArgs) => new(
        "dotnet",
        [Path.Combine(AppContext.BaseDirectory, "MCPal.TestMcpServer.dll"), .. extraArgs],
        new Dictionary<string, string>(),
        null,
        new Dictionary<string, string>());

    protected static AgentConfig ConfigWith(Dictionary<string, LocalServerConfig> servers, int callTimeoutSeconds = 30) =>
        new(new CloudConfig("http://localhost", "mcpal_test_key", "test-agent"), servers, callTimeoutSeconds);

    protected ILifetimeScope GetServices(AgentConfig config, Action<ContainerBuilder>? configure = null)
    {
        var builder = new ContainerBuilder();
        builder.RegisterInstance<ILoggerFactory>(NullLoggerFactory.Instance);
        builder.RegisterModule(new AgentModule());
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
