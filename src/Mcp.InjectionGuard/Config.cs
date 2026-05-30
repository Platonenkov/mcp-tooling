using System.Text.Json.Serialization;

namespace Mcp.InjectionGuard;

/// <summary>
/// Optional <c>injectionguard.json</c> at the repo root. Tunes the scan scope, lists
/// per-method exemptions, and lets the consumer extend or override the built-in
/// "external-content-returning" heuristics. Absence of the file means "scan
/// <c>src/**/Tools/*.cs</c> with default heuristics and no exemptions" — the strictest
/// mode short of forcing <c>[ExternalContent]</c> annotations everywhere.
/// </summary>
public sealed class InjectionGuardConfig
{
    /// <summary>
    /// Glob patterns (relative to <c>--repo-root</c>, forward slashes, <c>**</c> = any subtree)
    /// of C# files to scan. Default: <c>src/**/Tools/*.cs</c> — matches the convention used
    /// across every MCP server in the fleet.
    /// </summary>
    [JsonPropertyName("include")]
    public List<string> Include { get; set; } = new() { "src/**/Tools/*.cs" };

    /// <summary>
    /// Per-method exemption list. Entries match by either the simple method name
    /// (<c>"SendMessage"</c>) or the qualified <c>Type.Method</c> form
    /// (<c>"SendMessageTool.SendMessage"</c>). An exempt method is skipped entirely
    /// regardless of how the heuristic classifies it — equivalent to a
    /// <c>[NotExternalContent]</c> attribute.
    /// </summary>
    [JsonPropertyName("exempt")]
    public List<string> Exempt { get; set; } = new();

    /// <summary>
    /// Method-name prefixes (case-sensitive, matched as <c>^prefix</c>) that classify the
    /// method as external-content-returning when none of the attribute opt-ins/opt-outs
    /// fire. Default mirrors the conservative <c>Get|Read|Search|List|Find|Fetch|Resolve</c>
    /// set; consumers can extend (e.g. <c>"Dump"</c>) but cannot shrink — the built-ins
    /// are always honoured.
    /// </summary>
    [JsonPropertyName("extraNamePrefixes")]
    public List<string> ExtraNamePrefixes { get; set; } = new();

    /// <summary>
    /// Substring fragments of invocation expressions whose presence in a method body marks
    /// the method as external-content-returning (e.g. <c>"GetAsync"</c>,
    /// <c>"SendRequestAsync"</c>). Default mirrors the Telegram / X / XRPL / HTTP /
    /// IMcpSecretResolver heuristic; consumers can add custom service method names.
    /// </summary>
    [JsonPropertyName("extraInvocationFragments")]
    public List<string> ExtraInvocationFragments { get; set; } = new();
}
