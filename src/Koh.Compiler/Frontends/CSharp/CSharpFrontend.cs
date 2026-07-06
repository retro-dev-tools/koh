using Koh.Compiler.Ir;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Frontends.CSharp;

/// <summary>Thrown when a C# construct falls outside the supported "Koh C#" subset.</summary>
public sealed class CSharpNotSupportedException : Exception
{
    public CSharpNotSupportedException(string message) : base(message) { }
}

/// <summary>
/// The C# frontend. Roslyn parses the source; this walks the syntax tree, rejecting constructs
/// outside the supported systems subset ("Koh C#") and lowering the rest to Koh IR. It does not
/// use the semantic model: types are tracked from declarations with C-like rules (see
/// <see cref="CsType"/>). Static methods become IR functions; locals and parameters become
/// <c>alloca</c>s (so control flow needs no phi construction here — mutable state lives in memory,
/// and the backend statically allocates it).
/// </summary>
public sealed class CSharpFrontend : IFrontend
{
    public string Name => "csharp";

    public IReadOnlyList<string> Extensions => [".cs"];

    public IrModule Lower(SourceText source, DiagnosticBag diagnostics)
    {
        var tree = CSharpSyntaxTree.ParseText(source.ToString(), path: source.FilePath);
        var root = tree.GetCompilationUnitRoot();

        foreach (var diag in tree.GetDiagnostics())
            if (diag.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                throw new CSharpNotSupportedException($"C# parse error: {diag.GetMessage()}");

        var module = new IrModule(source.FilePath.Length > 0 ? source.FilePath : "csharp");
        var methods = new Dictionary<string, CsMethod>(StringComparer.Ordinal);
        var bodies = new List<(CsMethod Method, BlockSyntax? Body, ArrowExpressionClauseSyntax? Arrow)>();

        // Pass 1: signatures, so calls resolve regardless of source order. Accept both class methods
        // and top-level `static T F(...) {...}` functions (which Roslyn parses as local functions).
        foreach (var decl in CollectMethods(root))
        {
            var (name, returnSyntax, parameterList, body, arrow) = Describe(decl);
            var returnType = MapReturnType(returnSyntax);
            var paramTypes = new List<CsType>();
            var parameters = new List<IrParameter>();
            foreach (var p in parameterList.Parameters)
            {
                var t = MapType(p.Type!);
                paramTypes.Add(t);
                parameters.Add(new IrParameter(p.Identifier.Text, t.Ir));
            }

            var fn = new IrFunction(name, returnType?.Ir ?? IrType.Void, parameters);
            module.Functions.Add(fn);
            var method = new CsMethod(fn, returnType, paramTypes);
            methods[name] = method;
            bodies.Add((method, body, arrow));
        }

        // Pass 2: bodies.
        foreach (var (method, body, arrow) in bodies)
            new MethodLowerer(method, body, arrow, methods).Lower();

        return module;
    }

    private static IEnumerable<SyntaxNode> CollectMethods(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is MethodDeclarationSyntax)
                yield return node;
            else if (node is LocalFunctionStatementSyntax fn && fn.Parent is GlobalStatementSyntax)
                yield return node; // top-level function
        }
    }

    private static (string Name, TypeSyntax Return, ParameterListSyntax Parameters, BlockSyntax? Body, ArrowExpressionClauseSyntax? Arrow)
        Describe(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => (m.Identifier.Text, m.ReturnType, m.ParameterList, m.Body, m.ExpressionBody),
        LocalFunctionStatementSyntax f => (f.Identifier.Text, f.ReturnType, f.ParameterList, f.Body, f.ExpressionBody),
        _ => throw new CSharpNotSupportedException($"unsupported declaration '{node.Kind()}'."),
    };

    internal static CsType MapType(TypeSyntax type)
    {
        if (type is PredefinedTypeSyntax predefined
            && CsType.FromKeyword(predefined.Keyword.Kind()) is { } t)
            return t;
        throw new CSharpNotSupportedException($"unsupported type '{type}' (Koh C# supports byte/sbyte/ushort/short/bool).");
    }

    private static CsType? MapReturnType(TypeSyntax type)
    {
        if (type is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword })
            return null;
        return MapType(type);
    }
}

/// <summary>A resolved method: its IR function plus Koh C# signature types (for signedness/coercion).</summary>
internal sealed record CsMethod(IrFunction Fn, CsType? Return, IReadOnlyList<CsType> Params);
