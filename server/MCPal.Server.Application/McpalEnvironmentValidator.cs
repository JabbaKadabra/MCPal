using Microsoft.Extensions.Options;

namespace MCPal.Server;

/// <summary>
/// Checks what depends on the hosting environment. In Production the Data Protection key ring must be persistent: it protects the
/// signing keys, and a lost key ring would silently rotate them (every local server then fails to verify until it reloads the JWKS).
/// Hosts without an environment (tests that build the container directly) are not checked.
/// </summary>
internal sealed class McpalEnvironmentValidator(IHostEnvironment? environment = null) : IValidateOptions<McpalOptions>
{
    public ValidateOptionsResult Validate(string? name, McpalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return environment is { } host && host.IsProduction() && string.IsNullOrWhiteSpace(options.DataProtectionPath)
            ? ValidateOptionsResult.Fail("Set 'Mcpal:DataProtectionPath' (environment variable Mcpal__DataProtectionPath) to a persistent directory, e.g. a mounted volume. Without it the Data Protection keys, and with them the caller token signing keys, are lost on every restart.")
            : ValidateOptionsResult.Success;
    }
}
