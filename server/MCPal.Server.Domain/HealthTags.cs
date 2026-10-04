namespace MCPal.Server;

/// <summary>Health check tags shared by the rings that register a check and the host that maps the endpoints.</summary>
internal static class HealthTags
{
    /// <summary>Health checks with this tag decide readiness; liveness runs none.</summary>
    public const string Ready = "ready";
}
