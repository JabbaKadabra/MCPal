using System.ComponentModel.DataAnnotations;

namespace MCPal.Server;

public sealed class McpalOptions : IValidatableObject
{
    public const string SectionName = "Mcpal";

    /// <summary>Public base URL of the server, without trailing slash. Used for OAuth metadata and the MCP resource URL.</summary>
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

    /// <summary>How long tool call audit rows are kept.</summary>
    [Range(1, 3650)]
    public int AuditRetentionDays { get; set; } = 90;

    /// <summary>Entries the audit writer holds in memory before it drops new ones (the write to the database is off the request path).</summary>
    [Range(1, 1_000_000)]
    public int AuditQueueCapacity { get; set; } = 10_000;

    /// <summary>Version of the newest bridge release, e.g. <c>1.1.0</c>. Bridges that are older get an "update available" hint in the portal. Empty disables the hint.</summary>
    public string? LatestBridgeVersion { get; set; }

    /// <summary>
    /// Releases page the setup walkthrough links to, without trailing slash. Archives are expected at
    /// <c>&lt;url&gt;/download/v&lt;version&gt;/mcpal-bridge-&lt;version&gt;-&lt;rid&gt;</c> (the GitHub release layout). Needs <see cref="LatestBridgeVersion"/> for direct links.
    /// </summary>
    [Required]
    [Url]
    public string BridgeReleaseBaseUrl { get; set; } = "https://github.com/JabbaKadabra/MCPal/releases";

    /// <summary>
    /// Container image of the bridge the setup walkthrough tells owners to run, without tag. The tag is <see cref="LatestBridgeVersion"/>
    /// (or <c>latest</c> when that is empty). Change it for a fork or a mirror.
    /// </summary>
    [Required]
    public string BridgeImage { get; set; } = "ghcr.io/jabbakadabra/mcpal-bridge";

    /// <summary>How long a bridge enrollment code (created on the Setup page) stays valid, in minutes.</summary>
    [Range(1, 1440)]
    public int EnrollmentLifetimeMinutes { get; set; } = 15;

    /// <summary>The signed caller token that local MCP servers receive with every tool call.</summary>
    public UserContextOptions UserContext { get; set; } = new();

    /// <summary>Outgoing mail (account confirmation, password reset, invitations). Without a host the mails are only written to the log.</summary>
    public SmtpOptions Smtp { get; set; } = new();

    /// <summary>Applies pending EF migrations at startup.</summary>
    public bool MigrateOnStartup { get; set; } = true;

    /// <summary>
    /// CIDR networks of reverse proxies whose X-Forwarded-For/-Proto headers are trusted, e.g. the Docker network of the
    /// TLS proxy. Loopback is always trusted.
    /// </summary>
    public string[] TrustedProxyNetworks { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.IsNullOrWhiteSpace(Smtp.Host))
        {
            if (Smtp.Port is < 1 or > 65535)
            {
                yield return new ValidationResult("The SMTP port must be between 1 and 65535.", [$"{nameof(Smtp)}.{nameof(SmtpOptions.Port)}"]);
            }

            if (string.IsNullOrWhiteSpace(Smtp.From) || !new EmailAddressAttribute().IsValid(Smtp.From))
            {
                yield return new ValidationResult("Set 'Mcpal:Smtp:From' to the sender address, e.g. mcpal@example.com.", [$"{nameof(Smtp)}.{nameof(SmtpOptions.From)}"]);
            }
        }

        if (UserContext.TokenLifetimeSeconds is < 30 or > 900)
        {
            yield return new ValidationResult("The caller token lifetime must be between 30 and 900 seconds.", [$"{nameof(UserContext)}.{nameof(UserContextOptions.TokenLifetimeSeconds)}"]);
        }

        if (UserContext.SigningKeyLifetimeDays is < 7 or > 365)
        {
            yield return new ValidationResult("The signing key lifetime must be between 7 and 365 days.", [$"{nameof(UserContext)}.{nameof(UserContextOptions.SigningKeyLifetimeDays)}"]);
        }

        foreach (var network in TrustedProxyNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(network, out _))
            {
                yield return new ValidationResult($"'{network}' is not a CIDR network such as 172.18.0.0/16.", [nameof(TrustedProxyNetworks)]);
            }
        }
    }
}

public sealed class UserContextOptions
{
    /// <summary>How long a caller token is valid (30 to 900 seconds). Short, because it is a bearer credential inside the company network.</summary>
    public int TokenLifetimeSeconds { get; set; } = 300;

    /// <summary>How long a signing key signs (7 to 365 days) before its successor takes over.</summary>
    public int SigningKeyLifetimeDays { get; set; } = 90;
}

public sealed class SmtpOptions
{
    /// <summary>SMTP server. Empty means mails are logged instead of sent (development).</summary>
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    public string? User { get; set; }

    public string? Password { get; set; }

    /// <summary>Sender address.</summary>
    public string? From { get; set; }

    /// <summary>Use STARTTLS (or implicit TLS on port 465). Turn off only for a local test server.</summary>
    public bool UseTls { get; set; } = true;
}
