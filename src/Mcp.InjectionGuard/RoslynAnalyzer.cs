using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Mcp.InjectionGuard;

/// <summary>
/// Roslyn syntax-tree walker that, for one C# file, finds every <c>[McpServerTool]</c>
/// method, classifies it as external-content-returning, and walks its return statements
/// to verify each non-exempt return wraps through <c>UntrustedContent.Wrap</c> /
/// <c>UntrustedContent.WrapJson</c>.
///
/// Syntax-only (no MSBuild workspace, no <see cref="SemanticModel"/>) so the tool stays
/// fast and dependency-light — same shape as <c>Mcp.ToolsDoc</c>. Attribute matching
/// tolerates <c>Foo</c> / <c>FooAttribute</c> / namespaced spellings; the wrap-call check
/// is a recursive descent into the returned expression's syntax tree (so locals assigned
/// from a wrapped value, ternaries with wrapped branches, and parenthesized wraps all
/// pass).
/// </summary>
public static class RoslynAnalyzer
{
    /// <summary>Built-in conservative method-name prefixes for the heuristic. Always honoured.</summary>
    private static readonly string[] BuiltinNamePrefixes =
    {
        "Get", "Read", "Search", "List", "Find", "Fetch", "Resolve",
    };

    /// <summary>Built-in invocation fragments for the heuristic. Always honoured.</summary>
    private static readonly string[] BuiltinInvocationFragments =
    {
        "GetAsync",
        "SendRequestAsync",
        "ExecuteAsync",
        "InvokeAsync",
        "QueryAsync",
        "RequestAsync",
        "GetStringAsync",
        "GetByteArrayAsync",
    };

    /// <summary>
    /// Return-type substrings that explicitly flag "the return value carries external
    /// content". When combined with a name-prefix hit, classification is unambiguous.
    /// </summary>
    private static readonly string[] ExternalReturnTypeFragments =
    {
        "string", "String",
        "JObject", "JArray", "JToken", "JsonObject", "JsonArray", "JsonNode",
        "IReadOnlyList", "IList", "List<", "IEnumerable",
        "object", "dynamic",
    };

    /// <summary>
    /// Return-type substrings that classify the method as safe regardless of any other
    /// signal — primitive scalars, void, raw <c>Task</c> / <c>ValueTask</c>. Tools that
    /// return <c>Task&lt;bool&gt;</c> or <c>Task&lt;int&gt;</c> cannot carry attacker-controlled
    /// strings, so they're never external.
    /// </summary>
    private static readonly string[] SafeReturnTypeWholeMatches =
    {
        "void", "Task", "ValueTask",
        "bool", "Boolean", "Task<bool>", "Task<Boolean>", "ValueTask<bool>", "ValueTask<Boolean>",
        "int", "Int32", "Task<int>", "Task<Int32>", "ValueTask<int>", "ValueTask<Int32>",
        "long", "Int64", "Task<long>", "Task<Int64>", "ValueTask<long>", "ValueTask<Int64>",
        "double", "Double", "Task<double>", "Task<Double>", "ValueTask<double>", "ValueTask<Double>",
        "decimal", "Decimal", "Task<decimal>", "Task<Decimal>", "ValueTask<decimal>", "ValueTask<Decimal>",
    };

    /// <summary>
    /// Scans <paramref name="filePath"/> and returns the per-method findings. Caller
    /// aggregates findings across files and emits the <c>::error::</c> / <c>::warning::</c>
    /// lines.
    /// </summary>
    public static List<ToolFinding> Scan(string filePath, string fileText, string relativePath, InjectionGuardConfig config)
    {
        List<ToolFinding> findings = new();

        SyntaxTree tree = CSharpSyntaxTree.ParseText(fileText, path: filePath);
        CompilationUnitSyntax root = (CompilationUnitSyntax)tree.GetRoot();

        IEnumerable<TypeDeclarationSyntax> toolTypes = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax)
            .Where(t => HasAttribute(t.AttributeLists, "McpServerToolType"));

        HashSet<string> exemptKeys = new(config.Exempt, StringComparer.Ordinal);
        HashSet<string> namePrefixes = new(BuiltinNamePrefixes, StringComparer.Ordinal);
        foreach (string extra in config.ExtraNamePrefixes) namePrefixes.Add(extra);

        HashSet<string> invocationFragments = new(BuiltinInvocationFragments, StringComparer.Ordinal);
        foreach (string extra in config.ExtraInvocationFragments) invocationFragments.Add(extra);

        foreach (TypeDeclarationSyntax typeDecl in toolTypes)
        {
            string typeName = typeDecl.Identifier.ValueText;
            foreach (MethodDeclarationSyntax method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                AttributeSyntax? toolAttr = FindAttribute(method.AttributeLists, "McpServerTool");
                if (toolAttr is null) continue;

                string methodName = method.Identifier.ValueText;
                string qualified = $"{typeName}.{methodName}";
                int line = LineOf(tree, method.Identifier.SpanStart);

                bool exemptViaConfig = exemptKeys.Contains(methodName) || exemptKeys.Contains(qualified);
                bool hasNotExternalContent = HasAttribute(method.AttributeLists, "NotExternalContent");
                bool hasExternalContent = HasAttribute(method.AttributeLists, "ExternalContent");

                if (exemptViaConfig || hasNotExternalContent)
                {
                    findings.Add(new ToolFinding(
                        relativePath, line, typeName, methodName,
                        Classification.Exempt,
                        ExemptReason: exemptViaConfig ? "injectionguard.json:exempt" : "[NotExternalContent]",
                        UnwrappedReturnLines: Array.Empty<int>(),
                        EveryReturnIsWrapped: false));
                    continue;
                }

                bool isExternal = hasExternalContent || HeuristicallyExternal(method, namePrefixes, invocationFragments);
                if (!isExternal)
                {
                    findings.Add(new ToolFinding(
                        relativePath, line, typeName, methodName,
                        Classification.NotExternal,
                        ExemptReason: null,
                        UnwrappedReturnLines: Array.Empty<int>(),
                        EveryReturnIsWrapped: false));
                    continue;
                }

                ReturnAuditResult audit = AuditReturns(method, tree);
                Classification cls = hasExternalContent ? Classification.AttributeExternal : Classification.HeuristicExternal;
                findings.Add(new ToolFinding(
                    relativePath, line, typeName, methodName, cls,
                    ExemptReason: null,
                    UnwrappedReturnLines: audit.UnwrappedReturnLines,
                    EveryReturnIsWrapped: audit.EveryReturnIsWrapped));
            }
        }

        return findings;
    }

    // ---------------------------------------------------------------------------------------
    // Classification helpers
    // ---------------------------------------------------------------------------------------

    private static bool HeuristicallyExternal(
        MethodDeclarationSyntax method,
        HashSet<string> namePrefixes,
        HashSet<string> invocationFragments)
    {
        string returnType = method.ReturnType.ToString().Trim();

        // Hard gate: return type that physically cannot carry an attacker-controlled
        // string never qualifies as external. Catches `Task<bool>` / `Task<int>` write
        // tools (Send*, Edit*, Delete*, Set*) that happen to share a name prefix.
        if (SafeReturnTypeWholeMatches.Any(t => string.Equals(t, returnType, StringComparison.Ordinal)))
            return false;

        string methodName = method.Identifier.ValueText;
        bool namePrefixHit = namePrefixes.Any(p => methodName.StartsWith(p, StringComparison.Ordinal));
        bool returnTypeHit = ExternalReturnTypeFragments.Any(f => returnType.Contains(f, StringComparison.Ordinal));

        // Heuristic A: name prefix hit. Once we've ruled out scalar return types, a
        // method named Get*/Read*/Search*/List*/Find*/Fetch*/Resolve* is a reader by
        // convention — its return value is the external-content carrier.
        if (namePrefixHit) return true;

        // Heuristic B: name didn't match a reader prefix, but the return type itself
        // is a typical external-content carrier (string / JSON object / list / open
        // object). Treat as external.
        if (returnTypeHit) return true;

        // Heuristic C: body invokes a known external-content-producing API by name.
        // Backstop for awkward names like `WhoAmI` / `Status` / `Subscribe` that
        // none-the-less return third-party data.
        IEnumerable<InvocationExpressionSyntax> invocations = method.DescendantNodes().OfType<InvocationExpressionSyntax>();
        foreach (InvocationExpressionSyntax inv in invocations)
        {
            string callText = inv.Expression.ToString();
            foreach (string fragment in invocationFragments)
            {
                if (callText.Contains(fragment, StringComparison.Ordinal)) return true;
            }
        }

        return false;
    }

    // ---------------------------------------------------------------------------------------
    // Return-statement audit
    // ---------------------------------------------------------------------------------------

    private sealed record ReturnAuditResult(int[] UnwrappedReturnLines, bool EveryReturnIsWrapped);

    private static ReturnAuditResult AuditReturns(MethodDeclarationSyntax method, SyntaxTree tree)
    {
        // Collect every return statement and arrow-body expression as a "return".
        List<(ExpressionSyntax Expr, int Line, bool InsideCatch)> returns = new();

        // Expression-bodied method: `=> expr;` counts as one return.
        if (method.ExpressionBody is not null)
        {
            ExpressionSyntax expr = method.ExpressionBody.Expression;
            returns.Add((expr, LineOf(tree, expr.SpanStart), InsideCatch: false));
        }

        if (method.Body is not null)
        {
            foreach (ReturnStatementSyntax ret in method.Body.DescendantNodes().OfType<ReturnStatementSyntax>())
            {
                if (ret.Expression is null) continue; // `return;` in non-task void — irrelevant for guard
                bool insideCatch = ret.Ancestors().OfType<CatchClauseSyntax>().Any();
                returns.Add((ret.Expression, LineOf(tree, ret.SpanStart), insideCatch));
            }
        }

        // Build the param-name set so we can recognise "returns a method parameter" as safe.
        HashSet<string> paramNames = new(StringComparer.Ordinal);
        foreach (ParameterSyntax p in method.ParameterList.Parameters)
            paramNames.Add(p.Identifier.ValueText);

        // Build the local-decl map so we can recognise "returns a local whose initializer
        // (or any subsequent assignment) wraps via UntrustedContent.Wrap*".
        Dictionary<string, List<ExpressionSyntax>> localAssignments = CollectLocalAssignments(method);

        List<int> unwrappedLines = new();
        int totalConsidered = 0;

        foreach ((ExpressionSyntax expr, int line, bool insideCatch) in returns)
        {
            // Safe shapes — never count toward "wrapped" tally but never flag either.
            if (insideCatch) continue;
            if (expr is LiteralExpressionSyntax lit &&
                (lit.IsKind(SyntaxKind.NullLiteralExpression) || lit.IsKind(SyntaxKind.StringLiteralExpression) ||
                 lit.IsKind(SyntaxKind.TrueLiteralExpression) || lit.IsKind(SyntaxKind.FalseLiteralExpression) ||
                 lit.IsKind(SyntaxKind.NumericLiteralExpression) || lit.IsKind(SyntaxKind.DefaultLiteralExpression)))
                continue;
            if (expr is DefaultExpressionSyntax) continue;
            if (expr is ThrowExpressionSyntax) continue;
            if (expr is IdentifierNameSyntax idName && paramNames.Contains(idName.Identifier.ValueText)) continue;

            // Member-access / element-access entirely off a parameter root (e.g.
            // `return ctx.SomeProp;`) — treat as safe if root identifier is a parameter.
            if (RootIdentifierIsParameter(expr, paramNames)) continue;

            totalConsidered++;
            if (IsWrapped(expr, localAssignments)) continue;
            unwrappedLines.Add(line);
        }

        bool everyReturnIsWrapped = totalConsidered > 0 && unwrappedLines.Count == 0;
        return new ReturnAuditResult(unwrappedLines.ToArray(), everyReturnIsWrapped);
    }

    /// <summary>
    /// Walks <paramref name="method"/>'s body to gather every local variable's initializer
    /// expression plus any later <c>local = expr</c> assignment. Used to follow
    /// <c>var x = await Foo(); ...; return UntrustedContent.Wrap(x);</c> as wrapped and,
    /// inversely, <c>var x = UntrustedContent.Wrap(await Foo()); ...; return x;</c> as
    /// also wrapped.
    /// </summary>
    private static Dictionary<string, List<ExpressionSyntax>> CollectLocalAssignments(MethodDeclarationSyntax method)
    {
        Dictionary<string, List<ExpressionSyntax>> map = new(StringComparer.Ordinal);
        if (method.Body is null) return map;

        foreach (LocalDeclarationStatementSyntax decl in method.Body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            foreach (VariableDeclaratorSyntax v in decl.Declaration.Variables)
            {
                if (v.Initializer?.Value is null) continue;
                string name = v.Identifier.ValueText;
                if (!map.TryGetValue(name, out List<ExpressionSyntax>? list))
                    map[name] = list = new List<ExpressionSyntax>();
                list.Add(v.Initializer.Value);
            }
        }

        foreach (AssignmentExpressionSyntax assign in method.Body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (!assign.IsKind(SyntaxKind.SimpleAssignmentExpression)) continue;
            if (assign.Left is not IdentifierNameSyntax id) continue;
            string name = id.Identifier.ValueText;
            if (!map.TryGetValue(name, out List<ExpressionSyntax>? list))
                map[name] = list = new List<ExpressionSyntax>();
            list.Add(assign.Right);
        }

        return map;
    }

    /// <summary>
    /// Returns true if the returned expression syntactically contains a call to
    /// <c>UntrustedContent.Wrap(...)</c> or <c>UntrustedContent.WrapJson(...)</c>
    /// (including through parenthesized wraps, ternaries — both branches must wrap —
    /// awaited tasks, and chained member access on a wrap result). Identifier returns
    /// chase the local's known assignments.
    /// </summary>
    private static bool IsWrapped(ExpressionSyntax expr, Dictionary<string, List<ExpressionSyntax>> locals)
    {
        return IsWrappedCore(expr, locals, new HashSet<string>(StringComparer.Ordinal));
    }

    private static bool IsWrappedCore(ExpressionSyntax expr, Dictionary<string, List<ExpressionSyntax>> locals, HashSet<string> visitedLocals)
    {
        switch (expr)
        {
            case ParenthesizedExpressionSyntax paren:
                return IsWrappedCore(paren.Expression, locals, visitedLocals);

            case AwaitExpressionSyntax awt:
                return IsWrappedCore(awt.Expression, locals, visitedLocals);

            case CastExpressionSyntax cast:
                return IsWrappedCore(cast.Expression, locals, visitedLocals);

            case ConditionalExpressionSyntax cond:
                // Both branches must wrap. Constant-string branches count as safe (allowed).
                return BranchIsSafeOrWrapped(cond.WhenTrue, locals, visitedLocals)
                    && BranchIsSafeOrWrapped(cond.WhenFalse, locals, visitedLocals);

            case BinaryExpressionSyntax bin when bin.IsKind(SyntaxKind.CoalesceExpression):
                return BranchIsSafeOrWrapped(bin.Left, locals, visitedLocals)
                    && BranchIsSafeOrWrapped(bin.Right, locals, visitedLocals);

            case InvocationExpressionSyntax inv:
                if (IsWrapInvocation(inv)) return true;
                // Wrap result passed into a chained call (e.g. ToOutput(UntrustedContent.Wrap(...))) is
                // still wrapped — descend into the arg expressions to find the wrap call.
                foreach (ArgumentSyntax a in inv.ArgumentList.Arguments)
                {
                    if (IsWrappedCore(a.Expression, locals, visitedLocals)) return true;
                }
                // Member-access whose root is a wrap result, e.g. `UntrustedContent.Wrap(x).ToString()`.
                if (inv.Expression is MemberAccessExpressionSyntax invMa &&
                    IsWrappedCore(invMa.Expression, locals, visitedLocals)) return true;
                return false;

            case MemberAccessExpressionSyntax ma:
                return IsWrappedCore(ma.Expression, locals, visitedLocals);

            case IdentifierNameSyntax id:
                string name = id.Identifier.ValueText;
                if (!locals.TryGetValue(name, out List<ExpressionSyntax>? assignments)) return false;
                if (!visitedLocals.Add(name)) return false; // cycle guard
                // A local counts as wrapped if every observed assignment wraps OR is safe.
                foreach (ExpressionSyntax a in assignments)
                {
                    if (!BranchIsSafeOrWrapped(a, locals, visitedLocals)) return false;
                }
                return assignments.Count > 0;

            case ObjectCreationExpressionSyntax oc:
                // `new SomeDto { Field = UntrustedContent.Wrap(...) }` — descend into arg list + initializer.
                if (oc.ArgumentList is not null)
                {
                    foreach (ArgumentSyntax a in oc.ArgumentList.Arguments)
                        if (IsWrappedCore(a.Expression, locals, visitedLocals)) return true;
                }
                if (oc.Initializer is not null)
                {
                    foreach (ExpressionSyntax e in oc.Initializer.Expressions)
                        if (IsWrappedCore(e, locals, visitedLocals)) return true;
                }
                return false;

            case AnonymousObjectCreationExpressionSyntax aoc:
                foreach (AnonymousObjectMemberDeclaratorSyntax mem in aoc.Initializers)
                    if (IsWrappedCore(mem.Expression, locals, visitedLocals)) return true;
                return false;

            default:
                return false;
        }
    }

    private static bool BranchIsSafeOrWrapped(ExpressionSyntax expr, Dictionary<string, List<ExpressionSyntax>> locals, HashSet<string> visitedLocals)
    {
        if (expr is LiteralExpressionSyntax lit &&
            (lit.IsKind(SyntaxKind.NullLiteralExpression) || lit.IsKind(SyntaxKind.StringLiteralExpression) ||
             lit.IsKind(SyntaxKind.TrueLiteralExpression) || lit.IsKind(SyntaxKind.FalseLiteralExpression) ||
             lit.IsKind(SyntaxKind.NumericLiteralExpression) || lit.IsKind(SyntaxKind.DefaultLiteralExpression)))
            return true;
        if (expr is DefaultExpressionSyntax) return true;
        return IsWrappedCore(expr, locals, visitedLocals);
    }

    /// <summary>
    /// Returns true for <c>UntrustedContent.Wrap(...)</c>, <c>UntrustedContent.WrapJson(...)</c>,
    /// or namespaced variants (<c>Mcp.Auth.UntrustedContent.Wrap</c>). The simple-name check
    /// catches both static-using imports and direct qualification.
    /// </summary>
    private static bool IsWrapInvocation(InvocationExpressionSyntax inv)
    {
        string text = inv.Expression.ToString();
        // Strip generic suffix (Wrap<T>) for the suffix-match.
        int genericIdx = text.IndexOf('<');
        if (genericIdx >= 0) text = text[..genericIdx];

        if (text.EndsWith(".UntrustedContent.Wrap", StringComparison.Ordinal)) return true;
        if (text.EndsWith(".UntrustedContent.WrapJson", StringComparison.Ordinal)) return true;
        if (text == "UntrustedContent.Wrap" || text == "UntrustedContent.WrapJson") return true;
        // Tolerate a `using static …UntrustedContent;` import → bare `Wrap(...)` / `WrapJson(...)`.
        if (text == "Wrap" || text == "WrapJson") return true;
        return false;
    }

    private static bool RootIdentifierIsParameter(ExpressionSyntax expr, HashSet<string> paramNames)
    {
        ExpressionSyntax cursor = expr;
        while (true)
        {
            switch (cursor)
            {
                case ParenthesizedExpressionSyntax paren:
                    cursor = paren.Expression;
                    continue;
                case MemberAccessExpressionSyntax ma:
                    cursor = ma.Expression;
                    continue;
                case ElementAccessExpressionSyntax ea:
                    cursor = ea.Expression;
                    continue;
                case ConditionalAccessExpressionSyntax ca:
                    cursor = ca.Expression;
                    continue;
                case IdentifierNameSyntax id:
                    return paramNames.Contains(id.Identifier.ValueText);
                default:
                    return false;
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Attribute helpers
    // ---------------------------------------------------------------------------------------

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

    private static int LineOf(SyntaxTree tree, int position) => tree.GetLineSpan(new TextSpan(position, 0)).StartLinePosition.Line + 1;
}

/// <summary>
/// One <c>[McpServerTool]</c> method's classification plus the per-return audit outcome.
/// </summary>
public sealed record ToolFinding(
    string RelativePath,
    int Line,
    string TypeName,
    string MethodName,
    Classification Classification,
    string? ExemptReason,
    IReadOnlyList<int> UnwrappedReturnLines,
    bool EveryReturnIsWrapped);

public enum Classification
{
    /// <summary>Heuristic concluded "not user-content-returning"; skipped.</summary>
    NotExternal,
    /// <summary>Exempt via <c>[NotExternalContent]</c> attribute or <c>injectionguard.json:exempt</c>.</summary>
    Exempt,
    /// <summary>Explicit opt-in via <c>[ExternalContent]</c> attribute.</summary>
    AttributeExternal,
    /// <summary>Heuristic classified the method as external-content-returning.</summary>
    HeuristicExternal,
}
