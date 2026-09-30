using Microsoft.Extensions.DependencyInjection;

namespace MCPal.Cloud;

/// <summary>ASP.NET Core pipeline services (authentication, SignalR, MCP, ...). Grows in later phases.</summary>
internal static class CloudWebServices
{
    public static void Register(IServiceCollection services)
    {
        services.AddHealthChecks();
    }
}
