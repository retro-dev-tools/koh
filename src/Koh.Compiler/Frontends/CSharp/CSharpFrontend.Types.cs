using Koh.Compiler.Ir;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Frontends.CSharp;

// Type resolution and compile-time constant folding.
public sealed partial class CSharpFrontend
{
    internal static CsType ResolveType(TypeSyntax type, IReadOnlyDictionary<string, CsEnum> enums)
    {
        if (type is PredefinedTypeSyntax predefined && CsType.FromKeyword(predefined.Keyword.Kind()) is { } t)
            return t;
        // Int128/UInt128 have no keyword; they arrive as type names.
        if (type is IdentifierNameSyntax { Identifier.Text: "Int128" })
            return CsType.I128;
        if (type is IdentifierNameSyntax { Identifier.Text: "UInt128" })
            return CsType.U128;
        if (type is IdentifierNameSyntax id && enums.TryGetValue(id.Identifier.Text, out var e))
            return e.Underlying;
        if (type is PointerTypeSyntax pointer)
            return new CsType(IrType.Pointer(ResolveType(pointer.ElementType, enums).Ir), Signed: false);
        throw new CSharpNotSupportedException(
            $"unsupported type '{type}' (Koh C# supports byte/sbyte/ushort/short/bool, enums, and pointers).",
            type.GetLocation());
    }

    private static CsType? ResolveReturnType(TypeSyntax type, IReadOnlyDictionary<string, CsEnum> enums)
    {
        if (type is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword })
            return null;
        return ResolveType(type, enums);
    }

    /// <summary>Resolve a field/parameter/return type, mapping a class name to the class's heap pointer
    /// (a class instance is passed and stored as <c>byte*</c>). Falls back to the scalar resolver.</summary>
    private static CsType ResolveTypeAllowingClass(
        TypeSyntax type, IReadOnlyDictionary<string, CsEnum> enums, IReadOnlySet<string> classNames) =>
        type is IdentifierNameSyntax id && classNames.Contains(id.Identifier.Text)
            ? new CsType(IrType.Pointer(IrType.I8), Signed: false)
            : ResolveType(type, enums);

    private static CsType? ResolveReturnTypeAllowingClass(
        TypeSyntax type, IReadOnlyDictionary<string, CsEnum> enums, IReadOnlySet<string> classNames)
    {
        if (type is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword })
            return null;
        return ResolveTypeAllowingClass(type, enums, classNames);
    }

    private static byte[] ToLittleEndian(long value, int size)
    {
        var bytes = new byte[size];
        for (int i = 0; i < size; i++)
            bytes[i] = (byte)(value >> (8 * i));
        return bytes;
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
