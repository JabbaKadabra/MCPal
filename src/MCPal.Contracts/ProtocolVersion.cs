namespace MCPal.Contracts;

/// <summary>Version of the agent tunnel protocol. The cloud rejects agents with an unknown major version.</summary>
public static class ProtocolVersion
{
    public const string Current = "1.0";

    public const int Major = 1;
}
