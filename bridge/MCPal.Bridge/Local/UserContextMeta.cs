using System.Text.Json.Nodes;
using MCPal.Contracts;

namespace MCPal.Bridge.Local;

/// <summary>
/// Puts the caller into the <c>_meta</c> of a <c>tools/call</c> request to a local MCP server, under the key
/// <see cref="Key"/>. The bridge builds the request itself and never forwards Claude's <c>_meta</c>; this still removes the key
/// first, so nothing but the MCPal server's own caller can ever appear under it.
/// </summary>
internal static class UserContextMeta
{
    public const string Key = "eu.nordstein.mcp/user";

    /// <summary>
    /// Returns <paramref name="existing"/> without any <see cref="Key"/> and, when <paramref name="user"/> is given, with the caller under
    /// that key. Other keys stay. Null when nothing is left to send.
    /// </summary>
    public static JsonObject? Apply(JsonObject? existing, UserContext? user)
    {
        var meta = existing ?? [];
        meta.Remove(Key);
        if (user is not null)
        {
            meta[Key] = new JsonObject
            {
                ["token"] = user.Token,
                ["sub"] = user.UserId,
                ["email"] = user.Email,
                ["name"] = user.Name,
                ["groups"] = new JsonArray([.. user.Groups.Select(group => (JsonNode?)JsonValue.Create(group))]),
                ["companyId"] = user.CompanyId.ToString(),
                ["company"] = user.Company,
            };
        }

        return meta.Count == 0 ? null : meta;
    }
}
