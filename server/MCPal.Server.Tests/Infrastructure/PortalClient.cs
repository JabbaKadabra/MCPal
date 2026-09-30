using System.Net.Http.Json;
using System.Text.Json;

namespace MCPal.Server.Tests.Infrastructure;

/// <summary>A browser-like client for the portal API: keeps the session cookie and sends the anti-forgery header.</summary>
internal sealed class PortalClient : IDisposable
{
    private readonly HttpClient http;
    private string? csrfToken;

    public PortalClient(ServerWebApplicationFactory factory)
    {
        http = factory.CreateClient();
    }

    public static string Password => "correct-horse-battery";

    public async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellationToken) => await http.GetAsync(url, cancellationToken);

    public async Task<HttpResponseMessage> PostAsync(string url, object? body, CancellationToken cancellationToken, bool withCsrf = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        await AddCsrfAsync(request, withCsrf, cancellationToken);
        var response = await http.SendAsync(request, cancellationToken);

        // Anti-forgery tokens are bound to the signed-in user, so they must be fetched again after the identity changed.
        if (url.StartsWith("/api/portal/auth/", StringComparison.Ordinal) || url == "/api/portal/invitations/accept")
        {
            csrfToken = null;
        }

        return response;
    }

    public async Task<HttpResponseMessage> PatchAsync(string url, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body) };
        await AddCsrfAsync(request, true, cancellationToken);
        return await http.SendAsync(request, cancellationToken);
    }

    public async Task<HttpResponseMessage> DeleteAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        await AddCsrfAsync(request, true, cancellationToken);
        return await http.SendAsync(request, cancellationToken);
    }

    public async Task<HttpResponseMessage> SignupAsync(string company, string email, CancellationToken cancellationToken) =>
        await PostAsync("/api/portal/auth/signup", new { companyName = company, email, password = Password }, cancellationToken);

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.Clone();
    }

    public void Dispose() => http.Dispose();

    private async Task AddCsrfAsync(HttpRequestMessage request, bool withCsrf, CancellationToken cancellationToken)
    {
        if (!withCsrf)
        {
            return;
        }

        if (csrfToken is null)
        {
            using var response = await http.GetAsync("/api/portal/csrf", cancellationToken);
            csrfToken = (await JsonAsync(response, cancellationToken)).GetProperty("token").GetString();
        }

        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
    }
}
