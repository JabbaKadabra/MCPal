using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using MCPal.Contracts;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace MCPal.Server.Tunnel;

/// <summary>
/// Turns a bridge's tool descriptor into the MCP tool Claude sees. Runs once at registration, so a descriptor that
/// cannot be listed is rejected there and never breaks <c>tools/list</c>.
/// </summary>
internal static class ToolListing
{
    public static bool TryCreate(
        string publicName,
        string serverName,
        ToolDescriptor descriptor,
        [NotNullWhen(true)] out Tool? tool,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicName);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(descriptor);

        tool = null;
        if (!TryParse(descriptor.InputSchemaJson, out JsonElement schema))
        {
            error = "The input schema is not valid JSON.";
            return false;
        }

        JsonElement? outputSchema = null;
        if (descriptor.OutputSchemaJson is not null)
        {
            if (!TryParse(descriptor.OutputSchemaJson, out JsonElement parsed))
            {
                error = "The output schema is not valid JSON.";
                return false;
            }

            outputSchema = parsed;
        }

        ToolAnnotations? annotations = null;
        if (descriptor.AnnotationsJson is not null && !TryParse(descriptor.AnnotationsJson, out annotations))
        {
            error = "The annotations are not valid MCP tool annotations.";
            return false;
        }

        try
        {
            tool = new Tool
            {
                Name = publicName,
                Title = descriptor.Title,
                Description = $"[{serverName}] {descriptor.Description}".TrimEnd(),
                InputSchema = schema,
                Annotations = annotations,
            };
        }
        catch (ArgumentException ex)
        {
            error = $"The input schema is not a valid MCP tool input schema: {ex.Message}";
            return false;
        }

        try
        {
            tool.OutputSchema = outputSchema;
        }
        catch (ArgumentException ex)
        {
            tool = null;
            error = $"The output schema is not a valid JSON Schema: {ex.Message}";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParse<T>(string json, [NotNullWhen(true)] out T? value)
    {
        try
        {
            value = JsonSerializer.Deserialize<T>(json, McpJsonUtilities.DefaultOptions);
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }
}
