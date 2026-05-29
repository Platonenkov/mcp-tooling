using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcp.ToolsDoc;

/// <summary>
/// Config-driven generator + CI sync-check for MCP tool reference docs. Reads
/// <c>toolsdoc.json</c> (servers + output/marker paths), walks each server's tool
/// directory with Roslyn (syntax only, no build), and runs up to three passes:
///   1. <b>Render</b> the canonical per-tool reference into the configured output.
///   2. <b>Substitute</b> <c>&lt;!-- toolcount:NAME --&gt;…&lt;!-- /toolcount:NAME --&gt;</c>
///      markers in the configured marker files (optional).
///   3. <b>Verify</b> the configured cheatsheet mentions every tool by name (optional).
///
/// Modes: <c>--write</c> (default) overwrites/edits in place; <c>--check</c> compares
/// in-memory against disk and exits non-zero on drift (for CI).
/// Options: <c>--config &lt;path&gt;</c> (default &lt;repo-root&gt;/toolsdoc.json),
/// <c>--repo-root &lt;path&gt;</c> (default: git root from cwd, else cwd).
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        bool check = args.Contains("--check");
        string repoRoot = GetOption(args, "--repo-root") ?? FindRepoRoot();
        string configPath = GetOption(args, "--config") ?? Path.Combine(repoRoot, "toolsdoc.json");

        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"::error::config not found: {configPath} (pass --config or add toolsdoc.json at the repo root).");
            return 2;
        }

        ToolsDocConfig config;
        try
        {
            config = ToolsDocConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"::error::failed to read {configPath}: {ex.Message}");
            return 2;
        }

        List<ServerSpec> servers = config.Servers
            .Select(s => new ServerSpec(s.Id, string.IsNullOrEmpty(s.DisplayName) ? s.Id : s.DisplayName,
                Path.Combine(repoRoot, NormalizeRel(s.ToolsDir)), s.Blurb))
            .ToList();

        List<ServerDocument> documents;
        try
        {
            documents = servers.Select(ScanServer).ToList();
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine($"::error::{ex.Message}");
            return 2;
        }

        Dictionary<string, string> placeholders = BuildPlaceholderValues(documents);
        string rendered = Render(documents, config);
        string generatedPath = Path.Combine(repoRoot, NormalizeRel(config.GeneratedOutput));

        int exitCode = 0;

        // Pass 1: canonical reference doc.
        if (check)
        {
            string existing = File.Exists(generatedPath) ? File.ReadAllText(generatedPath) : "";
            if (!string.Equals(NormalizeLineEndings(existing), NormalizeLineEndings(rendered), StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"::error::{config.GeneratedOutput} is out of sync with [McpServerTool] attributes.");
                Console.Error.WriteLine("       Regenerate locally with:  dotnet tool run mcp-toolsdoc");
                exitCode = 1;
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(generatedPath)!);
            File.WriteAllText(generatedPath, rendered);
        }

        // Pass 2: tool-count placeholder substitution (optional).
        foreach (string rel in config.MarkerFiles)
        {
            string file = Path.Combine(repoRoot, NormalizeRel(rel));
            if (!File.Exists(file)) continue;
            string before = File.ReadAllText(file);
            string after = SubstitutePlaceholders(before, placeholders);
            if (string.Equals(NormalizeLineEndings(before), NormalizeLineEndings(after), StringComparison.Ordinal)) continue;
            if (check)
            {
                Console.Error.WriteLine($"::error::tool-count placeholders in {rel} are stale. Regenerate with: dotnet tool run mcp-toolsdoc");
                exitCode = 1;
            }
            else
            {
                File.WriteAllText(file, after);
            }
        }

        // Pass 3: cheatsheet presence check (optional).
        if (!string.IsNullOrEmpty(config.Cheatsheet))
        {
            string cheatsheetPath = Path.Combine(repoRoot, NormalizeRel(config.Cheatsheet));
            if (File.Exists(cheatsheetPath))
            {
                List<string> missing = FindToolsMissingFromCheatsheet(documents, File.ReadAllText(cheatsheetPath));
                if (missing.Count > 0)
                {
                    Console.Error.WriteLine($"::error::{config.Cheatsheet} is missing {missing.Count} tool(s) that exist in code:");
                    foreach (string name in missing) Console.Error.WriteLine($"         - {name}");
                    exitCode = 1;
                }
            }
        }

        int total = documents.Sum(d => d.Tools.Count);
        if (check)
        {
            if (exitCode == 0) Console.WriteLine($"docs are in sync ({total} tools across {documents.Count} server(s)).");
            return exitCode;
        }

        Console.WriteLine($"Wrote {config.GeneratedOutput} ({total} tools across {documents.Count} server(s)).");
        foreach (ServerDocument doc in documents) Console.WriteLine($"  {doc.Spec.DisplayName}: {doc.Tools.Count} tools");
        if (config.MarkerFiles.Count > 0) Console.WriteLine($"Substituted tool-count placeholders across {config.MarkerFiles.Count} file(s).");
        return exitCode;
    }

    private static string? GetOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string NormalizeRel(string p) => p.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

    private static Dictionary<string, string> BuildPlaceholderValues(IReadOnlyList<ServerDocument> documents)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        int total = 0;
        foreach (ServerDocument doc in documents)
        {
            int count = doc.Tools.Count;
            total += count;
            values[doc.Spec.Id] = count.ToString(CultureInfo.InvariantCulture);
        }
        values["total"] = total.ToString(CultureInfo.InvariantCulture);
        return values;
    }

    private static string SubstitutePlaceholders(string text, IReadOnlyDictionary<string, string> values)
    {
        Regex pattern = new(
            @"<!--\s*toolcount:(?<name>[A-Za-z0-9_:-]+)\s*-->.*?<!--\s*/toolcount:\k<name>\s*-->",
            RegexOptions.Compiled | RegexOptions.Singleline);
        return pattern.Replace(text, match =>
        {
            string name = match.Groups["name"].Value;
            if (!values.TryGetValue(name, out string? value))
            {
                Console.Error.WriteLine($"warning: unknown placeholder 'toolcount:{name}' — leaving as-is.");
                return match.Value;
            }
            return $"<!-- toolcount:{name} -->{value}<!-- /toolcount:{name} -->";
        });
    }

    private static List<string> FindToolsMissingFromCheatsheet(IReadOnlyList<ServerDocument> documents, string cheatsheet)
    {
        List<string> missing = [];
        foreach (ServerDocument doc in documents)
        {
            foreach (ToolDoc tool in doc.Tools)
            {
                if (cheatsheet.Contains("`" + tool.Name + "`", StringComparison.Ordinal)) continue;
                if (cheatsheet.Contains(tool.Name, StringComparison.Ordinal)) continue;
                missing.Add(tool.Name);
            }
        }
        return missing;
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

    private static ServerDocument ScanServer(ServerSpec spec)
    {
        if (!Directory.Exists(spec.ToolsDir))
        {
            throw new DirectoryNotFoundException($"Tools directory not found for server '{spec.Id}': {spec.ToolsDir}");
        }

        List<ToolDoc> tools = [];
        foreach (string file in Directory.EnumerateFiles(spec.ToolsDir, "*.cs", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
            CompilationUnitSyntax root = (CompilationUnitSyntax)tree.GetRoot();

            IEnumerable<ClassDeclarationSyntax> toolTypes = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Where(c => HasAttribute(c.AttributeLists, "McpServerToolType"));

            foreach (ClassDeclarationSyntax classDecl in toolTypes)
            {
                foreach (MethodDeclarationSyntax method in classDecl.Members.OfType<MethodDeclarationSyntax>())
                {
                    AttributeSyntax? toolAttr = FindAttribute(method.AttributeLists, "McpServerTool");
                    if (toolAttr is null) continue;

                    string name = ExtractNamedStringArgument(toolAttr, "Name") ?? ToSnakeCase(method.Identifier.ValueText);
                    AttributeSyntax? descAttr = FindAttribute(method.AttributeLists, "Description");
                    string description = descAttr is not null ? ExtractPositionalStringArgument(descAttr) ?? "" : "";

                    tools.Add(new ToolDoc(name, description, PathRelativeToRepoRoot(file), ExtractParameters(method)));
                }
            }
        }

        tools.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return new ServerDocument(spec, tools);
    }

    private static List<ParamDoc> ExtractParameters(MethodDeclarationSyntax method)
    {
        List<ParamDoc> parameters = [];
        foreach (ParameterSyntax param in method.ParameterList.Parameters)
        {
            string typeText = param.Type?.ToString() ?? "";
            if (typeText == "CancellationToken") continue;
            AttributeSyntax? descAttr = FindAttribute(param.AttributeLists, "Description");
            if (descAttr is null) continue; // not a tool-exposed parameter (service/coordinator)

            string description = ExtractPositionalStringArgument(descAttr) ?? "";
            string? defaultValue = param.Default?.Value.ToString();
            bool isOptional = defaultValue is not null || typeText.EndsWith('?');
            parameters.Add(new ParamDoc(param.Identifier.ValueText, typeText, description, defaultValue, isOptional));
        }
        return parameters;
    }

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string nameWithoutSuffix) =>
        FindAttribute(lists, nameWithoutSuffix) is not null;

    private static AttributeSyntax? FindAttribute(SyntaxList<AttributeListSyntax> lists, string nameWithoutSuffix)
    {
        foreach (AttributeListSyntax list in lists)
        {
            foreach (AttributeSyntax attr in list.Attributes)
            {
                string name = attr.Name.ToString();
                string simple = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
                if (simple == nameWithoutSuffix || simple == nameWithoutSuffix + "Attribute") return attr;
            }
        }
        return null;
    }

    private static string? ExtractNamedStringArgument(AttributeSyntax attr, string argName)
    {
        if (attr.ArgumentList is null) return null;
        foreach (AttributeArgumentSyntax arg in attr.ArgumentList.Arguments)
        {
            if (arg.NameEquals is not null && arg.NameEquals.Name.Identifier.ValueText == argName)
                return FoldStringExpression(arg.Expression);
        }
        return null;
    }

    private static string? ExtractPositionalStringArgument(AttributeSyntax attr)
    {
        if (attr.ArgumentList is null || attr.ArgumentList.Arguments.Count == 0) return null;
        AttributeArgumentSyntax first = attr.ArgumentList.Arguments[0];
        if (first.NameEquals is not null || first.NameColon is not null) return null;
        return FoldStringExpression(first.Expression);
    }

    private static string? FoldStringExpression(ExpressionSyntax expr)
    {
        switch (expr)
        {
            case LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression):
                return literal.Token.ValueText;
            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression):
                string? left = FoldStringExpression(binary.Left);
                string? right = FoldStringExpression(binary.Right);
                return left is null || right is null ? null : left + right;
            case ParenthesizedExpressionSyntax paren:
                return FoldStringExpression(paren.Expression);
            case InterpolatedStringExpressionSyntax interp:
                StringBuilder sb = new();
                foreach (InterpolatedStringContentSyntax content in interp.Contents)
                {
                    if (content is InterpolatedStringTextSyntax text) sb.Append(text.TextToken.ValueText);
                    else return null;
                }
                return sb.ToString();
            default:
                return null;
        }
    }

    private static string ToSnakeCase(string pascal)
    {
        StringBuilder sb = new(pascal.Length + 4);
        for (int i = 0; i < pascal.Length; i++)
        {
            char c = pascal[i];
            if (i > 0 && char.IsUpper(c)) sb.Append('_');
            sb.Append(char.ToLower(c, CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static string PathRelativeToRepoRoot(string fullPath)
    {
        DirectoryInfo? cursor = new(Path.GetDirectoryName(fullPath)!);
        while (cursor is not null)
        {
            string gitPath = Path.Combine(cursor.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath)) break;
            cursor = cursor.Parent;
        }
        string baseDir = cursor?.FullName ?? Path.GetDirectoryName(fullPath)!;
        return Path.GetRelativePath(baseDir, fullPath).Replace('\\', '/');
    }

    private static string Render(IReadOnlyList<ServerDocument> documents, ToolsDocConfig config)
    {
        StringBuilder sb = new();
        int total = documents.Sum(d => d.Tools.Count);

        sb.AppendLine("# Tool reference (auto-generated)");
        sb.AppendLine();
        sb.AppendLine("> ⚠️ **Do not edit by hand.** Generated from `[McpServerTool]` / `[Description]`");
        sb.AppendLine("> attributes by the `Mcp.ToolsDoc` tool. CI fails if it drifts.");
        sb.AppendLine(">");
        sb.AppendLine("> Regenerate:");
        sb.AppendLine(">");
        sb.AppendLine("> ```");
        sb.AppendLine("> dotnet tool run mcp-toolsdoc");
        sb.AppendLine("> ```");
        sb.AppendLine();
        sb.Append("**").Append(total).Append(" tools across ").Append(documents.Count).AppendLine(" server(s).**");
        sb.AppendLine();

        foreach (ServerDocument doc in documents)
            sb.Append("- [`").Append(doc.Spec.DisplayName).Append("`](#").Append(AnchorFor(doc.Spec.DisplayName)).Append(") — ").Append(doc.Tools.Count).AppendLine(" tools");
        sb.AppendLine();

        foreach (ServerDocument doc in documents)
        {
            sb.Append("## `").Append(doc.Spec.DisplayName).AppendLine("`");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(doc.Spec.Blurb)) { sb.AppendLine(doc.Spec.Blurb); sb.AppendLine(); }
            sb.Append("**").Append(doc.Tools.Count).AppendLine(" tools.**");
            sb.AppendLine();
            foreach (ToolDoc tool in doc.Tools) RenderTool(sb, tool);
        }

        return sb.ToString();
    }

    private static void RenderTool(StringBuilder sb, ToolDoc tool)
    {
        sb.Append("### `").Append(tool.Name).AppendLine("`");
        sb.AppendLine();
        sb.Append("<sub>Source: `").Append(tool.SourceFile).AppendLine("`</sub>");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(tool.Description)) { sb.AppendLine(tool.Description); sb.AppendLine(); }

        if (tool.Parameters.Count == 0)
        {
            sb.AppendLine("_No parameters._");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| Param | Type | Required | Default | Description |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (ParamDoc p in tool.Parameters)
        {
            string required = p.IsOptional ? "no" : "**yes**";
            string defaultCell = p.DefaultValue is null ? "—" : "`" + p.DefaultValue + "`";
            sb.Append("| `").Append(p.Name).Append("` | `").Append(EscapeTableCell(p.Type)).Append("` | ").Append(required).Append(" | ").Append(EscapeTableCell(defaultCell)).Append(" | ").Append(EscapeTableCell(p.Description)).AppendLine(" |");
        }
        sb.AppendLine();
    }

    private static string EscapeTableCell(string s) => s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");

    private static string AnchorFor(string heading)
    {
        StringBuilder sb = new(heading.Length);
        foreach (char c in heading.ToLower(CultureInfo.InvariantCulture))
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c is ' ' or '-' or '_') sb.Append('-');
        }
        return sb.ToString();
    }

    private static string NormalizeLineEndings(string s) => s.Replace("\r\n", "\n");
}

internal sealed record ServerSpec(string Id, string DisplayName, string ToolsDir, string Blurb);
internal sealed record ServerDocument(ServerSpec Spec, List<ToolDoc> Tools);
internal sealed record ToolDoc(string Name, string Description, string SourceFile, List<ParamDoc> Parameters);
internal sealed record ParamDoc(string Name, string Type, string Description, string? DefaultValue, bool IsOptional);
