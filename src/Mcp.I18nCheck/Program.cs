namespace Mcp.I18nCheck;

/// <summary>
/// Bilingual-docs CI gate (the C# port of the shared <c>check-translations.sh</c>). English is
/// canonical; every English doc must have its Russian counterpart and the Russian file must be
/// non-stub (≥ <see cref="MinBytes"/> bytes):
///   - <c>docs/&lt;path&gt;.md</c> ↔ <c>docs/ru/&lt;path&gt;.md</c> (mirror subtree).
///   - <c>&lt;dir&gt;/X.md</c> ↔ <c>&lt;dir&gt;/X.ru.md</c> (suffix) for the repo root, <c>plugins/*</c>,
///     <c>examples/**</c>, <c>servers/*</c>, <c>infra/</c>.
/// English-only / generated / agent files are exempted via a repo-root <c>.i18nignore</c>;
/// pairs outside the conventional locations go in <c>.i18npairs</c> (<c>en:ru</c> per line).
/// Options: <c>--repo-root &lt;path&gt;</c> (default: git root from cwd, else cwd).
/// Exit 0 = all good; 1 = missing/stub/orphan.
/// </summary>
public static class Program
{
    private const int MinBytes = 200;

    public static int Main(string[] args)
    {
        string repoRoot = GetOption(args, "--repo-root") ?? FindRepoRoot();
        repoRoot = Path.GetFullPath(repoRoot);

        HashSet<string> ignore = ReadIgnore(Path.Combine(repoRoot, ".i18nignore"));
        int errors = 0;
        void Err(string m) { Console.Error.WriteLine($"::error::{m}"); errors++; }

        void CheckNonStub(string ruFull, string ruRel)
        {
            if (File.Exists(ruFull) && new FileInfo(ruFull).Length < MinBytes)
                Err($"RU translation looks like a stub ({new FileInfo(ruFull).Length}b < {MinBytes}b): {ruRel}");
        }

        // 1) docs/ mirror.
        string docs = Path.Combine(repoRoot, "docs");
        if (Directory.Exists(docs))
        {
            foreach (string en in Directory.EnumerateFiles(docs, "*.md", SearchOption.AllDirectories))
            {
                string rel = Rel(repoRoot, en);
                if (rel.StartsWith("docs/ru/", StringComparison.Ordinal)) continue;
                if (ignore.Contains(rel)) continue;
                string ruRel = "docs/ru/" + rel["docs/".Length..];
                string ruFull = Path.Combine(repoRoot, NormSep(ruRel));
                if (!File.Exists(ruFull)) Err($"missing RU mirror: {rel} -> {ruRel}");
                CheckNonStub(ruFull, ruRel);
            }
            string docsRu = Path.Combine(docs, "ru");
            if (Directory.Exists(docsRu))
            {
                foreach (string ru in Directory.EnumerateFiles(docsRu, "*.md", SearchOption.AllDirectories))
                {
                    string ruRel = Rel(repoRoot, ru);
                    string enRel = "docs/" + ruRel["docs/ru/".Length..];
                    if (ignore.Contains(enRel)) continue;
                    if (!File.Exists(Path.Combine(repoRoot, NormSep(enRel))))
                        Err($"orphan RU mirror (no English source): {ruRel} -> {enRel}");
                }
            }
        }

        // 2) suffix pairs outside docs/.
        foreach (string en in SuffixCandidates(repoRoot))
        {
            string rel = Rel(repoRoot, en);
            if (rel.EndsWith(".ru.md", StringComparison.Ordinal)) continue;
            if (ignore.Contains(rel)) continue;
            string ruRel = rel[..^3] + ".ru.md";
            string ruFull = Path.Combine(repoRoot, NormSep(ruRel));
            if (!File.Exists(ruFull)) Err($"missing RU sibling: {rel} -> {ruRel}");
            CheckNonStub(ruFull, ruRel);
        }

        // 3) explicit extra pairs (.i18npairs).
        string pairsFile = Path.Combine(repoRoot, ".i18npairs");
        if (File.Exists(pairsFile))
        {
            foreach (string raw in File.ReadAllLines(pairsFile))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || !line.Contains(':')) continue;
                string en = line[..line.IndexOf(':')].Trim();
                string ru = line[(line.IndexOf(':') + 1)..].Trim();
                if (!File.Exists(Path.Combine(repoRoot, NormSep(en)))) { Err($"explicit pair references missing English file: {en}"); continue; }
                string ruFull = Path.Combine(repoRoot, NormSep(ru));
                if (!File.Exists(ruFull)) Err($"missing RU (explicit pair): {en} -> {ru}");
                CheckNonStub(ruFull, ru);
            }
        }

        if (errors > 0)
        {
            Console.Error.WriteLine($"::error::{errors} bilingual-docs issue(s). English is canonical; add the Russian counterpart (docs/ru/<path>.md or <name>.ru.md), or list English-only docs in .i18nignore.");
            return 1;
        }
        Console.WriteLine("OK: bilingual docs check passed.");
        return 0;
    }

    /// <summary>Files matching the suffix-pair globs: root *.md, plugins/*/*.md,
    /// examples/*/*.md, examples/*/*/*.md, servers/*/*.md, infra/*.md.</summary>
    private static IEnumerable<string> SuffixCandidates(string root)
    {
        foreach (string f in TopMd(root)) yield return f;
        foreach (string sub in TopDirs(Path.Combine(root, "plugins")))
            foreach (string f in TopMd(sub)) yield return f;
        foreach (string sub in TopDirs(Path.Combine(root, "examples")))
        {
            foreach (string f in TopMd(sub)) yield return f;
            foreach (string sub2 in TopDirs(sub))
                foreach (string f in TopMd(sub2)) yield return f;
        }
        foreach (string sub in TopDirs(Path.Combine(root, "servers")))
            foreach (string f in TopMd(sub)) yield return f;
        foreach (string f in TopMd(Path.Combine(root, "infra"))) yield return f;
    }

    private static IEnumerable<string> TopMd(string dir) =>
        // Mirror the shell `*.md` glob (no dotglob): skip dot-prefixed names like
        // `.session-handoff.md` — they're not user docs and shell globbing ignores them.
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
            : [];

    private static IEnumerable<string> TopDirs(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateDirectories(dir) : [];

    private static HashSet<string> ReadIgnore(string path)
    {
        HashSet<string> set = new(StringComparer.Ordinal);
        if (!File.Exists(path)) return set;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            set.Add(line);
        }
        return set;
    }

    private static string Rel(string root, string full) =>
        Path.GetRelativePath(root, full).Replace('\\', '/');

    private static string NormSep(string p) => p.Replace('/', Path.DirectorySeparatorChar);

    private static string? GetOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

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
}
