using Microsoft.Extensions.Options;

namespace MCPal.Server.Tenancy;

internal sealed record EnrollRequest(string? Code, string? BridgeName);

internal sealed record EnrollResponse(string Url, string ApiKey);

/// <summary>
/// Anonymous: the enrollment code is the credential. A bridge trades it for its own bridge key. It sits outside <c>/api/portal</c>
/// because no cookie, user or anti-forgery token is involved; the rate limit policy slows guessing and creation spam.
/// </summary>
internal static class BridgeEnrollEndpoints
{
    public const string RateLimitPolicy = "enroll";

    public static void Map(IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/bridge/enroll", EnrollAsync).RequireRateLimiting(RateLimitPolicy);
    }

    private static async Task<IResult> EnrollAsync(EnrollRequest? request, BridgeEnrollmentService enrollments, IOptions<McpalOptions> options, CancellationToken cancellationToken)
    {
        var redeemed = await enrollments.RedeemAsync(request?.Code, request?.BridgeName, cancellationToken);
        return redeemed is null
            ? Results.Json(new { error = "invalid_code" }, statusCode: StatusCodes.Status400BadRequest)
            : Results.Json(new EnrollResponse(options.Value.PublicUrl.TrimEnd('/'), redeemed.ApiKey));
    }
}
