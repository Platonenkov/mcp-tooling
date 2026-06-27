using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace Mcp.SkillLint;

/// <summary>
/// Cross-plugin SKILL.md trigger-keyword overlap detector. Each plugin in the fleet ships a
/// <c>plugins/&lt;plugin&gt;/skills/&lt;skill-name&gt;/SKILL.md</c> with a YAML-frontmatter
/// <c>description</c> field that lists the natural-language phrases the Claude Code plugin
/// loader uses for relevance scoring. Two plugins in the same repo (especially cloud-vs-local
/// pairs) can accidentally share trigger phrases, which causes the agent to load the wrong
/// plugin (or both) for a given query.
///
/// The tool walks <c>plugins/*/skills/*/SKILL.md</c> under <c>--repo-root</c>, extracts every
/// quoted trigger phrase from the frontmatter <c>description</c>, then reports:
///   - <b>conflicts</b>: the exact same trigger (case-insensitive) in two or more SKILL.md
///     files, unless whitelisted in <c>skilllint.json</c> &gt; <c>sharedTriggers</c>.
///   - <b>near-overlaps</b>: Levenshtein distance ≤ N (default 2) between triggers in
///     different plugins — a warning, not a failure (gives "did you mean to share?" hint).
///
/// Options:
///   <c>--check</c>                  CI mode: exit non-zero on any conflict (warnings never fail).
///   <c>--repo-root &lt;path&gt;</c>       default: cwd.
///   <c>--config &lt;path&gt;</c>          default: <c>skilllint.json</c> at the repo root if it exists.
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

        SkillLintConfig config = LoadConfig(repoRoot, configPath);

        List<SkillPath> files = EnumerateSkillFiles(repoRoot, config);
        if (files.Count == 0)
        {
            Console.Out.WriteLine($"OK: skill-lint — no SKILL.md files found under {ToForward(Path.GetRelativePath(Directory.GetCurrentDirectory(), repoRoot))}/plugins; nothing to check.");
            return 0;
        }

        int pluginCount = files.Select(f => f.PluginName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Console.Out.WriteLine($"INFO: scanned {files.Count} SKILL.md file(s) across {pluginCount} plugin(s).");

        // Pass 1 — frontmatter validity. Every SKILL.md must have a leading `---`...`---` block that
        // parses as YAML with a non-empty `name` + `description`. A malformed frontmatter (e.g. an
        // unquoted `description:` whose value contains a colon-space, which YAML reads as a nested
        // mapping) loads the skill with EMPTY metadata — name/description/triggers silently dropped.
        // This is a hard error, independent of trigger overlap.
        List<string> frontmatterErrors = ValidateFrontmatter(files);

        // Pass 2 — cross-plugin trigger overlap, over the files whose description we could read.
        List<SkillFile> skills = new();
        foreach (SkillPath f in files)
        {
            SkillFile? parsed = ParseSkillFile(f.AbsolutePath, repoRoot, f.PluginName, f.SkillName);
            if (parsed is not null) skills.Add(parsed);
        }
        List<string> conflicts = FindConflicts(skills, config);
        List<string> nearOverlaps = FindNearOverlaps(skills, config);

        foreach (string err in frontmatterErrors) Console.Error.WriteLine($"::error::{err}");
        foreach (string err in conflicts) Console.Error.WriteLine($"::error::{err}");
        foreach (string warn in nearOverlaps) Console.Error.WriteLine($"::warning::{warn}");

        int hardErrors = frontmatterErrors.Count + conflicts.Count;
        if (hardErrors == 0 && nearOverlaps.Count == 0)
        {
            Console.Out.WriteLine("OK: skill frontmatter valid; triggers consistent.");
            return 0;
        }

        if (hardErrors == 0)
        {
            Console.Out.WriteLine($"OK: skill frontmatter valid; triggers consistent ({nearOverlaps.Count} near-overlap warning(s) — review but not blocking).");
            return 0;
        }

        Console.Error.WriteLine($"FAIL: {frontmatterErrors.Count} frontmatter error(s), {conflicts.Count} conflict(s), {nearOverlaps.Count} near-overlap(s).");
        return check ? 1 : 0;
    }

    // ---------------------------------------------------------------------------------------
    // SKILL.md discovery + parsing
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Walks <c>plugins/&lt;plugin&gt;/skills/&lt;skill&gt;/SKILL.md</c> under repo root. The
    /// plugin and skill names are derived from path segments; this is the convention used
    /// across the fleet (telegram-mcp, staticbit-xrpl-mcp, x-mcp, XrplMeta.Mcp).
    /// </summary>
    private static List<SkillPath> EnumerateSkillFiles(string repoRoot, SkillLintConfig config)
    {
        List<SkillPath> result = new();
        string pluginsRoot = Path.Combine(repoRoot, "plugins");
        if (!Directory.Exists(pluginsRoot)) return result;

        HashSet<string> excludePlugins = new(config.ExcludePlugins, StringComparer.OrdinalIgnoreCase);

        foreach (string skillMd in Directory.EnumerateFiles(pluginsRoot, "SKILL.md", SearchOption.AllDirectories))
        {
            string rel = ToForward(Path.GetRelativePath(repoRoot, skillMd));
            string[] parts = rel.Split('/');
            // Expected shape: plugins/<plugin>/skills/<skill>/SKILL.md → 5 segments.
            if (parts.Length < 5) continue;
            if (!parts[0].Equals("plugins", StringComparison.OrdinalIgnoreCase)) continue;
            if (!parts[2].Equals("skills", StringComparison.OrdinalIgnoreCase)) continue;

            string pluginName = parts[1];
            if (excludePlugins.Contains(pluginName)) continue;

            result.Add(new SkillPath(skillMd, rel, pluginName, parts[3]));
        }

        return result;
    }

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder().Build();

    /// <summary>
    /// Frontmatter-validity pass: every SKILL.md must open with a <c>---</c>…<c>---</c> block that
    /// parses as a YAML mapping carrying a non-empty <c>name</c> and <c>description</c>. Returns one
    /// error string per offending file (empty = all valid). This mirrors what <c>claude plugin
    /// validate</c> enforces — a broken frontmatter makes the skill load with empty metadata.
    /// </summary>
    private static List<string> ValidateFrontmatter(List<SkillPath> files)
    {
        List<string> errors = new();
        foreach (SkillPath f in files)
        {
            string text = File.ReadAllText(f.AbsolutePath, Encoding.UTF8);
            Match fm = FrontmatterRegex.Match(text);
            if (!fm.Success)
            {
                errors.Add($"{f.RelativePath}:1 missing or unterminated YAML frontmatter (--- ... ---)");
                continue;
            }

            object? root;
            try
            {
                root = YamlDeserializer.Deserialize<object?>(fm.Groups["body"].Value);
            }
            catch (YamlException ex)
            {
                string detail = string.Join(" ", (ex.Message ?? "parse error").Split('\n', '\r')).Trim();
                errors.Add($"{f.RelativePath} invalid YAML frontmatter — {detail} (skill would load with empty metadata)");
                continue;
            }

            if (root is not IDictionary<object, object> map)
            {
                errors.Add($"{f.RelativePath} frontmatter is not a YAML mapping");
                continue;
            }

            foreach (string key in new[] { "name", "description" })
            {
                map.TryGetValue(key, out object? value);
                if (value is not string s || string.IsNullOrWhiteSpace(s))
                    errors.Add($"{f.RelativePath} frontmatter '{key}' is missing or empty");
            }
        }

        return errors;
    }

    private static readonly Regex FrontmatterRegex = new(
        @"\A---\r?\n(?<body>.*?)\r?\n---\r?\n",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DescriptionFieldRegex = new(
        @"^description\s*:\s*(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <summary>
    /// Trigger phrases inside the description are emitted as double-quoted strings. The
    /// SKILL.md format used across the fleet quotes every phrase literally:
    ///     description: ... Recognizes phrases like "send telegram message", "post to channel", ...
    /// Matches a straight double-quoted run that does not itself contain a double quote — good
    /// enough for the well-formed strings the fleet authors hand-write.
    /// </summary>
    private static readonly Regex QuotedTriggerRegex = new(
        "\"(?<phrase>[^\"\\r\\n]{1,300})\"",
        RegexOptions.Compiled);

    private static SkillFile? ParseSkillFile(string absPath, string repoRoot, string pluginName, string skillName)
    {
        string text = File.ReadAllText(absPath, Encoding.UTF8);
        Match fm = FrontmatterRegex.Match(text);
        if (!fm.Success) return null;

        string frontmatter = fm.Groups["body"].Value;
        // Compute frontmatter line offset (1-based first frontmatter content line in the file).
        int frontmatterStartLine = CountLines(text, fm.Groups["body"].Index) + 1;

        // Locate the `description:` field. YAML allows multi-line values via simple unfolding;
        // SKILL.md authors stick to a single (very long) line, so we capture the description
        // payload as the substring starting at the field value through the next field-start
        // (a line beginning with `<word>:` at column 0) or end-of-frontmatter.
        Match desc = DescriptionFieldRegex.Match(frontmatter);
        if (!desc.Success) return null;

        int descStart = desc.Index;
        int descLineInFrontmatter = CountLines(frontmatter, descStart);
        int descLine = frontmatterStartLine + descLineInFrontmatter;

        // Grab everything from the description value to the next field or end.
        string after = frontmatter[descStart..];
        Match nextField = Regex.Match(after, @"^(?<key>[A-Za-z_][A-Za-z0-9_-]*)\s*:", RegexOptions.Multiline);
        string descPayload = nextField.Success && nextField.Index > 0
            ? after[..nextField.Index]
            : after;

        List<TriggerOccurrence> triggers = new();
        foreach (Match m in QuotedTriggerRegex.Matches(descPayload))
        {
            string phrase = m.Groups["phrase"].Value.Trim();
            if (phrase.Length == 0) continue;
            // Strip trailing punctuation/ellipsis that authors put inside quotes for prose-flow.
            phrase = phrase.TrimEnd('.', ',', ';', '!', '?', ' ');
            if (phrase.Length == 0) continue;

            int lineOffsetInPayload = CountLines(descPayload, m.Index);
            int absoluteLine = descLine + lineOffsetInPayload;
            triggers.Add(new TriggerOccurrence(phrase, absoluteLine));
        }

        string relPath = ToForward(Path.GetRelativePath(repoRoot, absPath));
        return new SkillFile(relPath, pluginName, skillName, descLine, triggers);
    }

    private static int CountLines(string text, int upToIndex)
    {
        if (upToIndex <= 0) return 0;
        int count = 0;
        for (int i = 0; i < upToIndex && i < text.Length; i++)
            if (text[i] == '\n') count++;
        return count;
    }

    // ---------------------------------------------------------------------------------------
    // Conflict detection
    // ---------------------------------------------------------------------------------------

    private static List<string> FindConflicts(List<SkillFile> skills, SkillLintConfig config)
    {
        // Bucket every trigger by its normalized form (case-insensitive, whitespace-collapsed).
        Dictionary<string, List<(SkillFile File, TriggerOccurrence Occ)>> buckets =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (SkillFile file in skills)
            foreach (TriggerOccurrence occ in file.Triggers)
            {
                string key = NormalizeTrigger(occ.Phrase);
                if (!buckets.TryGetValue(key, out List<(SkillFile, TriggerOccurrence)>? list))
                    buckets[key] = list = new();
                list.Add((file, occ));
            }

        List<string> errors = new();
        foreach ((string normalized, List<(SkillFile File, TriggerOccurrence Occ)> hits) in buckets)
        {
            // Find every distinct plugin holding this trigger.
            HashSet<string> plugins = new(hits.Select(h => h.File.PluginName), StringComparer.OrdinalIgnoreCase);
            if (plugins.Count < 2) continue; // same plugin can repeat its own trigger across skills — not a conflict

            if (IsWhitelisted(normalized, plugins, config)) continue;

            // Build a stable ordering for reporting (alphabetic by path).
            List<(SkillFile File, TriggerOccurrence Occ)> ordered = hits
                .OrderBy(h => h.File.RelativePath, StringComparer.Ordinal)
                .ThenBy(h => h.Occ.Line)
                .ToList();

            (SkillFile primary, TriggerOccurrence primaryOcc) = ordered[0];
            for (int i = 1; i < ordered.Count; i++)
            {
                (SkillFile other, TriggerOccurrence otherOcc) = ordered[i];
                if (string.Equals(primary.PluginName, other.PluginName, StringComparison.OrdinalIgnoreCase))
                    continue; // already reported intra-plugin against the primary
                string phrase = ordered[0].Occ.Phrase;
                errors.Add(
                    $"{primary.RelativePath}:{primaryOcc.Line} trigger \"{phrase}\" also appears in " +
                    $"{other.RelativePath}:{otherOcc.Line} (and is not whitelisted in skilllint.json)");
            }
        }

        return errors;
    }

    private static bool IsWhitelisted(string normalizedTrigger, HashSet<string> plugins, SkillLintConfig config)
    {
        foreach (SharedTrigger entry in config.SharedTriggers)
        {
            if (!string.Equals(NormalizeTrigger(entry.Trigger), normalizedTrigger, StringComparison.OrdinalIgnoreCase))
                continue;

            HashSet<string> allowed = new(entry.Plugins, StringComparer.OrdinalIgnoreCase);
            // Every plugin currently sharing the trigger must be in the whitelist set for
            // the entry to apply. A plugin holding the trigger that the entry doesn't list
            // still gets flagged — keeps the whitelist honest.
            if (plugins.IsSubsetOf(allowed)) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------------------------------
    // Near-overlap (Levenshtein) warnings
    // ---------------------------------------------------------------------------------------

    private static List<string> FindNearOverlaps(List<SkillFile> skills, SkillLintConfig config)
    {
        List<string> warnings = new();
        // Flat list of (file, occurrence, normalized) — but only across distinct plugins.
        List<(SkillFile File, TriggerOccurrence Occ, string Norm)> flat = new();
        foreach (SkillFile file in skills)
            foreach (TriggerOccurrence occ in file.Triggers)
            {
                string norm = NormalizeTrigger(occ.Phrase);
                if (norm.Length < config.NearOverlapMinLength) continue;
                flat.Add((file, occ, norm));
            }

        // Avoid duplicate suggestions: track already-reported (norm-A, norm-B) pairs.
        HashSet<string> reportedPairs = new(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < flat.Count; i++)
            for (int j = i + 1; j < flat.Count; j++)
            {
                (SkillFile fa, TriggerOccurrence oa, string na) = flat[i];
                (SkillFile fb, TriggerOccurrence ob, string nb) = flat[j];
                if (string.Equals(fa.PluginName, fb.PluginName, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(na, nb, StringComparison.OrdinalIgnoreCase)) continue; // exact conflict handled elsewhere

                int dist = Levenshtein(na, nb);
                if (dist == 0 || dist > config.NearOverlapDistance) continue;

                string pairKey = string.CompareOrdinal(na, nb) < 0 ? $"{na}|{nb}" : $"{nb}|{na}";
                if (!reportedPairs.Add(pairKey)) continue;

                warnings.Add(
                    $"trigger \"{oa.Phrase}\" in {fa.RelativePath}:{oa.Line} is very similar to " +
                    $"\"{ob.Phrase}\" in {fb.RelativePath}:{ob.Line} (edit distance {dist}) — did you mean to share?");
            }

        return warnings;
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static string NormalizeTrigger(string phrase)
    {
        string trimmed = phrase.Trim().ToLowerInvariant();
        return WhitespaceRegex.Replace(trimmed, " ");
    }

    private static SkillLintConfig LoadConfig(string repoRoot, string? configPath)
    {
        string abs = configPath is not null
            ? Path.GetFullPath(configPath)
            : Path.Combine(repoRoot, "skilllint.json");

        if (!File.Exists(abs)) return new SkillLintConfig();

        try
        {
            SkillLintConfig? loaded = JsonSerializer.Deserialize<SkillLintConfig>(File.ReadAllText(abs), JsonOptions);
            return loaded ?? new SkillLintConfig();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::warning::failed to parse {abs}: {ex.Message} (continuing with defaults).");
            return new SkillLintConfig();
        }
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

    private static string ToForward(string p) => p.Replace('\\', '/');

    private static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}

internal sealed record SkillPath(string AbsolutePath, string RelativePath, string PluginName, string SkillName);

internal sealed record SkillFile(
    string RelativePath,
    string PluginName,
    string SkillName,
    int DescriptionLine,
    List<TriggerOccurrence> Triggers);

internal sealed record TriggerOccurrence(string Phrase, int Line);
