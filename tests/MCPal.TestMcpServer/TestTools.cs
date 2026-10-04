using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace MCPal.TestMcpServer;

[McpServerToolType]
internal sealed class TestTools
{
    [McpServerTool(Name = "echo"), Description("Returns the given text.")]
    public static string Echo([Description("Text to echo")] string text) => $"echo: {text}";

    [McpServerTool(Name = "add"), Description("Adds two numbers.")]
    public static int Add(int a, int b) => a + b;

    private static int cancellations;

    [McpServerTool(Name = "slow"), Description("Waits the given number of milliseconds.")]
    public static async Task<string> Slow(int milliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(milliseconds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref cancellations);
            throw;
        }

        return "done";
    }

    [McpServerTool(Name = "cancellations"), Description("Returns how many slow calls were cancelled since the server started.")]
    public static int Cancellations() => Volatile.Read(ref cancellations);

    [McpServerTool(Name = "weather", UseStructuredContent = true), Description("Returns a typed weather report; the SDK adds an output schema and structured content.")]
    public static WeatherReport Weather([Description("City name")] string city) => new(city, 21.5, ["sunny", "windy"]);

    [McpServerTool(Name = "with_meta"), Description("Returns a result with its own _meta.")]
    public static CallToolResult WithMeta() => new()
    {
        Content = [new TextContentBlock { Text = "meta" }],
        Meta = new System.Text.Json.Nodes.JsonObject { ["source"] = "station-7" },
    };

    [McpServerTool(Name = "whoami"), Description("Returns the caller the MCPal bridge put into _meta (eu.nordstein.mcp/user) as JSON, or 'none'.")]
    public static string Whoami(RequestContext<CallToolRequestParams> context) =>
        context.Params?.Meta?["eu.nordstein.mcp/user"]?.ToJsonString() ?? "none";

    [McpServerTool(Name = "fail"), Description("Always fails.")]
    public static string Fail() => throw new InvalidOperationException("boom from test server");

    [McpServerTool(Name = "crash"), Description("Terminates the server process.")]
    public static string Crash()
    {
        Environment.Exit(3);
        return "unreachable";
    }
}

internal sealed record WeatherReport(string City, double Temperature, string[] Tags);
