using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mcp.ToolsDoc;

/// <summary>
/// Repo-specific configuration, read from <c>toolsdoc.json</c> at the repo root. Only the
/// servers list + output/marker paths differ per repo; the scanner/renderer is shared.
/// </summary>
public sealed class ToolsDocConfig
{
    /// <summary>MCP servers to scan. Each contributes a section to the generated reference.</summary>
    [JsonPropertyName("servers")]
    public List<ServerConfig> Servers { get; set; } = [];

    /// <summary>Where to write the generated reference (repo-relative). Default docs/TOOLS.generated.md.</summary>
    [JsonPropertyName("generatedOutput")]
    public string GeneratedOutput { get; set; } = "docs/TOOLS.generated.md";

    /// <summary>Optional: files whose <c>&lt;!-- toolcount:NAME --&gt;…&lt;!-- /toolcount:NAME --&gt;</c>
    /// markers are kept in sync with current tool counts (repo-relative).</summary>
    [JsonPropertyName("markerFiles")]
    public List<string> MarkerFiles { get; set; } = [];

    /// <summary>Optional: a hand-curated cheatsheet that must mention every tool by name (repo-relative).</summary>
    [JsonPropertyName("cheatsheet")]
    public string? Cheatsheet { get; set; }

    public static ToolsDocConfig Load(string path)
    {
        string json = File.ReadAllText(path);
        ToolsDocConfig? cfg = JsonSerializer.Deserialize<ToolsDocConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        if (cfg is null || cfg.Servers.Count == 0)
        {
            throw new InvalidOperationException($"{path}: config is empty or has no 'servers'.");
        }
        return cfg;
    }
}

/// <summary>One MCP server in the config: where its tools live and how to label it.</summary>
public sealed class ServerConfig
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    /// <summary>Repo-relative directory containing the <c>*.cs</c> tool definitions.</summary>
    [JsonPropertyName("toolsDir")] public string ToolsDir { get; set; } = "";
    /// <summary>Short prose shown under the server heading in the generated doc.</summary>
    [JsonPropertyName("blurb")] public string Blurb { get; set; } = "";
}
