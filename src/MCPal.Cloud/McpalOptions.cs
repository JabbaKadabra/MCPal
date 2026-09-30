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

    /// <summary>How long tool call audit rows are kept.</summary>
    [Range(1, 3650)]
    public int AuditRetentionDays { get; set; } = 90;

    /// <summary>Entries the audit writer holds in memory before it drops new ones (the write to the database is off the request path).</summary>
    [Range(1, 1_000_000)]
    public int AuditQueueCapacity { get; set; } = 10_000;

    /// <summary>Version of the newest agent release, e.g. <c>1.1.0</c>. Agents that are older get an "update available" hint in the portal. Empty disables the hint.</summary>
    public string? LatestAgentVersion { get; set; }

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

        foreach (var network in TrustedProxyNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(network, out _))
            {
                yield return new ValidationResult($"'{network}' is not a CIDR network such as 172.18.0.0/16.", [nameof(TrustedProxyNetworks)]);
            }
        }
    }
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
