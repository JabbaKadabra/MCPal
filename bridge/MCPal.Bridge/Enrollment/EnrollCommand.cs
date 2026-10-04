using MCPal.Bridge.Config;

namespace MCPal.Bridge.Enrollment;

/// <summary>The <c>enroll</c> verb and the automatic enrollment of <c>run</c>.</summary>
internal static class EnrollCommand
{
    /// <summary>Enrolls with the given code (argument, else <c>MCPAL_ENROLL</c>). Exit code 0 enrolled, 1 enrollment failed, 2 missing input.</summary>
    public static async Task<int> RunAsync(
        string configPath,
        string? url,
        string? code,
        IReadOnlyDictionary<string, string?> environment,
        HttpClient http,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var enrollmentCode = FirstNonBlank(code, environment.GetValueOrDefault(BridgeEnroller.CodeVariable));
        if (enrollmentCode is null)
        {
            error.WriteLine($"Give the enrollment code with --code or {BridgeEnroller.CodeVariable}. Create one on the Setup page of the portal.");
            return 2;
        }

        var target = BridgeConfigLoader.LoadEnrollmentTarget(configPath, environment, url);
        if (target.Url is null)
        {
            error.WriteLine("Give the MCPal server URL with --url, MCPAL_URL (when mcpal.json refers to it) or 'mcpal.url' in mcpal.json.");
            return 2;
        }

        try
        {
            await BridgeEnroller.EnrollAsync(http, target.Url, enrollmentCode, target.BridgeName, target.CredentialsPath, cancellationToken);
        }
        catch (EnrollmentException ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }

        output.WriteLine($"Enrolled bridge '{target.BridgeName}' at {target.Url}. The key is saved in {target.CredentialsPath}.");
        return 0;
    }

    /// <summary>
    /// For <c>run</c>: enrolls only when <c>MCPAL_ENROLL</c> is set and the bridge has no key yet. A restarted container keeps its key and
    /// ignores the (spent) code. Returns 0 when there was nothing to do or enrolling worked.
    /// </summary>
    public static async Task<int> EnrollWhenNeededAsync(
        string configPath,
        IReadOnlyDictionary<string, string?> environment,
        HttpClient http,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(environment);

        if (string.IsNullOrWhiteSpace(environment.GetValueOrDefault(BridgeEnroller.CodeVariable)))
        {
            return 0;
        }

        if (BridgeConfigLoader.LoadEnrollmentTarget(configPath, environment, urlOverride: null).HasKey)
        {
            return 0;
        }

        return await RunAsync(configPath, url: null, code: null, environment, http, output, error, cancellationToken);
    }

    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
