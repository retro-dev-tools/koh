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

    /// <summary>Whether <paramref name="value"/> fits in <paramref name="size"/> bytes as either a signed
    /// or an unsigned integer (so both <c>-1</c> and <c>255</c> fit one byte).</summary>
    internal static bool FitsInBytes(long value, int size)
    {
        if (size >= 8)
            return true; // a 64-bit value always fits 8 bytes
        int bits = 8 * size;
        long zeroExt = value & ((1L << bits) - 1);
        long signExt = zeroExt << (64 - bits) >> (64 - bits);
        return zeroExt == value || signExt == value;
    }

    /// <summary>Encode a folded constant into <paramref name="size"/> little-endian bytes, rejecting a
    /// value that does not fit rather than silently truncating it.</summary>
    private static byte[] ToLittleEndian(long value, int size)
    {
        if (!FitsInBytes(value, size))
            throw new CSharpNotSupportedException($"constant {value} does not fit in {size} byte(s).");
        var bytes = new byte[size];
        for (int i = 0; i < size; i++)
            bytes[i] = (byte)(value >> (8 * i));
        return bytes;
    }

    /// <summary>The integer value of a literal token, or a diagnostic for a non-integer literal (string,
    /// float, …). A <c>ulong</c> above <c>long.MaxValue</c> keeps its bit pattern (a 64-bit constant).</summary>
    private static long LiteralToLong(LiteralExpressionSyntax lit) => lit.Token.Value switch
    {
        int i => i,
        uint u => u,
        long l => l,
        ulong u => unchecked((long)u),
        byte b => b,
        sbyte s => s,
        short s => s,
        ushort u => u,
        char c => c,
        bool b => b ? 1 : 0,
        _ => throw new CSharpNotSupportedException(
            $"'{lit}' is not an integer constant.", lit.GetLocation()),
    };

    /// <summary>Fold a constant expression to a long, resolving bare names via <paramref name="lookup"/>
    /// and qualified <c>Enum.Member</c> references via <paramref name="enums"/>.</summary>
    /// <param name="unsigned">Evaluate <c>&gt;&gt;</c>, <c>/</c>, and <c>%</c> as unsigned — set for an
    /// unsigned-typed constant so a value with bit 63 set shifts/divides logically, not arithmetically.</param>
    internal static long ConstEval(ExpressionSyntax expr, Func<string, long?> lookup,
        IReadOnlyDictionary<string, CsEnum>? enums = null, bool unsigned = false) => expr switch
    {
        ParenthesizedExpressionSyntax p => ConstEval(p.Expression, lookup, enums, unsigned),
        LiteralExpressionSyntax lit => LiteralToLong(lit),
        IdentifierNameSyntax id => lookup(id.Identifier.Text)
            ?? throw new CSharpNotSupportedException($"'{id.Identifier.Text}' is not a constant.", id.GetLocation()),
        MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax type, Name: IdentifierNameSyntax member }
            when enums is not null && enums.TryGetValue(type.Identifier.Text, out var en)
                 && en.Members.TryGetValue(member.Identifier.Text, out var mv) => mv,
        // A cast in a constant is transparent to folding; the declared const/element width is enforced
        // when the value is stored (FitsInBytes / ToLittleEndian).
        CastExpressionSyntax cast => ConstEval(cast.Expression, lookup, enums, unsigned),
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } u => -ConstEval(u.Operand, lookup, enums, unsigned),
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryPlusExpression } u => ConstEval(u.Operand, lookup, enums, unsigned),
        PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.BitwiseNotExpression } u => ~ConstEval(u.Operand, lookup, enums, unsigned),
        BinaryExpressionSyntax bin => FoldBinary(bin, lookup, enums, unsigned),
        _ => throw new CSharpNotSupportedException($"'{expr}' is not a constant expression.", expr.GetLocation()),
    };

    private static long FoldBinary(BinaryExpressionSyntax bin, Func<string, long?> lookup,
        IReadOnlyDictionary<string, CsEnum>? enums, bool unsigned)
    {
        long l = ConstEval(bin.Left, lookup, enums, unsigned), r = ConstEval(bin.Right, lookup, enums, unsigned);
        // Division/remainder guard the zero and MinValue/-1 traps so a bad constant is a diagnostic, not
        // an uncaught DivideByZeroException/OverflowException that would escape the frontend. For an
        // unsigned constant, shift/divide/modulo are logical/unsigned (no sign extension, no -1 trap).
        return bin.Kind() switch
        {
            SyntaxKind.AddExpression => unchecked(l + r),
            SyntaxKind.SubtractExpression => unchecked(l - r),
            SyntaxKind.MultiplyExpression => unchecked(l * r),
            SyntaxKind.DivideExpression => r == 0
                ? throw new CSharpNotSupportedException($"division by zero in constant '{bin}'.", bin.GetLocation())
                : unsigned ? (long)((ulong)l / (ulong)r)
                : r == -1 ? unchecked(-l) : l / r,
            SyntaxKind.ModuloExpression => r == 0
                ? throw new CSharpNotSupportedException($"division by zero in constant '{bin}'.", bin.GetLocation())
                : unsigned ? (long)((ulong)l % (ulong)r)
                : r == -1 ? 0 : l % r,
            SyntaxKind.BitwiseAndExpression => l & r,
            SyntaxKind.BitwiseOrExpression => l | r,
            SyntaxKind.ExclusiveOrExpression => l ^ r,
            SyntaxKind.LeftShiftExpression => l << (int)r,
            SyntaxKind.RightShiftExpression => unsigned ? (long)((ulong)l >> (int)r) : l >> (int)r,
            _ => throw new CSharpNotSupportedException($"'{bin}' is not a constant expression.", bin.GetLocation()),
        };
    }
}
