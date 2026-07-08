using Koh.Compiler.Ir;
using Microsoft.CodeAnalysis.CSharp;

namespace Koh.Compiler.Frontends.CSharp;

/// <summary>
/// A Koh C# value type: its IR representation plus signedness (which selects signed vs. unsigned
/// divide/remainder/shift/compare). The subset is deliberately C-like — arithmetic on 8-bit types
/// stays 8-bit rather than promoting to <c>int</c> the way standard C# does.
/// </summary>
internal readonly record struct CsType(IrType Ir, bool Signed)
{
    public static readonly CsType Bool = new(IrType.I8, false);
    public static readonly CsType U8 = new(IrType.I8, false);
    public static readonly CsType I8 = new(IrType.I8, true);
    public static readonly CsType U16 = new(IrType.I16, false);
    public static readonly CsType I16 = new(IrType.I16, true);
    public static readonly CsType U32 = new(IrType.I32, false);
    public static readonly CsType I32 = new(IrType.I32, true);
    public static readonly CsType U64 = new(IrType.I64, false);
    public static readonly CsType I64 = new(IrType.I64, true);
    public static readonly CsType U128 = new(IrType.Int(128), false);
    public static readonly CsType I128 = new(IrType.Int(128), true);

    public int Bits => Ir.Bits;

    /// <summary>Map a C# predefined-type keyword to a Koh C# type, or null if unsupported.</summary>
    public static CsType? FromKeyword(SyntaxKind keyword) => keyword switch
    {
        SyntaxKind.ByteKeyword => U8,
        SyntaxKind.SByteKeyword => I8,
        SyntaxKind.UShortKeyword => U16,
        SyntaxKind.ShortKeyword => I16,
        SyntaxKind.UIntKeyword => U32,
        SyntaxKind.IntKeyword => I32,
        SyntaxKind.ULongKeyword => U64,
        SyntaxKind.LongKeyword => I64,
        SyntaxKind.BoolKeyword => Bool,
        _ => null,
    };
}
