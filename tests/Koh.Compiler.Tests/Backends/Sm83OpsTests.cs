using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Ir;

namespace Koh.Compiler.Tests.Backends;

/// <summary>Unit tests for the pure SM83 encoding helpers now that they are a standalone class.</summary>
public class Sm83OpsTests
{
    [Test]
    public async Task AluOpcodes_UseCarryFormForHighBytes()
    {
        // Byte 0 uses the non-carry opcode; higher bytes chain the carry.
        await Assert.That(Sm83Ops.AluImmOpcode(IrBinaryOp.Add, 0)).IsEqualTo((byte)0xC6); // ADD A,d8
        await Assert.That(Sm83Ops.AluImmOpcode(IrBinaryOp.Add, 1)).IsEqualTo((byte)0xCE); // ADC A,d8
        await Assert.That(Sm83Ops.AluImmOpcode(IrBinaryOp.Sub, 0)).IsEqualTo((byte)0xD6); // SUB d8
        await Assert.That(Sm83Ops.AluImmOpcode(IrBinaryOp.Sub, 3)).IsEqualTo((byte)0xDE); // SBC A,d8
        await Assert.That(Sm83Ops.AluRegOpcode(IrBinaryOp.Add, 0)).IsEqualTo((byte)0x80); // ADD A,B
        await Assert.That(Sm83Ops.AluRegOpcode(IrBinaryOp.Add, 1)).IsEqualTo((byte)0x88); // ADC A,B
        await Assert.That(Sm83Ops.AluImmOpcode(IrBinaryOp.And, 0)).IsEqualTo((byte)0xE6); // bitwise ops ignore k
    }

    [Test]
    public async Task ByteOf_SignExtendsBeyond64Bits()
    {
        // Bytes 0..7 are the value's bytes; bytes 8..15 are its sign extension (not a repeat of the low
        // bytes, which a raw shift would give since C# masks the shift count to 63).
        var pos = IrBuilder.ConstInt(IrType.Int(128), 0x0102030405060708);
        await Assert.That(Sm83Ops.ByteOf(pos, 0)).IsEqualTo((byte)0x08);
        await Assert.That(Sm83Ops.ByteOf(pos, 7)).IsEqualTo((byte)0x01);
        await Assert.That(Sm83Ops.ByteOf(pos, 8)).IsEqualTo((byte)0x00);  // positive -> zero-extended
        await Assert.That(Sm83Ops.ByteOf(pos, 15)).IsEqualTo((byte)0x00);
        var neg = IrBuilder.ConstInt(IrType.Int(128), -1);
        await Assert.That(Sm83Ops.ByteOf(neg, 0)).IsEqualTo((byte)0xFF);
        await Assert.That(Sm83Ops.ByteOf(neg, 15)).IsEqualTo((byte)0xFF); // negative -> sign-extended
    }

    [Test]
    public async Task Normalize_SwapsAndSignsPredicates()
    {
        // `a > b` becomes `b < a` (swap); signed predicates share the unsigned base with Signed = true.
        await Assert.That(Sm83Ops.Normalize(IrCompareOp.Ugt)).IsEqualTo((IrCompareOp.Ult, true, false));
        await Assert.That(Sm83Ops.Normalize(IrCompareOp.Sle)).IsEqualTo((IrCompareOp.Uge, true, true));
        await Assert.That(Sm83Ops.Normalize(IrCompareOp.Eq)).IsEqualTo((IrCompareOp.Eq, false, false));
    }
}
