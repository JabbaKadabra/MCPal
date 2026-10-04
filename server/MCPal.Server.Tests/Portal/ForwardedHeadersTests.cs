using System.Net;
using MCPal.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

namespace MCPal.Server.Tests.Portal;

/// <summary>The server runs behind a TLS-terminating reverse proxy; client IP and scheme come from X-Forwarded-* of trusted proxies only.</summary>
[TestFixture]
internal sealed class ForwardedHeadersTests
{
    private const int PortalLimitPerMinute = 120;
    private static readonly IPAddress Proxy = IPAddress.Parse("10.0.0.5");

    private static CancellationToken Ct => TestContext.CurrentContext.CancellationToken;

    private static Task<ServerWebApplicationFactory> CreateBehindProxyAsync() =>
        ServerWebApplicationFactory.CreateAsync(Ct, settings: new() { ["Mcpal:TrustedProxyNetworks:0"] = "10.0.0.0/8" });

    private static async Task<HttpContext> GetCsrfAsync(ServerWebApplicationFactory factory, IPAddress remote, string? forwardedFor, string? forwardedProto = null)
    {
        return await factory.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/api/portal/csrf";
            context.Connection.RemoteIpAddress = remote;
            if (forwardedFor is not null)
            {
                context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            }

            if (forwardedProto is not null)
            {
                context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
            }
        }, Ct);
    }

    [Test]
    public async Task PortalRateLimit_BehindTrustedProxy_CountsEachForwardedClientSeparately()
    {
        await using var factory = await CreateBehindProxyAsync();
        for (var i = 0; i < PortalLimitPerMinute; i++)
        {
            (await GetCsrfAsync(factory, Proxy, "203.0.113.1")).Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        var busyClient = await GetCsrfAsync(factory, Proxy, "203.0.113.1");
        var otherClient = await GetCsrfAsync(factory, Proxy, "203.0.113.2");

        busyClient.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        otherClient.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Test]
    public async Task PortalRateLimit_UntrustedSenderWithForwardedFor_IgnoresHeader()
    {
        await using var factory = await CreateBehindProxyAsync();
        var attacker = IPAddress.Parse("198.51.100.7");
        for (var i = 0; i < PortalLimitPerMinute; i++)
        {
            (await GetCsrfAsync(factory, attacker, $"203.0.113.{i % 250}")).Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        var next = await GetCsrfAsync(factory, attacker, "192.0.2.99");

        next.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
    }

    [Test]
    public async Task Cookies_ForwardedHttpsFromTrustedProxy_AreSecure()
    {
        await using var factory = await CreateBehindProxyAsync();

        var context = await GetCsrfAsync(factory, Proxy, "203.0.113.1", "https");

        var cookies = context.Response.Headers.SetCookie.ToString();
        cookies.Should().Contain("mcpal.csrf=").And.Contain("secure");
    }

    [Test]
    public async Task Cookies_PlainHttpWithoutProxy_AreNotSecure()
    {
        await using var factory = await CreateBehindProxyAsync();

        var context = await GetCsrfAsync(factory, IPAddress.Parse("198.51.100.7"), null, "https");

        context.Response.Headers.SetCookie.ToString().Should().Contain("mcpal.csrf=").And.NotContain("secure");
    }
}
