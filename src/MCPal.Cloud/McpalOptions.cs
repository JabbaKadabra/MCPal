using System.ComponentModel.DataAnnotations;

namespace MCPal.Cloud;

public sealed class McpalOptions : IValidatableObject
{
    public const string SectionName = "Mcpal";

    /// <summary>Public base URL of the cloud, without trailing slash. Used for OAuth metadata and the MCP resource URL.</summary>
    [Required]
    [Url]
    public string PublicUrl { get; set; } = "http://localhost:8080";

    public string? DataProtectionPath { get; set; }

    [Range(1, 3600)]
    public int ToolCallTimeoutSeconds { get; set; } = 120;

    [Range(1, 1440)]
    public int AccessTokenLifetimeMinutes { get; set; } = 60;

    [Range(1, 365)]
    public int RefreshTokenLifetimeDays { get; set; } = 30;

    [Range(1, 100000)]
    public int McpRequestsPerMinute { get; set; } = 600;

    /// <summary>Applies pending EF migrations at startup.</summary>
    public bool MigrateOnStartup { get; set; } = true;

    /// <summary>
    /// CIDR networks of reverse proxies whose X-Forwarded-For/-Proto headers are trusted, e.g. the Docker network of the
    /// TLS proxy. Loopback is always trusted.
    /// </summary>
    public string[] TrustedProxyNetworks { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var network in TrustedProxyNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(network, out _))
            {
                yield return new ValidationResult($"'{network}' is not a CIDR network such as 172.18.0.0/16.", [nameof(TrustedProxyNetworks)]);
            }
        }
    }
}
