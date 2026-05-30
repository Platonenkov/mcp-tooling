using System.Text.Json.Serialization;

namespace Mcp.LinkCheck;

/// <summary>
/// Optional per-repo config loaded from <c>linkcheck.json</c> at the repo root. All fields
/// are optional with sensible defaults; the tool runs config-less in most cases.
/// </summary>
public sealed class LinkCheckConfig
{
    /// <summary>
    /// Glob patterns (repo-relative) of <c>.md</c> files to skip entirely. Useful for
    /// auto-generated or historical/archive files where stale links are expected. Defaults
    /// include the toolsdoc auto-generated output and anything in <c>bin/</c>/<c>obj/</c>.
    /// </summary>
    [JsonPropertyName("excludePaths")]
    public List<string> ExcludePaths { get; set; } = new();

    /// <summary>
    /// When <c>true</c>, every external <c>http(s)://...</c> link is HEAD-checked
    /// (with a small concurrency limit). Default <c>false</c> because external links flake
    /// for transient reasons unrelated to the PR under review, and we don't want CI to
    /// catch fire because someone's marketing page is briefly down.
    /// </summary>
    [JsonPropertyName("checkExternalLinks")]
    public bool CheckExternalLinks { get; set; } = false;

    /// <summary>
    /// Anchor IDs to accept even when no matching heading exists in the target file. Use
    /// sparingly — typically for HTML-style <c>&lt;a name="..."&gt;</c> anchors that the
    /// GitHub-flavored slug algorithm doesn't recover from <c>##</c> headings.
    /// </summary>
    [JsonPropertyName("allowedAnchors")]
    public List<string> AllowedAnchors { get; set; } = new();

    /// <summary>
    /// GitHub-style URL prefixes that should be treated as "this repo" and resolved
    /// against the local filesystem (e.g. <c>https://github.com/&lt;owner&gt;/&lt;repo&gt;/blob/main/</c>).
    /// Auto-populated from <c>git remote get-url origin</c> when empty.
    /// </summary>
    [JsonPropertyName("repoUrlPrefixes")]
    public List<string> RepoUrlPrefixes { get; set; } = new();
}
