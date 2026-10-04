using System.Collections.Concurrent;
using ModelContextProtocol.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MCPal.E2E.Tests;

/// <summary>One HTTP request the local server saw: the HTTP verb, the JSON-RPC method of its body (if any) and its Authorization header.</summary>
internal sealed record SeenRequest(string HttpMethod, string? RpcMethod, string? Authorization);

/// <summary>A real HTTP MCP server on a free loopback port that records what it receives, as a local server behind a bridge.</summary>
internal sealed class HttpLocalMcpServer : IAsyncDisposable
{
    private readonly WebApplication app;

    private HttpLocalMcpServer(WebApplication app, Uri url, ConcurrentQueue<SeenRequest> requests)
    {
        this.app = app;
        Url = url;
        Requests = requests;
    }

    public Uri Url { get; }

    public ConcurrentQueue<SeenRequest> Requests { get; }

    public static async Task<HttpLocalMcpServer> StartAsync(CancellationToken cancellationToken)
    {
        var requests = new ConcurrentQueue<SeenRequest>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<HttpLocalTools>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            string? rpcMethod = null;
            if (HttpMethods.IsPost(context.Request.Method))
            {
                context.Request.EnableBuffering();
                using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                context.Request.Body.Position = 0;
                using var document = System.Text.Json.JsonDocument.Parse(body);
                rpcMethod = document.RootElement.TryGetProperty("method", out var method) ? method.GetString() : null;
            }

            requests.Enqueue(new SeenRequest(context.Request.Method, rpcMethod, context.Request.Headers.Authorization.ToString() is { Length: > 0 } header ? header : null));
            await next(context);
        });
        app.MapMcp("/mcp");
        await app.StartAsync(cancellationToken);
        var address = app.Urls.First();
        return new HttpLocalMcpServer(app, new Uri(address + "/mcp"), requests);
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

[McpServerToolType]
internal sealed class HttpLocalTools
{
    [McpServerTool(Name = "whoami_http"), System.ComponentModel.Description("Returns the Authorization header of this tool call.")]
    public static string WhoAmI(IHttpContextAccessor accessor) =>
        accessor.HttpContext?.Request.Headers.Authorization.ToString() is { Length: > 0 } header ? header : "none";
}
