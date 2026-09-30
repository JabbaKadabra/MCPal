using System.ComponentModel;
using ModelContextProtocol.Server;

namespace MCPal.TestMcpServer;

[McpServerToolType]
internal sealed class TestTools
{
    [McpServerTool(Name = "echo"), Description("Returns the given text.")]
    public static string Echo([Description("Text to echo")] string text) => $"echo: {text}";

    [McpServerTool(Name = "add"), Description("Adds two numbers.")]
    public static int Add(int a, int b) => a + b;

    [McpServerTool(Name = "slow"), Description("Waits the given number of milliseconds.")]
    public static async Task<string> Slow(int milliseconds, CancellationToken cancellationToken)
    {
        await Task.Delay(milliseconds, cancellationToken);
        return "done";
    }

    [McpServerTool(Name = "fail"), Description("Always fails.")]
    public static string Fail() => throw new InvalidOperationException("boom from test server");

    [McpServerTool(Name = "crash"), Description("Terminates the server process.")]
    public static string Crash()
    {
        Environment.Exit(3);
        return "unreachable";
    }
}
