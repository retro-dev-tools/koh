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
        // Wrap in an implicit static class so plain `static T F(...)` methods and enum/struct
        // declarations coexist without hitting C#'s "top-level statements first" rule. (Source
        // lines shift by one; accounted for when line maps are emitted.)
        var wrapped = "static class __KohProgram {\n" + source.ToString() + "\n}";
        var tree = CSharpSyntaxTree.ParseText(wrapped, path: source.FilePath);
        var root = tree.GetCompilationUnitRoot();

        foreach (var diag in tree.GetDiagnostics())
            if (diag.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                throw new CSharpNotSupportedException($"C# parse error: {diag.GetMessage()}");

        var module = new IrModule(source.FilePath.Length > 0 ? source.FilePath : "csharp");
        var methods = new Dictionary<string, CsMethod>(StringComparer.Ordinal);
        var bodies = new List<(CsMethod Method, BlockSyntax? Body, ArrowExpressionClauseSyntax? Arrow)>();

        // Pass 0: enums (named constants), so their types and members resolve everywhere.
        var enums = CollectEnums(root);

        // Pass 1: signatures, so calls resolve regardless of source order. Accept both class methods
        // and top-level `static T F(...) {...}` functions (which Roslyn parses as local functions).
        foreach (var decl in CollectMethods(root))
        {
            var (name, returnSyntax, parameterList, body, arrow) = Describe(decl);
            var returnType = ResolveReturnType(returnSyntax, enums);
            var paramTypes = new List<CsType>();
            var parameters = new List<IrParameter>();
            foreach (var p in parameterList.Parameters)
            {
                var t = ResolveType(p.Type!, enums);
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
            new MethodLowerer(method, body, arrow, methods, enums).Lower();

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

    internal static CsType ResolveType(TypeSyntax type, IReadOnlyDictionary<string, CsEnum> enums)
    {
        if (type is PredefinedTypeSyntax predefined && CsType.FromKeyword(predefined.Keyword.Kind()) is { } t)
            return t;
        if (type is IdentifierNameSyntax id && enums.TryGetValue(id.Identifier.Text, out var e))
            return e.Underlying;
        throw new CSharpNotSupportedException(
            $"unsupported type '{type}' (Koh C# supports byte/sbyte/ushort/short/bool and enums).");
    }

    private static CsType? ResolveReturnType(TypeSyntax type, IReadOnlyDictionary<string, CsEnum> enums)
    {
        if (type is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword })
            return null;
        return ResolveType(type, enums);
    }

    private static Dictionary<string, CsEnum> CollectEnums(CompilationUnitSyntax root)
    {
        var enums = new Dictionary<string, CsEnum>(StringComparer.Ordinal);
        foreach (var decl in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
        {
            var underlying = decl.BaseList is { Types.Count: > 0 } bases
                ? ResolveType(bases.Types[0].Type, enums)
                : CsType.U8; // Koh C# defaults enums to byte (int has no place on an 8-bit CPU)

            var members = new Dictionary<string, long>(StringComparer.Ordinal);
            long next = 0;
            foreach (var member in decl.Members)
            {
                long value = member.EqualsValue is { } eq
                    ? ConstEval(eq.Value, name => members.TryGetValue(name, out var v) ? v : null)
                    : next;
                members[member.Identifier.Text] = value;
                next = value + 1;
            }
            enums[decl.Identifier.Text] = new CsEnum(underlying, members);
        }
        return enums;
    }

    /// <summary>Fold a constant expression to a long, resolving bare names via <paramref name="lookup"/>.</summary>
    internal static long ConstEval(ExpressionSyntax expr, Func<string, long?> lookup) => expr switch
    {
        ParenthesizedExpressionSyntax p => ConstEval(p.Expression, lookup),
        LiteralExpressionSyntax lit => Convert.ToInt64(lit.Token.Value),
        IdentifierNameSyntax id => lookup(id.Identifier.Text)
            ?? throw new CSharpNotSupportedException($"'{id.Identifier.Text}' is not a constant."),
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } u => -ConstEval(u.Operand, lookup),
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryPlusExpression } u => ConstEval(u.Operand, lookup),
        BinaryExpressionSyntax bin => FoldBinary(bin, lookup),
        _ => throw new CSharpNotSupportedException($"'{expr}' is not a constant expression."),
    };

    private static long FoldBinary(BinaryExpressionSyntax bin, Func<string, long?> lookup)
    {
        long l = ConstEval(bin.Left, lookup), r = ConstEval(bin.Right, lookup);
        return bin.Kind() switch
        {
            SyntaxKind.AddExpression => l + r,
            SyntaxKind.SubtractExpression => l - r,
            SyntaxKind.MultiplyExpression => l * r,
            SyntaxKind.DivideExpression => l / r,
            SyntaxKind.ModuloExpression => l % r,
            SyntaxKind.BitwiseAndExpression => l & r,
            SyntaxKind.BitwiseOrExpression => l | r,
            SyntaxKind.ExclusiveOrExpression => l ^ r,
            SyntaxKind.LeftShiftExpression => l << (int)r,
            SyntaxKind.RightShiftExpression => l >> (int)r,
            _ => throw new CSharpNotSupportedException($"'{bin}' is not a constant expression."),
        };
    }
}

/// <summary>An enum: its underlying Koh C# type and member values.</summary>
internal sealed record CsEnum(CsType Underlying, IReadOnlyDictionary<string, long> Members);

/// <summary>A resolved method: its IR function plus Koh C# signature types (for signedness/coercion).</summary>
internal sealed record CsMethod(IrFunction Fn, CsType? Return, IReadOnlyList<CsType> Params);
