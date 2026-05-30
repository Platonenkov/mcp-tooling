using System.Text.Json.Serialization;

namespace Mcp.SkillLint;

/// <summary>
/// Optional <c>skilllint.json</c> at the repo root. Declares intentional cross-plugin
/// trigger sharing (e.g. <c>"telegram"</c> appearing in both <c>telegram-bot</c> and
/// <c>telegram-user</c>) so the tool does not flag it as a conflict. Absence of the file
/// means "every overlap is a conflict" — the strictest mode.
/// </summary>
public sealed class SkillLintConfig
{
    /// <summary>
    /// Trigger keywords that are deliberately shared across two or more plugins. Each entry
    /// pins the keyword to an exact set of plugins; an overlap that exactly matches one of
    /// the whitelist entries is suppressed, anything else still fails.
    /// </summary>
    [JsonPropertyName("sharedTriggers")]
    public List<SharedTrigger> SharedTriggers { get; set; } = new();

    /// <summary>
    /// Plugin directory names to skip entirely (no SKILL.md under them will be scanned).
    /// Use for archived or in-progress plugins where triggers are intentionally placeholder.
    /// </summary>
    [JsonPropertyName("excludePlugins")]
    public List<string> ExcludePlugins { get; set; } = new();

    /// <summary>
    /// Maximum Levenshtein distance for the near-overlap warning. Default 2; raising it
    /// catches more typos but produces more noise on legitimately distinct phrases.
    /// </summary>
    [JsonPropertyName("nearOverlapDistance")]
    public int NearOverlapDistance { get; set; } = 2;

    /// <summary>
    /// Minimum trigger length to consider for near-overlap warnings. Very short triggers
    /// (e.g. "X", "DM") generate too many false positives; default 5 chars.
    /// </summary>
    [JsonPropertyName("nearOverlapMinLength")]
    public int NearOverlapMinLength { get; set; } = 5;
}

public sealed class SharedTrigger
{
    /// <summary>The exact trigger phrase (case-insensitive match).</summary>
    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = string.Empty;

    /// <summary>
    /// The plugin directory names that are allowed to share this trigger. An occurrence
    /// in any plugin NOT in this set is still flagged.
    /// </summary>
    [JsonPropertyName("plugins")]
    public List<string> Plugins { get; set; } = new();
}
