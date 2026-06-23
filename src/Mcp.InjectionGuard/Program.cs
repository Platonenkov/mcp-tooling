using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcp.InjectionGuard;

/// <summary>
/// Roslyn-based prompt-injection defence gate. For every <c>[McpServerTool]</c> method in
/// the consumer repo this tool decides whether the method returns external content
/// (HTTP bodies, JSON from third-party APIs, tool output) — and if so, asserts the
/// returned value is syntactically wrapped through <c>UntrustedContent.Wrap(...)</c> or
/// <c>UntrustedContent.WrapJson(...)</c> before reaching the MCP client.
///
/// Classification rules (in order):
///   1. <c>injectionguard.json:exempt</c> list OR <c>[NotExternalContent]</c> attribute → skip.
///   2. <c>[ExternalContent]</c> attribute → require wrap.
///   3. Heuristic: method-name prefix (<c>Get|Read|Search|List|Find|Fetch|Resolve</c>) AND
///      return-type carries external content (<c>string</c>/<c>JObject</c>/<c>JArray</c>/
///      <c>IReadOnlyList</c>/<c>object</c>), OR method body invokes a known
///      external-content-producing API (<c>*.GetAsync</c>, <c>*.SendRequestAsync</c>,
///      <c>*.ExecuteAsync</c>, …) — both sets extensible via <c>injectionguard.json</c>.
///
/// Per return statement we accept: <c>throw</c>, <c>null</c>, constant literals, returns
/// inside a <c>catch</c> block, returns nested in a lambda / local function (they belong to
/// the inner function, not the tool method), and returns that root in a method parameter.
/// Everything else must syntactically descend into a wrap invocation — directly, via a wrapped
/// local, a wrapped object initializer, a wrapped ternary, or a wrapped null-coalesce.
///
/// Options:
///   <c>--check</c>                 CI mode: exit non-zero on any error.
///   <c>--repo-root &lt;path&gt;</c>      default: cwd.
///   <c>--config &lt;path&gt;</c>         default: <c>injectionguard.json</c> at the repo root if it exists.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static int Main(string[] args)
    {
        bool check = args.Contains("--check");
        string repoRoot = Path.GetFullPath(GetOption(args, "--repo-root") ?? Directory.GetCurrentDirectory());
        string? configPath = GetOption(args, "--config");

        InjectionGuardConfig config = LoadConfig(repoRoot, configPath);

        List<string> files = DiscoverFiles(repoRoot, config.Include);
        if (files.Count == 0)
        {
            Console.Out.WriteLine($"OK: injection-guard — no files matched {string.Join(", ", config.Include)} under {ToForward(Path.GetRelativePath(Directory.GetCurrentDirectory(), repoRoot))}; nothing to check.");
            return 0;
        }

        List<ToolFinding> allFindings = new();
        foreach (string abs in files)
        {
            string text = File.ReadAllText(abs);
            string rel = ToForward(Path.GetRelativePath(repoRoot, abs));
            allFindings.AddRange(RoslynAnalyzer.Scan(abs, text, rel, config));
        }

        int toolCount = allFindings.Count;
        int notExternal = allFindings.Count(f => f.Classification == Classification.NotExternal);
        int exempt = allFindings.Count(f => f.Classification == Classification.Exempt);
        int requireWrap = allFindings.Count(f =>
            f.Classification is Classification.AttributeExternal or Classification.HeuristicExternal);

        Console.Out.WriteLine(
            $"INFO: scanned {toolCount} [McpServerTool] method(s) across {files.Count} file(s) — " +
            $"{requireWrap} classified external-content-returning, {exempt} exempt, {notExternal} not external.");

        List<string> errors = new();
        List<string> warnings = new();

        foreach (ToolFinding finding in allFindings)
        {
            if (finding.Classification is Classification.NotExternal or Classification.Exempt) continue;

            if (finding.UnwrappedReturnLines.Count > 0)
            {
                string lines = string.Join(",", finding.UnwrappedReturnLines);
                string suffix = finding.Classification == Classification.HeuristicExternal
                    ? " (classified by heuristic — silence with [NotExternalContent] / injectionguard.json:exempt if benign)"
                    : "";
                errors.Add(
                    $"{finding.RelativePath}:{finding.Line} method `{finding.MethodName}` is classified as " +
                    $"external-content-returning but its return at line {lines} does not wrap through " +
                    $"UntrustedContent.Wrap / UntrustedContent.WrapJson{suffix}");
                continue;
            }

            if (finding.Classification == Classification.AttributeExternal && finding.EveryReturnIsWrapped)
            {
                // Attribute is honoured — the warning that previously fired here was noise:
                // a tool with [ExternalContent] *should* wrap its returns. Skip it.
            }
        }

        foreach (string err in errors) Console.Error.WriteLine($"::error::{err}");
        foreach (string warn in warnings) Console.Error.WriteLine($"::warning::{warn}");

        if (errors.Count == 0)
        {
            Console.Out.WriteLine($"OK: every external-content-returning tool wraps through UntrustedContent.");
            return 0;
        }

        Console.Error.WriteLine($"FAIL: {errors.Count} method(s) return external content without wrapping through UntrustedContent.");
        return check ? 1 : 0;
    }

    // ---------------------------------------------------------------------------------------
    // File discovery
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves the configured glob patterns into a deduplicated list of absolute file paths.
    /// Supports <c>**</c> (any subtree), <c>*</c> (any segment), and literal segments. Skips
    /// <c>bin/</c>, <c>obj/</c>, <c>.git/</c>, <c>node_modules/</c>.
    /// </summary>
    private static List<string> DiscoverFiles(string repoRoot, IReadOnlyList<string> patterns)
    {
        SortedSet<string> set = new(StringComparer.Ordinal);
        foreach (string pattern in patterns)
        {
            foreach (string abs in MatchGlob(repoRoot, pattern))
            {
                set.Add(abs);
            }
        }
        return set.ToList();
    }

    private static IEnumerable<string> MatchGlob(string repoRoot, string pattern)
    {
        string norm = pattern.Replace('\\', '/');
        Regex re = GlobToRegex(norm);

        if (!Directory.Exists(repoRoot)) yield break;

        foreach (string abs in Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories))
        {
            string rel = ToForward(Path.GetRelativePath(repoRoot, abs));
            if (IsSkippableDir(rel)) continue;
            if (re.IsMatch(rel)) yield return abs;
        }
    }

    private static bool IsSkippableDir(string rel)
    {
        string[] parts = rel.Split('/');
        foreach (string seg in parts)
        {
            if (seg.Equals("bin", StringComparison.OrdinalIgnoreCase)) return true;
            if (seg.Equals("obj", StringComparison.OrdinalIgnoreCase)) return true;
            if (seg.Equals(".git", StringComparison.OrdinalIgnoreCase)) return true;
            if (seg.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Translate a glob (<c>**</c>, <c>*</c>, literal characters) into a compiled regex
    /// anchored at both ends. <c>**</c> matches any number of segments (including zero);
    /// <c>*</c> matches anything except <c>/</c>.
    /// </summary>
    private static Regex GlobToRegex(string glob)
    {
        System.Text.StringBuilder sb = new();
        sb.Append('^');
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // `**/` or `**`
                if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    sb.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    sb.Append(".*");
                    i += 1;
                }
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else if ("\\.+()|^$[]{}".IndexOf(c) >= 0)
            {
                sb.Append('\\').Append(c);
            }
            else
            {
                sb.Append(c);
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Compiled);
    }

    // ---------------------------------------------------------------------------------------
    // Config + utilities
    // ---------------------------------------------------------------------------------------

    private static InjectionGuardConfig LoadConfig(string repoRoot, string? configPath)
    {
        string abs = configPath is not null
            ? Path.GetFullPath(configPath)
            : Path.Combine(repoRoot, "injectionguard.json");

        if (!File.Exists(abs)) return new InjectionGuardConfig();

        try
        {
            InjectionGuardConfig? loaded = JsonSerializer.Deserialize<InjectionGuardConfig>(File.ReadAllText(abs), JsonOptions);
            return loaded ?? new InjectionGuardConfig();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::warning::failed to parse {abs}: {ex.Message} (continuing with defaults).");
            return new InjectionGuardConfig();
        }
    }

    private static string ToForward(string p) => p.Replace('\\', '/');

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
