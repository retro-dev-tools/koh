using Koh.Compiler.Ir;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Frontends.CSharp;

// Type-NAME resolution and compile-time constant folding. Since Stage-2 P6, type-name resolution is
// symbol-first, like every other name-resolution site in the frontend (see CSharpSemantics.Enums/
// Structs/Classes): a predefined-keyword type never needs a symbol (CsType.FromKeyword is exact and
// free), but a user enum/Int128/UInt128 identifier resolves via the semantic model first, falling back
// to spelled text only where consulting a symbol would be unsafe — never because no symbol COULD
// resolve (every ResolveType/ResolveTypeAllowingClass caller passes a real node from the main or
// instances tree; a detached node, the pre-migration problem the string tables originally guarded
// against, cannot reach either function post-Stage-2-P2). The one genuine hazard is temporal, not
// structural: CSharpSemantics.Enums/Classes are lazily materialized from a registration LIST that fills
// in as each declaration pass runs (CollectEnums, CollectClasses) — reading the index before every
// relevant Register* call has fired freezes it incomplete FOREVER (Lazy&lt;T&gt; caches its first
// answer), silently dropping every declaration registered after that point from every later reader, not
// just the one in progress. `ConstEval`'s `Enum.Member` arm (consulted from CollectEnums itself, for a
// member initializer that references another enum) and `ResolveTypeAllowingClass` (consulted from
// CollectClasses itself, for a field whose type is a possibly-later-declared or self-referential class)
// both hit exactly this window; both take the safe route by NOT touching the symbol-keyed index while
// their own collection pass is still running — see each one's own remarks.
public sealed partial class CSharpFrontend
{
    /// <summary>Whether <paramref name="sym"/> (the resolved symbol of an <see cref="IdentifierNameSyntax"/>,
    /// or null if none resolved) names the BCL <c>System.Int128</c>/<c>UInt128</c> struct. Symbol-first
    /// (checked by namespace + name, not spelling), falling back to the raw identifier text only when no
    /// symbol resolved at all — never when a symbol resolved to something else (a user type that merely
    /// happens to be named "Int128" must NOT be misread as the 128-bit integer).</summary>
    private static bool IsBigIntName(
        INamedTypeSymbol? sym,
        IdentifierNameSyntax idName,
        string name
    ) =>
        sym is null
            ? idName.Identifier.Text == name
            : sym.ContainingNamespace?.Name == "System" && sym.Name == name;

    /// <param name="semantics">The resolution oracle, or null only from <see cref="CollectEnums"/>'s own
    /// call for an enum's base-list type — resolving THAT type by symbol could force
    /// <see cref="CSharpSemantics.Enums"/> to materialize before every enum in the file is registered
    /// (see this file's header remarks); every other caller always has a real, already-safe
    /// <see cref="CSharpSemantics"/> to pass. A null oracle still resolves every predefined keyword and
    /// Int128/UInt128 by text (an enum's base type is never itself a user enum in valid C#), just not a
    /// user enum name.</param>
    internal static CsType ResolveType(TypeSyntax type, CSharpSemantics? semantics)
    {
        if (
            type is PredefinedTypeSyntax predefined
            && CsType.FromKeyword(predefined.Keyword.Kind()) is { } t
        )
            return t;
        // Int128/UInt128 have no keyword; they arrive as type names. A user enum's own type name also
        // arrives this way — resolved via the symbol it names (see IsBigIntName/CSharpSemantics.Enums).
        if (type is IdentifierNameSyntax idName)
        {
            var sym = semantics?.Sym(idName) as INamedTypeSymbol;
            if (IsBigIntName(sym, idName, "Int128"))
                return CsType.I128;
            if (IsBigIntName(sym, idName, "UInt128"))
                return CsType.U128;
            if (
                semantics is not null
                && sym is not null
                && semantics.Enums.TryGetValue(sym, out var e)
            )
                return e.Underlying;
        }
        if (type is PointerTypeSyntax pointer)
            return new CsType(
                IrType.Pointer(ResolveType(pointer.ElementType, semantics).Ir),
                Signed: false
            );
        throw new CSharpNotSupportedException(
            $"unsupported type '{type}' (Koh C# supports byte/sbyte/ushort/short/bool, enums, and pointers).",
            type.GetLocation()
        );
    }

    /// <summary>Resolve a field/parameter/return type, mapping a class name to the class's heap pointer
    /// (a class instance is passed and stored as <c>byte*</c>). Falls back to the scalar resolver.</summary>
    /// <param name="classIndexSafe">False only from <see cref="CollectClasses"/>'s own field-layout pass:
    /// a field there may name a class not yet registered (a self- or later-declared reference, e.g. a
    /// linked-list node) — consulting <see cref="CSharpSemantics.Classes"/> there would force it to
    /// materialize before every class in the file is registered, freezing it incomplete forever (see this
    /// file's header remarks). <paramref name="classNames"/> (every class name in the file, gathered up
    /// front before that pass runs — see <see cref="CollectClasses"/>) is the safe check for that one
    /// caller; every other caller runs after <see cref="CollectClasses"/> fully completes, when the index
    /// is safe, and passes true (the default) to get the real symbol-first classification.</param>
    private static CsType ResolveTypeAllowingClass(
        TypeSyntax type,
        CSharpSemantics semantics,
        IReadOnlySet<string> classNames,
        bool classIndexSafe = true
    )
    {
        if (type is IdentifierNameSyntax id)
        {
            if (
                classIndexSafe
                && semantics.Sym(id) is INamedTypeSymbol sym
                && semantics.Classes.TryGetValue(sym, out _)
            )
                return new CsType(IrType.Pointer(IrType.I8), Signed: false);
            if (classNames.Contains(id.Identifier.Text))
                return new CsType(IrType.Pointer(IrType.I8), Signed: false);
        }
        return ResolveType(type, semantics);
    }

    private static CsType? ResolveReturnTypeAllowingClass(
        TypeSyntax type,
        CSharpSemantics semantics,
        IReadOnlySet<string> classNames
    )
    {
        if (type is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword })
            return null;
        return ResolveTypeAllowingClass(type, semantics, classNames);
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
            throw new CSharpNotSupportedException(
                $"constant {value} does not fit in {size} byte(s)."
            );
        var bytes = new byte[size];
        for (int i = 0; i < size; i++)
            bytes[i] = (byte)(value >> (8 * i));
        return bytes;
    }

    /// <summary>The integer value of a literal token, or a diagnostic for a non-integer literal (string,
    /// float, …). A <c>ulong</c> above <c>long.MaxValue</c> keeps its bit pattern (a 64-bit constant).</summary>
    private static long LiteralToLong(LiteralExpressionSyntax lit) =>
        lit.Token.Value switch
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
                $"'{lit}' is not an integer constant.",
                lit.GetLocation()
            ),
        };

    /// <summary>Resolve a <c>Type.Member</c> constant reference to its folded value: symbol-first via
    /// <paramref name="semantics"/>'s <see cref="CSharpSemantics.Enums"/> index (the same <see
    /// cref="CsEnum"/> instance <paramref name="enums"/> — the declaration-pass-local, incrementally-built
    /// string table — holds), falling back to <paramref name="enums"/> by spelled text when
    /// <paramref name="semantics"/> is null. The value itself always comes from that <see cref="CsEnum"/>'s
    /// own folded <c>Members</c> table either way (this never re-derives a value from Roslyn's own constant
    /// folding — Koh's arithmetic rules, not C#'s, are authoritative for it, same as everywhere else).
    /// <paramref name="semantics"/> is null only from <see cref="CollectEnums"/>'s own call (self- and
    /// forward-referential enum members: <c>enum E : ushort { A = 1, B = A + 1 }</c>, or one enum's member
    /// initializer referencing another enum entirely) — consulting <see cref="CSharpSemantics.Enums"/>
    /// there, mid-collection, would force it to materialize before every enum in the file is registered,
    /// silently freezing it incomplete forever (<see cref="Lazy{T}"/> caches its first read) for every
    /// later reader in the compile, not just this one. <paramref name="enums"/> — the enums collected SO
    /// FAR in file order, growing across the very call chain this recurses through — is exactly as safe
    /// there as it always was: a forward reference to a not-yet-declared enum already didn't resolve under
    /// this same text lookup before Stage-2 P6, so nothing regresses; every OTHER caller (post-collection)
    /// passes a real <paramref name="semantics"/> and gets genuine symbol-first resolution.</summary>
    private static bool TryEnumMember(
        MemberAccessExpressionSyntax access,
        IdentifierNameSyntax typeName,
        string memberName,
        IReadOnlyDictionary<string, CsEnum>? enums,
        CSharpSemantics? semantics,
        out long value
    )
    {
        CsEnum? found = null;
        if (
            semantics is not null
            && semantics.Sym(access) is IFieldSymbol { ContainingType: { } enumType }
            && semantics.Enums.TryGetValue(enumType, out var bySymbol)
        )
            found = bySymbol;
        else if (enums is not null && enums.TryGetValue(typeName.Identifier.Text, out var byName))
            found = byName;
        if (found is not null && found.Members.TryGetValue(memberName, out value))
            return true;
        value = 0;
        return false;
    }

    /// <summary>Fold a constant expression to a long, resolving bare names via <paramref name="lookup"/>
    /// and qualified <c>Enum.Member</c> references via <see cref="TryEnumMember"/> (symbol-first through
    /// <paramref name="semantics"/>, text-first through <paramref name="enums"/> — see its remarks for
    /// when each applies).</summary>
    /// <param name="unsigned">Evaluate <c>&gt;&gt;</c>, <c>/</c>, and <c>%</c> as unsigned — set for an
    /// unsigned-typed constant so a value with bit 63 set shifts/divides logically, not arithmetically.</param>
    internal static long ConstEval(
        ExpressionSyntax expr,
        Func<string, long?> lookup,
        IReadOnlyDictionary<string, CsEnum>? enums = null,
        bool unsigned = false,
        CSharpSemantics? semantics = null
    ) =>
        expr switch
        {
            ParenthesizedExpressionSyntax p => ConstEval(
                p.Expression,
                lookup,
                enums,
                unsigned,
                semantics
            ),
            LiteralExpressionSyntax lit => LiteralToLong(lit),
            IdentifierNameSyntax id => lookup(id.Identifier.Text)
                ?? throw new CSharpNotSupportedException(
                    $"'{id.Identifier.Text}' is not a constant.",
                    id.GetLocation()
                ),
            MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax type,
                Name: IdentifierNameSyntax member
            } access
                when TryEnumMember(
                    access,
                    type,
                    member.Identifier.Text,
                    enums,
                    semantics,
                    out var mv
                ) => mv,
            // A cast in a constant is transparent to folding; the declared const/element width is enforced
            // when the value is stored (FitsInBytes / ToLittleEndian).
            CastExpressionSyntax cast => ConstEval(
                cast.Expression,
                lookup,
                enums,
                unsigned,
                semantics
            ),
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } u =>
                -ConstEval(u.Operand, lookup, enums, unsigned, semantics),
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryPlusExpression } u =>
                ConstEval(u.Operand, lookup, enums, unsigned, semantics),
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.BitwiseNotExpression } u =>
                ~ConstEval(u.Operand, lookup, enums, unsigned, semantics),
            BinaryExpressionSyntax bin => FoldBinary(bin, lookup, enums, unsigned, semantics),
            _ => throw new CSharpNotSupportedException(
                $"'{expr}' is not a constant expression.",
                expr.GetLocation()
            ),
        };

    private static long FoldBinary(
        BinaryExpressionSyntax bin,
        Func<string, long?> lookup,
        IReadOnlyDictionary<string, CsEnum>? enums,
        bool unsigned,
        CSharpSemantics? semantics
    )
    {
        long l = ConstEval(bin.Left, lookup, enums, unsigned, semantics),
            r = ConstEval(bin.Right, lookup, enums, unsigned, semantics);
        // Division/remainder guard the zero and MinValue/-1 traps so a bad constant is a diagnostic, not
        // an uncaught DivideByZeroException/OverflowException that would escape the frontend. For an
        // unsigned constant, shift/divide/modulo are logical/unsigned (no sign extension, no -1 trap).
        return bin.Kind() switch
        {
            SyntaxKind.AddExpression => unchecked(l + r),
            SyntaxKind.SubtractExpression => unchecked(l - r),
            SyntaxKind.MultiplyExpression => unchecked(l * r),
            SyntaxKind.DivideExpression => r == 0
                ? throw new CSharpNotSupportedException(
                    $"division by zero in constant '{bin}'.",
                    bin.GetLocation()
                )
            : unsigned ? (long)((ulong)l / (ulong)r)
            : r == -1 ? unchecked(-l)
            : l / r,
            SyntaxKind.ModuloExpression => r == 0
                ? throw new CSharpNotSupportedException(
                    $"division by zero in constant '{bin}'.",
                    bin.GetLocation()
                )
            : unsigned ? (long)((ulong)l % (ulong)r)
            : r == -1 ? 0
            : l % r,
            SyntaxKind.BitwiseAndExpression => l & r,
            SyntaxKind.BitwiseOrExpression => l | r,
            SyntaxKind.ExclusiveOrExpression => l ^ r,
            SyntaxKind.LeftShiftExpression => l << (int)r,
            SyntaxKind.RightShiftExpression => unsigned ? (long)((ulong)l >> (int)r) : l >> (int)r,
            _ => throw new CSharpNotSupportedException(
                $"'{bin}' is not a constant expression.",
                bin.GetLocation()
            ),
        };
    }
}
