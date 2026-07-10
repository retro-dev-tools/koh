using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;

namespace Koh.Compiler.Tests.Ir;

public class RedundantBankSelectEliminationTests
{
    private static (IrModule Module, IrFunction Fn, IrBuilder B, IrBasicBlock Entry) NewFn(
        params IrParameter[] parameters
    )
    {
        var module = new IrModule("test");
        var fn = new IrFunction("f", IrType.Void, parameters);
        module.Functions.Add(fn);
        var entry = fn.AppendBlock("entry");
        var b = new IrBuilder();
        b.PositionAtEnd(entry);
        return (module, fn, b, entry);
    }

    /// <summary>A pointer to a fixed absolute address, as the frontend forms <c>(byte*)addr</c>: a
    /// bitcast of an integer constant to a byte pointer.</summary>
    private static IrValue AddressPtr(IrBuilder b, int address) =>
        b.Conv(
            IrConvOp.Bitcast,
            IrBuilder.ConstInt(IrType.I16, address),
            IrType.Pointer(IrType.I8)
        );

    /// <summary>Emit <c>*(byte*)0x2000 = bank;</c> for a constant bank.</summary>
    private static void SelectBank(IrBuilder b, int bank) =>
        b.Store(IrBuilder.ConstInt(IrType.I8, bank), AddressPtr(b, 0x2000));

    private static int StoreCount(IrBasicBlock block) =>
        block.Instructions.OfType<StoreInstruction>().Count();

    [Test]
    public async Task RemovesConsecutiveSelectsOfTheSameBank()
    {
        var (module, fn, b, entry) = NewFn();
        SelectBank(b, 3);
        b.Load(AddressPtr(b, 0x4000)); // read a banked global — does not change the mapped bank
        SelectBank(b, 3); // redundant: bank 3 is still mapped
        b.Ret();

        var changed = new RedundantBankSelectEliminationPass().Run(fn);

        await Assert.That(changed).IsTrue();
        await Assert.That(StoreCount(entry)).IsEqualTo(1);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task KeepsSelectsOfDifferentBanks()
    {
        var (module, fn, b, entry) = NewFn();
        SelectBank(b, 3);
        SelectBank(b, 4); // a different bank — must stay
        b.Ret();

        var changed = new RedundantBankSelectEliminationPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert.That(StoreCount(entry)).IsEqualTo(2);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task CallInvalidatesKnownBank()
    {
        var (module, fn, b, entry) = NewFn();
        var callee = new IrFunction("g", IrType.Void, [], isExternal: true);
        module.Functions.Add(callee);

        SelectBank(b, 3);
        b.Call(callee, []); // g may switch banks — the re-select below is not redundant
        SelectBank(b, 3);
        b.Ret();

        var changed = new RedundantBankSelectEliminationPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert.That(StoreCount(entry)).IsEqualTo(2);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task OrdinaryMemoryWriteIsTransparent()
    {
        var (module, fn, b, entry) = NewFn();
        SelectBank(b, 3);
        b.Store(IrBuilder.ConstInt(IrType.I8, 42), AddressPtr(b, 0xC000)); // WRAM — no effect on bank
        SelectBank(b, 3); // still redundant
        b.Ret();

        var changed = new RedundantBankSelectEliminationPass().Run(fn);

        await Assert.That(changed).IsTrue();
        await Assert.That(StoreCount(entry)).IsEqualTo(2); // the WRAM store plus one surviving select
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task WriteToAnotherMbcRegisterInvalidates()
    {
        var (module, fn, b, entry) = NewFn();
        SelectBank(b, 3);
        b.Store(IrBuilder.ConstInt(IrType.I8, 0), AddressPtr(b, 0x0000)); // RAM-enable / MBC control
        SelectBank(b, 3); // conservatively not proven redundant
        b.Ret();

        var changed = new RedundantBankSelectEliminationPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }

    [Test]
    public async Task VariableBankSelectClearsKnownBank()
    {
        var bank = new IrParameter("bank", IrType.I8);
        var (module, fn, b, entry) = NewFn(bank);
        SelectBank(b, 3);
        b.Store(bank, AddressPtr(b, 0x2000)); // select a runtime bank — now unknown
        SelectBank(b, 3); // cannot prove bank 3 is mapped — keep
        b.Ret();

        var changed = new RedundantBankSelectEliminationPass().Run(fn);

        await Assert.That(changed).IsFalse();
        await Assert.That(StoreCount(entry)).IsEqualTo(3);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();
    }
}
