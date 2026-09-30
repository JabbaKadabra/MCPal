using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCPal.Agent.Config;

/// <summary>Reads and validates <c>mcpal.json</c>. <c>MCPAL_API_KEY</c> overrides <c>cloud.apiKey</c>.</summary>
internal static class AgentConfigLoader
{
    public const string ApiKeyVariable = "MCPAL_API_KEY";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static AgentConfig Load(string path, IReadOnlyDictionary<string, string?> environment, bool requireCloud)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AgentConfigException($"Cannot read config file '{path}': {ex.Message}");
        }

        return Parse(json, environment, requireCloud);
    }

    public static IReadOnlyDictionary<string, string?> CurrentEnvironment() =>
        new Dictionary<string, string?> { [ApiKeyVariable] = Environment.GetEnvironmentVariable(ApiKeyVariable) };

    public static AgentConfig Parse(string json, IReadOnlyDictionary<string, string?> environment, bool requireCloud)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(environment);

        RawConfig? raw;
        try
        {
            raw = JsonSerializer.Deserialize<RawConfig>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new AgentConfigException($"Config is not valid JSON: {ex.Message}");
        }

        raw ??= new RawConfig();
        var cloud = ParseCloud(raw.Cloud, environment, requireCloud);
        var servers = new Dictionary<string, LocalServerConfig>(StringComparer.Ordinal);
        foreach (var (name, server) in raw.McpServers ?? [])
        {
            servers[name] = ParseServer(name, server);
        }

        return new AgentConfig(cloud, servers, raw.CallTimeoutSeconds is > 0 ? raw.CallTimeoutSeconds.Value : AgentConfig.DefaultCallTimeoutSeconds);
    }

    private static CloudConfig ParseCloud(RawCloud? raw, IReadOnlyDictionary<string, string?> environment, bool required)
    {
        var apiKey = environment.GetValueOrDefault(ApiKeyVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = raw?.ApiKey;
        }

        var url = raw?.Url;
        if (required)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                throw new AgentConfigException("Config section 'cloud' needs 'url' with an absolute http(s) URL.");
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new AgentConfigException($"Config section 'cloud' needs 'apiKey' (or set {ApiKeyVariable}).");
            }
        }

        return new CloudConfig(
            (url ?? string.Empty).TrimEnd('/'),
            apiKey ?? string.Empty,
            string.IsNullOrWhiteSpace(raw?.AgentName) ? Environment.MachineName : raw.AgentName);
    }

    private static LocalServerConfig ParseServer(string name, RawServer? raw)
    {
        raw ??= new RawServer();
        var hasCommand = !string.IsNullOrWhiteSpace(raw.Command);
        var hasUrl = !string.IsNullOrWhiteSpace(raw.Url);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AgentConfigException("A server in 'mcpServers' has an empty name.");
        }

        if (hasCommand && hasUrl)
        {
            throw new AgentConfigException($"Server '{name}' must have either 'command' or 'url', not both.");
        }

        if (!hasCommand && !hasUrl)
        {
            throw new AgentConfigException($"Server '{name}' needs 'command' (stdio) or 'url' (HTTP).");
        }

        if (hasUrl && (!Uri.TryCreate(raw.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        {
            throw new AgentConfigException($"Server '{name}' needs 'url' to be an absolute http(s) URL.");
        }

        return new LocalServerConfig(
            hasCommand ? raw.Command : null,
            raw.Args ?? [],
            raw.Env ?? [],
            hasUrl ? raw.Url : null,
            raw.Headers ?? []);
    }

    private sealed class RawConfig
    {
        public RawCloud? Cloud { get; set; }

        public Dictionary<string, RawServer?>? McpServers { get; set; }

        public int? CallTimeoutSeconds { get; set; }
    }

    private sealed class RawCloud
    {
        public string? Url { get; set; }

        public string? ApiKey { get; set; }

        public string? AgentName { get; set; }
    }

    private sealed class RawServer
    {
        public string? Command { get; set; }

        public List<string>? Args { get; set; }

        public Dictionary<string, string>? Env { get; set; }

        public string? Url { get; set; }

        public Dictionary<string, string>? Headers { get; set; }
    }
}
