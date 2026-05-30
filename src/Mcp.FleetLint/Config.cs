using System.Text.Json.Serialization;

namespace Mcp.FleetLint;

/// <summary>
/// The canonical fleet inventory. Lives at <c>fleet-lint.json</c> in the mcp-tooling repo root
/// and is downloaded by downstream callers from
/// <c>https://raw.githubusercontent.com/Platonenkov/mcp-tooling/main/fleet-lint.json</c>.
/// Single source of truth for every cross-repo invariant that's not a local code property.
/// </summary>
public sealed class FleetConfig
{
    /// <summary>
    /// Hostname of the central OAuth 2.1 authorization server (no protocol). All cloud MCPs
    /// validate JWTs against this AS via JWKS. Any reference to a different <c>auth.mcp.*</c>
    /// hostname is a typo.
    /// </summary>
    [JsonPropertyName("authorizationServer")]
    public string AuthorizationServer { get; set; } = string.Empty;

    /// <summary>One entry per cloud MCP plugin.</summary>
    [JsonPropertyName("mcps")]
    public List<McpEntry> Mcps { get; set; } = new();

    /// <summary>
    /// Additional <c>*.staticbit.io</c> hostnames that are NOT MCP endpoints but are referenced
    /// from docs and should not be flagged as typos (e.g. marketing site, admin panel paths).
    /// </summary>
    [JsonPropertyName("allowedExtraHostnames")]
    public List<string> AllowedExtraHostnames { get; set; } = new();
}

public sealed class McpEntry
{
    /// <summary>
    /// Stable identifier (lowercase). Often matches the OAuth scope; used as a fallback when
    /// the plugin manifest doesn't expose the scope directly.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>OAuth 2.1 scope this MCP gates <c>/mcp</c> on.</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    /// <summary>Canonical hostname of the <c>https://&lt;host&gt;/mcp</c> endpoint (no protocol).</summary>
    [JsonPropertyName("host")]
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Port used by the Claude Code plugin for the OAuth callback (per-MCP unique). The
    /// <c>oauth.callbackPort</c> field of the plugin's <c>.mcp.json</c> manifest must match.
    /// </summary>
    [JsonPropertyName("callbackPort")]
    public int CallbackPort { get; set; }

    /// <summary>
    /// GitHub <c>owner/repo</c> hosting this MCP's code. Used to identify the current repo
    /// by matching against <c>git remote get-url origin</c>.
    /// </summary>
    [JsonPropertyName("repo")]
    public string Repo { get; set; } = string.Empty;
}
