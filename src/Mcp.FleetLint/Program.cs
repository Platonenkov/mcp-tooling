using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcp.FleetLint;

/// <summary>
/// Cross-repo consistency gate. Each consumer repo runs this against itself; the canonical
/// fleet inventory <c>fleet-lint.json</c> is downloaded from mcp-tooling@main (or read from
/// disk when the tool itself is invoked inside mcp-tooling). The tool identifies the current
/// repo by its git remote and runs the per-repo checks; if the repo isn't in the inventory
/// (e.g. mcp-auth, mcp-tooling itself, or a non-fleet repo using the tool), every check is
/// a no-op and the run exits clean.
///
/// Checks (all opt-out via <c>fleet-lint.json</c> entries):
///   - <b>Hostname consistency</b>: every <c>*.staticbit.io</c> string in committed files
///     must match this repo's own canonical host, the AS host, or another known fleet host.
///   - <b>callbackPort</b>: plugin <c>.mcp.json</c> manifests' <c>oauth.callbackPort</c>
///     must match the repo's entry.
///   - <b>OAuth scope</b>: <c>appsettings.json</c> <c>OAuth.RequiredScope</c> must match.
///   - <b>AS hostname</b>: any <c>auth.mcp.*</c> reference must be the canonical AS host.
///
/// Options:
///   <c>--check</c>           CI mode: exit non-zero on any issue
///   <c>--repo-root &lt;path&gt;</c>  default: git root from cwd
///   <c>--config &lt;path&gt;</c>     local fleet-lint.json (overrides remote download)
///   <c>--config-url &lt;url&gt;</c>  override the canonical remote URL
/// </summary>
public static class Program
{
    private const string DefaultConfigUrl =
        "https://raw.githubusercontent.com/Platonenkov/mcp-tooling/main/fleet-lint.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static async Task<int> Main(string[] args)
    {
        bool check = args.Contains("--check");
        string repoRoot = Path.GetFullPath(GetOption(args, "--repo-root") ?? FindRepoRoot());
        string? configPath = GetOption(args, "--config");
        string configUrl = GetOption(args, "--config-url") ?? DefaultConfigUrl;

        FleetConfig? config = await LoadConfigAsync(repoRoot, configPath, configUrl);
        if (config is null)
        {
            Console.Error.WriteLine("::error::fleet-lint.json not found locally or remotely; cannot run checks.");
            return check ? 1 : 0;
        }

        (string? owner, string? repo) = TryReadGitOrigin(repoRoot);
        string? identity = owner is not null && repo is not null ? $"{owner}/{repo}" : null;

        McpEntry? selfEntry = identity is null
            ? null
            : config.Mcps.FirstOrDefault(m => string.Equals(m.Repo, identity, StringComparison.OrdinalIgnoreCase));

        if (selfEntry is null)
        {
            Console.Out.WriteLine($"OK: fleet-lint — this repo ({identity ?? "<unknown>"}) is not in the fleet inventory; nothing to check.");
            return 0;
        }

        Console.Out.WriteLine($"fleet-lint: checking {identity} (scope={selfEntry.Scope}, host={selfEntry.Host}, callbackPort={selfEntry.CallbackPort}) against fleet of {config.Mcps.Count} MCP(s).");

        List<string> errors = new();

        CheckHostnames(repoRoot, config, selfEntry, errors);
        CheckCallbackPort(repoRoot, selfEntry, errors);
        CheckOAuthScope(repoRoot, selfEntry, errors);
        CheckAuthServerHost(repoRoot, config, errors);
        CheckReleaseModel(repoRoot, selfEntry, errors);

        if (errors.Count == 0)
        {
            Console.Out.WriteLine("OK: fleet-lint passed (5 invariant classes).");
            return 0;
        }

        foreach (string err in errors) Console.Error.WriteLine($"::error::{err}");
        Console.Error.WriteLine($"::error::{errors.Count} fleet-lint issue(s).");
        return check ? 1 : 0;
    }

    // ---------------------------------------------------------------------------------------
    // Check 1: Hostname consistency. Every *.staticbit.io string in text files should be one
    // of: self host, AS host, other fleet host, or an explicitly allowed extra (config).
    // Anything else is a typo or stale doc reference to a decommissioned hostname.
    // ---------------------------------------------------------------------------------------
    private static readonly Regex HostnameRegex = new(
        @"\b(?<host>[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)*\.staticbit\.io)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void CheckHostnames(string repoRoot, FleetConfig config, McpEntry self, List<string> errors)
    {
        HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase) { config.AuthorizationServer };
        foreach (McpEntry e in config.Mcps) allowed.Add(e.Host);
        foreach (string h in config.AllowedExtraHostnames) allowed.Add(h);

        Dictionary<string, List<(string Path, int Line)>> hits = new(StringComparer.OrdinalIgnoreCase);
        foreach (string abs in EnumerateScannableFiles(repoRoot))
        {
            int lineNum = 0;
            foreach (string line in File.ReadLines(abs))
            {
                lineNum++;
                foreach (Match m in HostnameRegex.Matches(line))
                {
                    string host = m.Groups["host"].Value.ToLowerInvariant();
                    if (allowed.Contains(host)) continue;
                    if (!hits.TryGetValue(host, out List<(string, int)>? list))
                        hits[host] = list = new();
                    list.Add((ToForward(Path.GetRelativePath(repoRoot, abs)), lineNum));
                }
            }
        }

        foreach ((string host, List<(string Path, int Line)> occurrences) in hits)
        {
            // Suggest the closest known hostname (Levenshtein) so the error is actionable.
            string? suggestion = SuggestClosest(host, allowed, threshold: 3);
            string suggestionTxt = suggestion is null ? "" : $" — did you mean {suggestion}?";
            foreach ((string path, int line) in occurrences.Take(3))
                errors.Add($"{path}:{line}: unknown staticbit.io hostname \"{host}\"{suggestionTxt}");
            if (occurrences.Count > 3)
                errors.Add($"… and {occurrences.Count - 3} more occurrences of \"{host}\"");
        }
    }

    // ---------------------------------------------------------------------------------------
    // Check 2: OAuth callback port in .mcp.json plugin manifests must equal the canonical
    // value for the repo. Detects accidental copy-paste collisions when adding a new plugin.
    // ---------------------------------------------------------------------------------------
    private static void CheckCallbackPort(string repoRoot, McpEntry self, List<string> errors)
    {
        foreach (string abs in Directory.EnumerateFiles(repoRoot, ".mcp.json", SearchOption.AllDirectories))
        {
            string rel = ToForward(Path.GetRelativePath(repoRoot, abs));
            if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal))
                continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(abs), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
            catch (Exception ex) { errors.Add($"{rel}: failed to parse — {ex.Message}"); continue; }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("oauth", out JsonElement oauth)) continue;
                if (!oauth.TryGetProperty("callbackPort", out JsonElement portEl)) continue;
                if (portEl.ValueKind != JsonValueKind.Number) continue;
                int port = portEl.GetInt32();
                if (port != self.CallbackPort)
                    errors.Add($"{rel}: oauth.callbackPort={port} but fleet inventory says {self.CallbackPort} for {self.Repo}");
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Check 3: OAuth scope. Walks every appsettings*.json (the cloud server config) for the
    // OAuth section's RequiredScope; must match entry.scope.
    // ---------------------------------------------------------------------------------------
    private static void CheckOAuthScope(string repoRoot, McpEntry self, List<string> errors)
    {
        foreach (string abs in Directory.EnumerateFiles(repoRoot, "appsettings*.json", SearchOption.AllDirectories))
        {
            string rel = ToForward(Path.GetRelativePath(repoRoot, abs));
            if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal))
                continue;
            if (rel.Contains("appsettings.Local", StringComparison.OrdinalIgnoreCase)) continue; // gitignored local overrides

            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(abs), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
            catch { continue; }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("OAuth", out JsonElement oauth)) continue;
                if (!oauth.TryGetProperty("RequiredScope", out JsonElement scopeEl)) continue;
                if (scopeEl.ValueKind != JsonValueKind.String) continue;
                string scope = scopeEl.GetString() ?? "";
                if (!string.Equals(scope, self.Scope, StringComparison.Ordinal))
                    errors.Add($"{rel}: OAuth.RequiredScope=\"{scope}\" but fleet inventory says \"{self.Scope}\"");
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Check 4: AS hostname. Any auth.mcp.* reference must be exactly the canonical AS host.
    // Catches typos like `auth.mcp.staticbiit.io` or stale aliases.
    // ---------------------------------------------------------------------------------------
    private static readonly Regex AuthHostRegex = new(
        @"\bauth(?:\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)+\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void CheckAuthServerHost(string repoRoot, FleetConfig config, List<string> errors)
    {
        foreach (string abs in EnumerateScannableFiles(repoRoot))
        {
            int lineNum = 0;
            foreach (string line in File.ReadLines(abs))
            {
                lineNum++;
                foreach (Match m in AuthHostRegex.Matches(line))
                {
                    string host = m.Value.ToLowerInvariant();
                    if (string.Equals(host, config.AuthorizationServer, StringComparison.OrdinalIgnoreCase)) continue;
                    // Levenshtein-based typo detection: only flag `auth.*` hostnames suspiciously
                    // close to the canonical AS — leaves legitimate third-party `auth.example.com`
                    // references alone, but catches `auth.mcp.staticbiit.io`, `auth.staticbit.io`
                    // (missing `.mcp`), `auth.mcp.staticbi.io`, etc.
                    int dist = Levenshtein(host, config.AuthorizationServer);
                    if (dist > 3) continue;
                    string rel = ToForward(Path.GetRelativePath(repoRoot, abs));
                    errors.Add($"{rel}:{lineNum}: authorization-server hostname \"{host}\" differs from canonical \"{config.AuthorizationServer}\" (edit distance {dist}) — typo?");
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------
    private static async Task<FleetConfig?> LoadConfigAsync(string repoRoot, string? configPath, string configUrl)
    {
        if (configPath is not null)
        {
            string abs = Path.GetFullPath(configPath);
            if (!File.Exists(abs)) return null;
            return JsonSerializer.Deserialize<FleetConfig>(File.ReadAllText(abs), JsonOptions);
        }
        string local = Path.Combine(repoRoot, "fleet-lint.json");
        if (File.Exists(local))
            return JsonSerializer.Deserialize<FleetConfig>(File.ReadAllText(local), JsonOptions);

        try
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
            string body = await http.GetStringAsync(configUrl);
            return JsonSerializer.Deserialize<FleetConfig>(body, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::failed to download fleet-lint.json from {configUrl}: {ex.Message}");
            return null;
        }
    }

    private static IEnumerable<string> EnumerateScannableFiles(string repoRoot)
    {
        // Text-only file types that legitimately contain hostnames/scopes. We deliberately skip
        // .md (link-check already covers those) for hostname-typo speed, EXCEPT explicit doc
        // sources where ops engineers type hostnames into command snippets.
        string[] exts = [".cs", ".json", ".yml", ".yaml", ".sh", ".ps1", ".cmd", ".env", ".example", ".md", ".dockerfile", ".props"];
        foreach (string abs in Directory.EnumerateFiles(repoRoot, "*", SearchOption.AllDirectories))
        {
            string rel = ToForward(Path.GetRelativePath(repoRoot, abs));
            if (rel.StartsWith(".git/", StringComparison.Ordinal)) continue;
            if (rel.Contains("/bin/", StringComparison.Ordinal)) continue;
            if (rel.Contains("/obj/", StringComparison.Ordinal)) continue;
            if (rel.Contains("/node_modules/", StringComparison.Ordinal)) continue;
            if (rel.Equals("docs/TOOLS.generated.md", StringComparison.OrdinalIgnoreCase)) continue;
            string name = Path.GetFileName(rel);
            string ext = Path.GetExtension(rel);
            bool isText = exts.Contains(ext, StringComparer.OrdinalIgnoreCase)
                || name.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase);
            if (!isText) continue;
            yield return abs;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Check 5: Release model consistency. For repos declared "per-plugin" in fleet-lint.json
    // (the default), assert that the per-plugin release machinery is in place — `release-plugin.yml`
    // workflow, root `RELEASE.md` doc, and at least one `plugins/<plugin>/CHANGELOG.md`. Catches
    // drift where a multi-plugin repo accidentally falls back to a monorepo-style release without
    // updating its release model declaration.
    // ---------------------------------------------------------------------------------------
    private static void CheckReleaseModel(string repoRoot, McpEntry self, List<string> errors)
    {
        string model = string.IsNullOrEmpty(self.ReleaseModel) ? "per-plugin" : self.ReleaseModel;
        if (!string.Equals(model, "per-plugin", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string releasePluginYml = Path.Combine(repoRoot, ".github", "workflows", "release-plugin.yml");
        if (!File.Exists(releasePluginYml))
        {
            errors.Add(".github/workflows/release-plugin.yml: missing — required for releaseModel=\"per-plugin\" (set releaseModel=\"shared\" in fleet-lint.json if the shared-version convention is intentional)");
        }

        string releaseMd = Path.Combine(repoRoot, "RELEASE.md");
        if (!File.Exists(releaseMd))
        {
            errors.Add("RELEASE.md: missing — required for releaseModel=\"per-plugin\" (documents the per-plugin tag procedure)");
        }

        string pluginsDir = Path.Combine(repoRoot, "plugins");
        if (Directory.Exists(pluginsDir))
        {
            bool anyPerPluginChangelog = Directory.EnumerateFiles(pluginsDir, "CHANGELOG.md", SearchOption.AllDirectories).Any();
            if (!anyPerPluginChangelog)
            {
                errors.Add("plugins/*/CHANGELOG.md: no per-plugin CHANGELOG.md found — required for releaseModel=\"per-plugin\" (each plugin needs its own changelog so release-plugin.sh can prepend entries cleanly)");
            }
        }
    }

    private static string? SuggestClosest(string input, IEnumerable<string> candidates, int threshold)
    {
        string? best = null;
        int bestDist = int.MaxValue;
        foreach (string c in candidates)
        {
            int d = Levenshtein(input, c);
            if (d < bestDist) { bestDist = d; best = c; }
        }
        return bestDist <= threshold ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        return d[a.Length, b.Length];
    }

    private static (string? owner, string? repo) TryReadGitOrigin(string repoRoot)
    {
        try
        {
            ProcessStartInfo psi = new("git", "remote get-url origin")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process? p = Process.Start(psi);
            if (p is null) return (null, null);
            string url = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            url = url.Replace("git@github.com:", "https://github.com/", StringComparison.OrdinalIgnoreCase);
            if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) url = url[..^4];
            Match m = Regex.Match(url, @"github\.com[:/](?<owner>[^/]+)/(?<repo>[^/?#]+)", RegexOptions.IgnoreCase);
            return m.Success ? (m.Groups["owner"].Value, m.Groups["repo"].Value) : (null, null);
        }
        catch { return (null, null); }
    }

    private static string ToForward(string p) => p.Replace('\\', '/');

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            string gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath)) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
