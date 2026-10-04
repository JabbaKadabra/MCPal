using System.Text.Json;
using MCPal.Bridge.Config;

namespace MCPal.Bridge.Enrollment;

/// <summary>
/// The bridge key a bridge got by enrolling, kept in a small JSON file next to <c>mcpal.json</c> (or at <c>mcpal.credentialsFile</c>).
/// On Unix the file is mode 600; on Windows the ACL of the data directory (set by <c>install.ps1</c>) protects it.
/// </summary>
internal static class BridgeCredentials
{
    public const string DefaultFileName = "credentials.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The stored key, or null when the file does not exist or holds none.</summary>
    public static string? TryRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return null;
        }

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BridgeConfigException($"Cannot read credentials file '{path}': {ex.Message}");
        }

        try
        {
            var key = JsonSerializer.Deserialize<StoredCredentials>(content, JsonOptions)?.ApiKey;
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
        }
        catch (JsonException ex)
        {
            throw new BridgeConfigException($"Credentials file '{path}' is not valid: {ex.Message}");
        }
    }

    public static void Write(string path, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var stream = new FileStream(path, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(JsonSerializer.Serialize(new StoredCredentials(apiKey), JsonOptions));
            }

            if (!OperatingSystem.IsWindows())
            {
                // UnixCreateMode applies to new files only; an older, wider file is narrowed here.
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BridgeConfigException($"Cannot write credentials file '{path}': {ex.Message}");
        }
    }

    private sealed record StoredCredentials(string? ApiKey);
}
