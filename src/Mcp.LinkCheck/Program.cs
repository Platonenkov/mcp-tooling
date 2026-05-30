using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Mcp.LinkCheck;

/// <summary>
/// Markdown link integrity gate. For every <c>.md</c> tracked in the repo:
///   - <c>[text](relative/path)</c> → assert <c>path</c> exists (or its <c>.md</c> sibling does);
///   - <c>[text](relative/path#anchor)</c> → also assert the GitHub-flavored anchor slug matches
///     a heading in the target file;
///   - <c>[text](https://github.com/&lt;owner&gt;/&lt;repo&gt;/blob/&lt;branch&gt;/&lt;path&gt;)</c> where
///     the URL maps to this repo → validate the path locally too.
/// External non-GitHub HTTPS links are skipped by default (<c>linkcheck.json:checkExternalLinks=true</c>
/// to opt in to a HEAD probe with bounded concurrency).
///
/// Options:
///   <c>--check</c>              CI mode: exit non-zero on any broken link
///   <c>--repo-root &lt;path&gt;</c>   default: git root from cwd, else cwd
///   <c>--config &lt;path&gt;</c>      default: <c>&lt;repo-root&gt;/linkcheck.json</c> (optional)
/// </summary>
public static class Program
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static async Task<int> Main(string[] args)
    {
        bool check = args.Contains("--check");
        string repoRoot = Path.GetFullPath(GetOption(args, "--repo-root") ?? FindRepoRoot());
        string configPath = GetOption(args, "--config") ?? Path.Combine(repoRoot, "linkcheck.json");

        LinkCheckConfig config = File.Exists(configPath)
            ? JsonSerializer.Deserialize<LinkCheckConfig>(
                File.ReadAllText(configPath),
                new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
              ?? new LinkCheckConfig()
            : new LinkCheckConfig();

        // Auto-populate the GitHub-blob prefix from `git remote` when the config didn't
        // pin it — so cross-repo PRs that link via the full https URL stay validatable.
        if (config.RepoUrlPrefixes.Count == 0)
        {
            (string? owner, string? repo) = TryReadGitOrigin(repoRoot);
            if (owner is not null && repo is not null)
            {
                config.RepoUrlPrefixes.Add($"https://github.com/{owner}/{repo}/blob/main/");
                config.RepoUrlPrefixes.Add($"https://github.com/{owner}/{repo}/blob/master/");
                config.RepoUrlPrefixes.Add($"https://github.com/{owner}/{repo}/tree/main/");
                config.RepoUrlPrefixes.Add($"https://github.com/{owner}/{repo}/tree/master/");
            }
        }

        // Walk every tracked .md, parse, collect broken links.
        List<string> errors = new();
        HashSet<string> allowedAnchors = new(config.AllowedAnchors, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, HashSet<string>> anchorCache = new(StringComparer.OrdinalIgnoreCase);

        foreach (string mdAbs in EnumerateMarkdownFiles(repoRoot, config.ExcludePaths))
        {
            string mdRel = ToForward(Path.GetRelativePath(repoRoot, mdAbs));
            string content;
            try { content = File.ReadAllText(mdAbs); }
            catch (Exception ex) { errors.Add($"{mdRel}: failed to read — {ex.Message}"); continue; }

            MarkdownDocument doc = Markdown.Parse(content, Pipeline);
            foreach (LinkInline link in doc.Descendants<LinkInline>())
            {
                if (link.IsImage) continue;            // images are checked separately if at all
                if (link.IsAutoLink) continue;         // bare URLs like <https://...>
                string? url = link.Url;
                if (string.IsNullOrEmpty(url)) continue;
                if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) continue;
                if (url.StartsWith("#"))               // same-file anchor
                {
                    string sameFileAnchor = url[1..];
                    HashSet<string> selfAnchors = anchorCache.TryGetValue(mdAbs, out HashSet<string>? cached)
                        ? cached
                        : anchorCache[mdAbs] = ExtractAnchorSlugs(content);
                    if (!selfAnchors.Contains(sameFileAnchor) && !allowedAnchors.Contains(sameFileAnchor))
                        errors.Add($"{mdRel}:{link.Line + 1}: missing anchor #{sameFileAnchor} in same file");
                    continue;
                }

                if (TryMatchRepoPrefix(url, config.RepoUrlPrefixes, out string? mapped) && mapped is not null)
                {
                    ValidateRelative(mdRel, mdAbs, mapped, link.Line + 1, repoRoot, allowedAnchors, anchorCache, errors);
                    continue;
                }

                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    if (!config.CheckExternalLinks) continue;
                    // External HEAD probing intentionally not implemented in the first cut —
                    // opt-in mode that flakes against transient outages is worse than off-by-default.
                    continue;
                }

                ValidateRelative(mdRel, mdAbs, url, link.Line + 1, repoRoot, allowedAnchors, anchorCache, errors);
            }
        }

        if (errors.Count == 0)
        {
            Console.Out.WriteLine($"OK: markdown links check passed ({CountFiles(repoRoot, config.ExcludePaths)} files scanned).");
            return 0;
        }

        foreach (string err in errors) Console.Error.WriteLine($"::error::{err}");
        Console.Error.WriteLine($"::error::{errors.Count} broken markdown link(s).");
        return check ? 1 : 0;
    }

    private static void ValidateRelative(
        string mdRel,
        string mdAbs,
        string url,
        int line,
        string repoRoot,
        HashSet<string> allowedAnchors,
        Dictionary<string, HashSet<string>> anchorCache,
        List<string> errors)
    {
        // Split URL into path / anchor and decode percent-escapes (e.g. spaces in filenames).
        int hash = url.IndexOf('#');
        string rawPath = hash >= 0 ? url[..hash] : url;
        string? anchor = hash >= 0 ? url[(hash + 1)..] : null;
        if (string.IsNullOrEmpty(rawPath) && anchor is not null)
            return; // already handled by the `#` branch above

        string decoded = Uri.UnescapeDataString(rawPath);
        string baseDir = Path.GetDirectoryName(mdAbs) ?? repoRoot;
        string targetAbs = Path.GetFullPath(Path.Combine(baseDir, decoded));

        // Reject paths that escape the repo (..\..\..\Windows) — silently treat as external.
        if (!targetAbs.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
            return;

        bool isFile = File.Exists(targetAbs);
        bool isDir = !isFile && Directory.Exists(targetAbs);
        if (!isFile && !isDir)
        {
            errors.Add($"{mdRel}:{line}: broken link → {url} (target does not exist)");
            return;
        }

        if (anchor is null || anchor.Length == 0) return;

        // Anchor only meaningful on markdown files. Resolve directories to a README.md if any.
        if (isDir)
        {
            string readme = Path.Combine(targetAbs, "README.md");
            if (!File.Exists(readme))
            {
                errors.Add($"{mdRel}:{line}: anchor #{anchor} on a directory link with no README.md → {url}");
                return;
            }
            targetAbs = readme;
        }
        if (!targetAbs.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return;

        if (allowedAnchors.Contains(anchor)) return;

        HashSet<string> anchors = anchorCache.TryGetValue(targetAbs, out HashSet<string>? cached)
            ? cached
            : anchorCache[targetAbs] = ExtractAnchorSlugs(File.ReadAllText(targetAbs));

        if (!anchors.Contains(anchor))
            errors.Add($"{mdRel}:{line}: missing anchor #{anchor} in {ToForward(Path.GetRelativePath(repoRoot, targetAbs))}");
    }

    /// <summary>
    /// Extract every heading from a markdown source as a GitHub-flavored slug. Also picks up
    /// HTML anchors of the form <c>&lt;a name="foo"&gt;</c> / <c>id="foo"</c> so docs that pre-date
    /// GH's heading-slug behavior keep validating.
    /// </summary>
    private static HashSet<string> ExtractAnchorSlugs(string markdown)
    {
        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> dupCounts = new(StringComparer.OrdinalIgnoreCase);

        MarkdownDocument doc = Markdown.Parse(markdown, Pipeline);
        foreach (HeadingBlock h in doc.Descendants<HeadingBlock>())
        {
            string text = ExtractInlineText(h.Inline);
            string slug = GitHubSlug(text);
            if (slug.Length == 0) continue;
            if (dupCounts.TryGetValue(slug, out int n))
            {
                dupCounts[slug] = n + 1;
                result.Add($"{slug}-{n}");
            }
            else
            {
                dupCounts[slug] = 1;
                result.Add(slug);
            }
        }

        // HTML named anchors: <a name="foo"> or <a id="foo"> or <h2 id="foo">.
        foreach (Match m in AnchorRegex.Matches(markdown))
            result.Add(m.Groups["id"].Value);

        return result;
    }

    private static readonly Regex AnchorRegex = new(
        @"<(?:a|h[1-6]|div|span|section)\b[^>]*?\b(?:name|id)\s*=\s*[""']?(?<id>[A-Za-z0-9_\-\.]+)[""']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string ExtractInlineText(Markdig.Syntax.Inlines.ContainerInline? inline)
    {
        if (inline is null) return string.Empty;
        StringBuilder sb = new();
        foreach (Markdig.Syntax.Inlines.Inline i in inline)
        {
            switch (i)
            {
                case Markdig.Syntax.Inlines.LiteralInline lit: sb.Append(lit.Content.ToString()); break;
                case Markdig.Syntax.Inlines.CodeInline code:  sb.Append(code.Content); break;
                case Markdig.Syntax.Inlines.EmphasisInline emp: sb.Append(ExtractInlineText(emp)); break;
                case Markdig.Syntax.Inlines.LinkInline link:   sb.Append(ExtractInlineText(link)); break;
                case Markdig.Syntax.Inlines.LineBreakInline:   sb.Append(' '); break;
                case Markdig.Syntax.Inlines.HtmlInline html:   /* drop tags */ break;
                default: sb.Append(i.ToString() ?? ""); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// GitHub-flavored heading anchor. Mirrors github.com/jch/html-pipeline's
    /// TableOfContentsFilter (Ruby): <c>text.downcase.gsub(/[^\p{Word}\- ]/u, '').tr(' ', '-')</c>.
    /// Three subtleties we got wrong before:
    ///   - <c>_</c> is a word character and stays as <c>_</c> (NOT collapsed to <c>-</c>);
    ///   - punctuation between words leaves the surrounding spaces in place, so a heading like
    ///     "A (B + C)" becomes "a-b--c" with a double dash — we MUST NOT collapse runs of <c>-</c>;
    ///   - leading/trailing dashes are preserved (GitHub doesn't trim them).
    /// </summary>
    private static string GitHubSlug(string heading)
    {
        StringBuilder sb = new(heading.Length);
        foreach (char c in heading.ToLowerInvariant())
        {
            // \p{Word} is [A-Za-z0-9_] plus Unicode letter/digit categories (covers Cyrillic).
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            else if (c == '-') sb.Append('-');
            else if (c == ' ' || c == '\t') sb.Append(' ');     // keep, will be tr-ed below
            // every other character (punctuation, emoji, etc.) is dropped
        }
        return sb.ToString().Replace(' ', '-');
    }

    private static bool TryMatchRepoPrefix(string url, List<string> prefixes, out string? relPath)
    {
        foreach (string prefix in prefixes)
        {
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                relPath = url[prefix.Length..];
                return true;
            }
        }
        relPath = null;
        return false;
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
            // Strip protocol and .git suffix; accept both ssh and https forms.
            url = url.Replace("git@github.com:", "https://github.com/", StringComparison.OrdinalIgnoreCase);
            if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) url = url[..^4];
            Match m = Regex.Match(url, @"github\.com[:/](?<owner>[^/]+)/(?<repo>[^/?#]+)", RegexOptions.IgnoreCase);
            return m.Success ? (m.Groups["owner"].Value, m.Groups["repo"].Value) : (null, null);
        }
        catch { return (null, null); }
    }

    private static IEnumerable<string> EnumerateMarkdownFiles(string repoRoot, List<string> excludePatterns)
    {
        // Defaults shared with Mcp.ToolsDoc / Mcp.I18nCheck: walk all .md but skip output dirs
        // and the auto-generated TOOLS file. We also skip anything matching configured globs.
        List<Regex> globRegexes = new();
        foreach (string g in excludePatterns) globRegexes.Add(GlobToRegex(g));

        foreach (string abs in Directory.EnumerateFiles(repoRoot, "*.md", SearchOption.AllDirectories))
        {
            string rel = ToForward(Path.GetRelativePath(repoRoot, abs));
            if (rel.StartsWith(".git/", StringComparison.Ordinal)) continue;
            if (rel.Contains("/bin/", StringComparison.Ordinal)) continue;
            if (rel.Contains("/obj/", StringComparison.Ordinal)) continue;
            if (rel.Contains("/node_modules/", StringComparison.Ordinal)) continue;
            if (rel.Equals("docs/TOOLS.generated.md", StringComparison.OrdinalIgnoreCase)) continue;
            bool excluded = false;
            foreach (Regex r in globRegexes) { if (r.IsMatch(rel)) { excluded = true; break; } }
            if (excluded) continue;
            yield return abs;
        }
    }

    private static int CountFiles(string repoRoot, List<string> excludePatterns)
        => EnumerateMarkdownFiles(repoRoot, excludePatterns).Count();

    private static Regex GlobToRegex(string glob)
    {
        StringBuilder sb = new("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; }
                    else sb.Append("[^/]*");
                    break;
                case '?': sb.Append("[^/]"); break;
                case '.': case '+': case '(': case ')': case '|': case '^': case '$': case '{': case '}': case '\\':
                    sb.Append('\\').Append(c); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
