using System.Text.Json;
using System.Text.Json.Serialization;
using MCPal.Bridge.Enrollment;

namespace MCPal.Bridge.Config;

/// <summary>
/// Reads and validates <c>mcpal.json</c> and the file with the local servers (Claude Code's <c>.mcp.json</c> shape: <c>mcp.json</c>
/// next to the config, or <c>mcpal.mcpServersFile</c>). <c>MCPAL_API_KEY</c> overrides <c>mcpal.apiKey</c>.
/// </summary>
internal static class BridgeConfigLoader
{
    public const string ApiKeyVariable = "MCPAL_API_KEY";

    /// <summary>File next to <c>mcpal.json</c> that holds the local servers when <c>mcpal.mcpServersFile</c> is not set. Optional.</summary>
    public const string DefaultServersFileName = "mcp.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static BridgeConfig Load(string path, IReadOnlyDictionary<string, string?> environment, bool requireMcpal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(environment);

        var raw = ParseRaw(ReadFile(path, "config file"));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var stored = requireMcpal ? BridgeCredentials.TryRead(CredentialsPath(raw.Mcpal, directory)) : null;
        return Build(raw, ReadServersFile(raw, directory), environment, requireMcpal, stored);
    }

    /// <summary>
    /// What <c>enroll</c> needs: the server URL (<paramref name="urlOverride"/>, else <c>mcpal.url</c> with variables expanded; null when neither
    /// is set), the bridge name, where the key goes and whether the bridge has a key already (environment, config or credentials file).
    /// </summary>
    public static EnrollmentTarget LoadEnrollmentTarget(string path, IReadOnlyDictionary<string, string?> environment, string? urlOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(environment);

        var raw = ParseRaw(ReadFile(path, "config file"));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
        var mcpal = raw.Mcpal;
        var credentialsPath = CredentialsPath(mcpal, directory);

        string? url = null;
        if (!string.IsNullOrWhiteSpace(urlOverride))
        {
            url = urlOverride.Trim();
        }
        else if (mcpal?.Url is { } rawUrl && !string.IsNullOrWhiteSpace(rawUrl))
        {
            url = EnvironmentExpander.Expand(rawUrl, environment, "Config section 'mcpal'");
        }

        var hasKey = !string.IsNullOrWhiteSpace(environment.GetValueOrDefault(ApiKeyVariable))
            || !string.IsNullOrWhiteSpace(mcpal?.ApiKey)
            || BridgeCredentials.TryRead(credentialsPath) is not null;
        var name = mcpal?.BridgeName is { } configured && !string.IsNullOrWhiteSpace(configured) ? configured : Environment.MachineName;
        return new EnrollmentTarget(url, name, credentialsPath, hasKey);
    }

    private static string CredentialsPath(RawMcpal? mcpal, string directory) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(mcpal?.CredentialsFile) ? BridgeCredentials.DefaultFileName : mcpal.CredentialsFile, directory);

    private static string ReadFile(string path, string what)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BridgeConfigException($"Cannot read {what} '{path}': {ex.Message}");
        }
    }

    /// <summary>The servers of the servers file: <c>mcpal.mcpServersFile</c> (must exist), else <c>mcp.json</c> next to the config (if it exists).</summary>
    private static ServersFile? ReadServersFile(RawConfig raw, string directory)
    {
        var named = !string.IsNullOrWhiteSpace(raw.Mcpal?.McpServersFile);
        var path = Path.GetFullPath(named ? raw.Mcpal?.McpServersFile ?? string.Empty : DefaultServersFileName, directory);
        if (!named && !File.Exists(path))
        {
            return null;
        }

        var content = ReadFile(path, "servers file");
        var noBlock = new BridgeConfigException($"Servers file '{path}' needs an 'mcpServers' block, like the .mcp.json of Claude Code.");
        if (string.IsNullOrWhiteSpace(content))
        {
            throw noBlock;
        }

        try
        {
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("mcpServers", out var block)
                || block.ValueKind != JsonValueKind.Object)
            {
                throw noBlock;
            }

            return new ServersFile(path, block.Deserialize<Dictionary<string, RawServer?>>(JsonOptions) ?? []);
        }
        catch (JsonException ex)
        {
            throw new BridgeConfigException($"Servers file '{path}' is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>The whole process environment, for <c>${VAR}</c> expansion. Names are case-insensitive on Windows only.</summary>
    public static IReadOnlyDictionary<string, string?> CurrentEnvironment()
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var variables = new Dictionary<string, string?>(comparer);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = entry.Value as string;
        }

        return variables;
    }

    /// <summary>Parses a config without a servers file (the servers are the inline <c>mcpServers</c> only).</summary>
    public static BridgeConfig Parse(string json, IReadOnlyDictionary<string, string?> environment, bool requireMcpal)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(environment);

        return Build(ParseRaw(json), null, environment, requireMcpal, storedApiKey: null);
    }

    private static RawConfig ParseRaw(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RawConfig>(json, JsonOptions) ?? new RawConfig();
        }
        catch (JsonException ex)
        {
            throw new BridgeConfigException($"Config is not valid JSON: {ex.Message}");
        }
    }

    private static BridgeConfig Build(RawConfig raw, ServersFile? serversFile, IReadOnlyDictionary<string, string?> environment, bool requireMcpal, string? storedApiKey)
    {
        var mcpal = ParseMcpal(raw.Mcpal, environment, requireMcpal, storedApiKey);
        var rawServers = MergeServers(raw, serversFile);
        var servers = new Dictionary<string, LocalServerConfig>(StringComparer.Ordinal);
        foreach (var (name, server) in rawServers)
        {
            servers[name] = ParseServer(name, server, environment);
        }

        var statusFile = string.IsNullOrWhiteSpace(raw.StatusFile) ? null : EnvironmentExpander.Expand(raw.StatusFile, environment, "Config 'statusFile'");
        var jwksFile = string.IsNullOrWhiteSpace(raw.JwksFile) ? null : EnvironmentExpander.Expand(raw.JwksFile, environment, "Config 'jwksFile'");
        return new BridgeConfig(mcpal, servers, raw.CallTimeoutSeconds is > 0 ? raw.CallTimeoutSeconds.Value : BridgeConfig.DefaultCallTimeoutSeconds)
        {
            StatusFile = statusFile,
            JwksFile = jwksFile,
        };
    }

    /// <summary>The inline servers plus the servers file, then the MCPal-only settings of <c>serverOptions</c> on top.</summary>
    private static Dictionary<string, RawServer?> MergeServers(RawConfig raw, ServersFile? serversFile)
    {
        var merged = new Dictionary<string, RawServer?>(raw.McpServers ?? [], StringComparer.Ordinal);
        foreach (var (name, server) in serversFile?.Servers ?? [])
        {
            if (!merged.TryAdd(name, server))
            {
                throw new BridgeConfigException($"Server '{name}' is defined in both 'mcpServers' of the config and in '{serversFile?.Path}'. Keep it in one place.");
            }
        }

        foreach (var (name, options) in raw.ServerOptions ?? [])
        {
            if (!merged.TryGetValue(name, out var server))
            {
                throw new BridgeConfigException($"'serverOptions' names the server '{name}', which is in neither 'mcpServers' nor the servers file.");
            }

            if (options is null)
            {
                continue;
            }

            server ??= new RawServer();
            server.IncludeTools = options.IncludeTools ?? server.IncludeTools;
            server.ExcludeTools = options.ExcludeTools ?? server.ExcludeTools;
            server.UserContext = options.UserContext ?? server.UserContext;
            server.UserTokenHeader = options.UserTokenHeader ?? server.UserTokenHeader;
            merged[name] = server;
        }

        return merged;
    }

    private static McpalConfig ParseMcpal(RawMcpal? raw, IReadOnlyDictionary<string, string?> environment, bool required, string? storedApiKey)
    {
        // Keys from the environment and the credentials file are literal: only the key written in mcpal.json may refer to ${VARIABLES}.
        var apiKey = environment.GetValueOrDefault(ApiKeyVariable);
        var keyIsLiteral = !string.IsNullOrWhiteSpace(apiKey);
        if (!keyIsLiteral)
        {
            apiKey = raw?.ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(storedApiKey))
            {
                apiKey = storedApiKey;
                keyIsLiteral = true;
            }
        }

        var url = raw?.Url;
        if (required)
        {
            const string context = "Config section 'mcpal'";
            url = url is null ? null : EnvironmentExpander.Expand(url, environment, context);
            apiKey = apiKey is null || keyIsLiteral
                ? apiKey
                : EnvironmentExpander.Expand(apiKey, environment, context);
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                throw new BridgeConfigException("Config section 'mcpal' needs 'url' with an absolute http(s) URL.");
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new BridgeConfigException($"Config section 'mcpal' needs 'apiKey' (or set {ApiKeyVariable}, or enroll this bridge with MCPAL_ENROLL; see the Setup page of the portal).");
            }
        }

        return new McpalConfig(
            (url ?? string.Empty).TrimEnd('/'),
            apiKey ?? string.Empty,
            string.IsNullOrWhiteSpace(raw?.BridgeName) ? Environment.MachineName : raw.BridgeName);
    }

    private static LocalServerConfig ParseServer(string name, RawServer? raw, IReadOnlyDictionary<string, string?> environment)
    {
        raw = Expand(name, raw ?? new RawServer(), environment);
        var hasCommand = !string.IsNullOrWhiteSpace(raw.Command);
        var hasUrl = !string.IsNullOrWhiteSpace(raw.Url);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BridgeConfigException("A server in 'mcpServers' has an empty name.");
        }

        if (hasCommand && hasUrl)
        {
            throw new BridgeConfigException($"Server '{name}' must have either 'command' or 'url', not both.");
        }

        if (!hasCommand && !hasUrl)
        {
            throw new BridgeConfigException($"Server '{name}' needs 'command' (stdio) or 'url' (HTTP).");
        }

        if (hasUrl && (!Uri.TryCreate(raw.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        {
            throw new BridgeConfigException($"Server '{name}' needs 'url' to be an absolute http(s) URL.");
        }

        var userContext = raw.UserContext ?? true;
        var userTokenHeader = string.IsNullOrWhiteSpace(raw.UserTokenHeader) ? null : raw.UserTokenHeader.Trim();
        ValidateUserTokenHeader(name, userTokenHeader, hasCommand, userContext, raw.Headers);

        return new LocalServerConfig(
            hasCommand ? raw.Command : null,
            raw.Args ?? [],
            raw.Env ?? [],
            hasUrl ? raw.Url : null,
            raw.Headers ?? [])
        {
            IncludeTools = Patterns(name, "includeTools", raw.IncludeTools),
            ExcludeTools = Patterns(name, "excludeTools", raw.ExcludeTools),
            UserContext = userContext,
            UserTokenHeader = userTokenHeader,
        };
    }

    private static void ValidateUserTokenHeader(string server, string? header, bool stdio, bool userContext, Dictionary<string, string>? staticHeaders)
    {
        if (header is null)
        {
            return;
        }

        if (stdio)
        {
            throw new BridgeConfigException($"Server '{server}': 'userTokenHeader' is only for HTTP servers ('url'). A stdio server reads the caller from the request's _meta.");
        }

        if (!userContext)
        {
            throw new BridgeConfigException($"Server '{server}': 'userTokenHeader' has no effect while 'userContext' is false.");
        }

        if (!IsHeaderName(header))
        {
            throw new BridgeConfigException($"Server '{server}': 'userTokenHeader' '{header}' is not a valid HTTP header name.");
        }

        if (staticHeaders?.Keys.Any(key => string.Equals(key, header, StringComparison.OrdinalIgnoreCase)) == true)
        {
            throw new BridgeConfigException($"Server '{server}': the header '{header}' is set in both 'headers' and 'userTokenHeader'. Remove it from 'headers'.");
        }
    }

    /// <summary>An HTTP header name is a token (RFC 9110): letters, digits and <c>!#$%&amp;'*+-.^_`|~</c>.</summary>
    private static bool IsHeaderName(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal));

    private static List<string> Patterns(string server, string field, List<string>? patterns)
    {
        if (patterns?.Any(string.IsNullOrWhiteSpace) == true)
        {
            throw new BridgeConfigException($"Server '{server}': '{field}' must not contain empty patterns.");
        }

        return patterns ?? [];
    }

    private static RawServer Expand(string name, RawServer raw, IReadOnlyDictionary<string, string?> environment)
    {
        var context = $"Server '{name}'";
        string? One(string? value) => value is null ? null : EnvironmentExpander.Expand(value, environment, context);
        Dictionary<string, string>? Map(Dictionary<string, string>? values) =>
            values?.ToDictionary(pair => pair.Key, pair => EnvironmentExpander.Expand(pair.Value, environment, context));

        return new RawServer
        {
            Command = One(raw.Command),
            Args = raw.Args?.Select(arg => EnvironmentExpander.Expand(arg, environment, context)).ToList(),
            Env = Map(raw.Env),
            Url = One(raw.Url),
            Headers = Map(raw.Headers),
            IncludeTools = raw.IncludeTools,
            ExcludeTools = raw.ExcludeTools,
            UserContext = raw.UserContext,
            UserTokenHeader = raw.UserTokenHeader,
        };
    }

    private sealed class RawConfig
    {
        public RawMcpal? Mcpal { get; set; }

        public Dictionary<string, RawServer?>? McpServers { get; set; }

        /// <summary>MCPal-only settings per server, so the servers themselves can stay in a file copied from another tool.</summary>
        public Dictionary<string, RawServerOptions?>? ServerOptions { get; set; }

        public int? CallTimeoutSeconds { get; set; }

        public string? StatusFile { get; set; }

        public string? JwksFile { get; set; }
    }

    private sealed class RawMcpal
    {
        public string? Url { get; set; }

        public string? ApiKey { get; set; }

        public string? BridgeName { get; set; }

        /// <summary>File with the local servers in the Claude Code <c>.mcp.json</c> shape; relative to the config. Default: <c>mcp.json</c> next to it, if present.</summary>
        public string? McpServersFile { get; set; }

        /// <summary>File that holds the key the bridge got by enrolling; relative to the config. Default: <c>credentials.json</c> next to it.</summary>
        public string? CredentialsFile { get; set; }
    }

    private sealed class RawServerOptions
    {
        public List<string>? IncludeTools { get; set; }

        public List<string>? ExcludeTools { get; set; }

        public bool? UserContext { get; set; }

        public string? UserTokenHeader { get; set; }
    }

    private sealed record ServersFile(string Path, Dictionary<string, RawServer?> Servers);

    private sealed class RawServer
    {
        public string? Command { get; set; }

        public List<string>? Args { get; set; }

        public Dictionary<string, string>? Env { get; set; }

        public string? Url { get; set; }

        public Dictionary<string, string>? Headers { get; set; }

        public List<string>? IncludeTools { get; set; }

        public List<string>? ExcludeTools { get; set; }

        public bool? UserContext { get; set; }

        public string? UserTokenHeader { get; set; }
    }
}
