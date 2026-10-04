using System.Net;
using System.Net.Http.Json;

namespace MCPal.Bridge.Enrollment;

/// <summary>Enrolling failed in a way the operator can act on; the message says how.</summary>
internal sealed class EnrollmentException(string message) : Exception(message);

/// <summary>Trades an enrollment code (made on the portal's Setup page) for a bridge key and saves the key in the credentials file.</summary>
internal static class BridgeEnroller
{
    public const string CodeVariable = "MCPAL_ENROLL";

    public const string InvalidCodeMessage = "The enrollment code is invalid, expired or already used. Create a new one on the Setup page of the portal.";

    /// <returns>The new bridge key (also written to <paramref name="credentialsPath"/>).</returns>
    public static async Task<string> EnrollAsync(HttpClient http, string serverUrl, string code, string bridgeName, string credentialsPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialsPath);

        var endpoint = new Uri(serverUrl.TrimEnd('/') + "/api/bridge/enroll");
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(endpoint, new { code, bridgeName }, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new EnrollmentException($"Cannot reach {endpoint}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EnrollmentException($"No answer from {endpoint} (timeout).");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                throw new EnrollmentException(InvalidCodeMessage);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new EnrollmentException("Too many enrollment attempts from this address. Wait a minute and try again.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new EnrollmentException($"Enrollment at {endpoint} failed: HTTP {(int)response.StatusCode}.");
            }

            var result = await response.Content.ReadFromJsonAsync<EnrollResponse>(cancellationToken);
            if (result?.ApiKey is not { Length: > 0 } key)
            {
                throw new EnrollmentException($"The answer of {endpoint} holds no key.");
            }

            BridgeCredentials.Write(credentialsPath, key);
            return key;
        }
    }

    private sealed record EnrollResponse(string? Url, string? ApiKey);
}
