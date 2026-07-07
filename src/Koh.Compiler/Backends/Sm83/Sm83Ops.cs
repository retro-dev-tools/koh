using Koh.Compiler.Ir;

namespace Koh.Compiler.Backends.Sm83;

/// <summary>Pure SM83 encoding helpers used by the code generator: constant byte extraction, comparison
/// normalization, and the ALU opcode tables. Stateless, so they can be unit-tested in isolation.</summary>
internal static class Sm83Ops
{
    /// <summary>Byte <paramref name="k"/> (0 = low) of a constant. The value is a 64-bit long; bytes 8..15
    /// of a wider (i128) constant are its sign extension — a raw <c>value >> (8*k)</c> would be wrong
    /// because C# masks the shift count to 63, replicating the low bytes for k &gt;= 8.</summary>
    public static byte ByteOf(IrValue value, int k) =>
        value is IrConstInt c
            ? (byte)(k < 8 ? c.Value >> (8 * k) : c.Value >> 63)
            : throw new NotSupportedException("expected a constant operand.");

    /// <summary>Reduce any predicate to a base carry/eq test plus operand-swap and sign flags:
    /// <c>Ugt/Ule</c> swap into <c>Ult/Uge</c>; signed predicates map to the same base with
    /// <c>Signed = true</c> (handled by flipping the top byte's sign bit).</summary>
    public static (IrCompareOp Pred, bool Swap, bool Signed) Normalize(IrCompareOp op) => op switch
    {
        IrCompareOp.Eq => (IrCompareOp.Eq, false, false),
        IrCompareOp.Ne => (IrCompareOp.Ne, false, false),
        IrCompareOp.Ult => (IrCompareOp.Ult, false, false),
        IrCompareOp.Uge => (IrCompareOp.Uge, false, false),
        IrCompareOp.Ugt => (IrCompareOp.Ult, true, false),  // a > b  <=>  b < a
        IrCompareOp.Ule => (IrCompareOp.Uge, true, false),  // a <= b <=>  b >= a
        IrCompareOp.Slt => (IrCompareOp.Ult, false, true),
        IrCompareOp.Sge => (IrCompareOp.Uge, false, true),
        IrCompareOp.Sgt => (IrCompareOp.Ult, true, true),   // a > b  <=>  b < a
        IrCompareOp.Sle => (IrCompareOp.Uge, true, true),   // a <= b <=>  b >= a
        _ => throw new NotSupportedException($"SM83 backend cannot lower comparison {op}."),
    };

    /// <summary>The immediate-operand ALU opcode for byte <paramref name="k"/> of a wide operation
    /// (<c>k == 0</c> uses the non-carry form; higher bytes chain the carry).</summary>
    public static byte AluImmOpcode(IrBinaryOp op, int k) => op switch
    {
        IrBinaryOp.Add => (byte)(k == 0 ? 0xC6 : 0xCE), // ADD A,d8 / ADC A,d8
        IrBinaryOp.Sub => (byte)(k == 0 ? 0xD6 : 0xDE), // SUB d8   / SBC A,d8
        IrBinaryOp.And => 0xE6,
        IrBinaryOp.Or => 0xF6,
        IrBinaryOp.Xor => 0xEE,
        _ => throw new NotSupportedException($"SM83 backend does not support '{op}'."),
    };

    /// <summary>The register-operand (B) ALU opcode for byte <paramref name="k"/> of a wide operation.</summary>
    public static byte AluRegOpcode(IrBinaryOp op, int k) => op switch
    {
        IrBinaryOp.Add => (byte)(k == 0 ? 0x80 : 0x88), // ADD A,B / ADC A,B
        IrBinaryOp.Sub => (byte)(k == 0 ? 0x90 : 0x98), // SUB B   / SBC A,B
        IrBinaryOp.And => 0xA0,
        IrBinaryOp.Or => 0xB0,
        IrBinaryOp.Xor => 0xA8,
        _ => throw new NotSupportedException($"SM83 backend does not support '{op}'."),
    };
}
