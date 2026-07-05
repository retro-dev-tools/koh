using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Ir;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Backends;

/// <summary>End-to-end tests for control flow, 16-bit integers, and static-address memory ops.</summary>
public class Sm83ControlFlowTests
{
    private static EmitModel Compile(IrModule m) => new Sm83Backend().Compile(m, new DiagnosticBag());

    private static GameBoySystem Load(EmitModel model, out int start, out int length)
    {
        var link = new LinkerType().Link([new LinkerInput("mvp", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("link produced no ROM");
        start = Sm83Backend.CodeBase;
        length = model.Sections[0].Data.Length;
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start;
        return gb;
    }

    private static void Run(GameBoySystem gb, int start, int length)
    {
        for (int steps = 0; steps < 100_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= start + length)
                break;
            gb.StepInstruction();
        }
    }

    private static byte RunA(IrModule module, Action<GameBoySystem>? setup = null)
    {
        var gb = Load(Compile(module), out int s, out int l);
        setup?.Invoke(gb);
        Run(gb, s, l);
        return gb.Registers.A;
    }

    private static ushort RunHL(IrModule module, Action<GameBoySystem>? setup = null)
    {
        var gb = Load(Compile(module), out int s, out int l);
        setup?.Invoke(gb);
        Run(gb, s, l);
        return gb.Registers.HL;
    }

    /// <summary>Build <c>func @main() : returnType { entry: ret body(builder) }</c>.</summary>
    private static IrModule Fn(IrType returnType, Func<IrBuilder, IrValue> body)
    {
        var module = new IrModule("t");
        var fn = new IrFunction("main", returnType, []);
        module.Functions.Add(fn);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        b.Ret(body(b));
        return module;
    }

    private static IrConstInt I16(int v) => IrBuilder.ConstInt(IrType.I16, v);
    private static IrConstInt I8(int v) => IrBuilder.ConstInt(IrType.I8, v);

    // ---- 16-bit arithmetic --------------------------------------------------

    [Test]
    public async Task I16_Add_CarriesAcrossBytes()
    {
        // 300 + 250 = 550 (exercises ADC across the low/high byte boundary)
        await Assert.That(RunHL(Fn(IrType.I16, b => b.Add(I16(300), I16(250))))).IsEqualTo((ushort)550);
    }

    [Test]
    public async Task I16_Sub_BorrowsAcrossBytes()
    {
        // 300 - 150 = 150 (low byte 0x2C - 0x96 borrows into the high byte via SBC)
        await Assert.That(RunHL(Fn(IrType.I16, b =>
        {
            var a = b.Add(I16(200), I16(100)); // 300
            var c = b.Add(I16(100), I16(50));  // 150
            return b.Sub(a, c);
        }))).IsEqualTo((ushort)150);
    }

    // ---- 16-bit comparisons -------------------------------------------------

    [Test]
    public async Task I16_Ult_True() =>
        await Assert.That(RunA(Fn(IrType.I8, b => b.Compare(IrCompareOp.Ult, I16(5), I16(10))))).IsEqualTo((byte)1);

    [Test]
    public async Task I16_Ugt_True_ViaSwap() =>
        await Assert.That(RunA(Fn(IrType.I8, b => b.Compare(IrCompareOp.Ugt, I16(10), I16(5))))).IsEqualTo((byte)1);

    [Test]
    public async Task I16_Eq_False() =>
        await Assert.That(RunA(Fn(IrType.I8, b => b.Compare(IrCompareOp.Eq, I16(7), I16(8))))).IsEqualTo((byte)0);

    // ---- signed comparisons -------------------------------------------------

    [Test]
    public async Task Slt_Signed_NegativeLessThanPositive() =>
        await Assert.That(RunA(Fn(IrType.I8, b => b.Compare(IrCompareOp.Slt, I8(-5), I8(3))))).IsEqualTo((byte)1);

    [Test]
    public async Task Sgt_Signed_NegativeOrdering() =>
        await Assert.That(RunA(Fn(IrType.I8, b => b.Compare(IrCompareOp.Sgt, I8(-1), I8(-2))))).IsEqualTo((byte)1);

    [Test]
    public async Task SignedVsUnsigned_DifferForNegatives()
    {
        // -1 < 1 signed is true; as unsigned (0xFF < 1) it is false.
        await Assert.That(RunA(Fn(IrType.I8, b => b.Compare(IrCompareOp.Slt, I8(-1), I8(1))))).IsEqualTo((byte)1);
        await Assert.That(RunA(Fn(IrType.I8, b => b.Compare(IrCompareOp.Ult, I8(-1), I8(1))))).IsEqualTo((byte)0);
    }

    [Test]
    public async Task Slt_Signed_I16() =>
        await Assert.That(RunA(Fn(IrType.I8, b => b.Compare(IrCompareOp.Slt, I16(-1000), I16(5))))).IsEqualTo((byte)1);

    // ---- conversions --------------------------------------------------------

    [Test]
    public async Task ZExt_I8_To_I16() =>
        await Assert.That(RunHL(Fn(IrType.I16, b => b.Conv(IrConvOp.ZExt, I8(200), IrType.I16)))).IsEqualTo((ushort)200);

    [Test]
    public async Task SExt_I8_To_I16_SignExtendsHighByte() =>
        // 200 as signed i8 is -56; sign-extended to i16 that is 0xFFC8 = 65480
        await Assert.That(RunHL(Fn(IrType.I16, b => b.Conv(IrConvOp.SExt, I8(200), IrType.I16)))).IsEqualTo((ushort)0xFFC8);

    [Test]
    public async Task Trunc_I16_To_I8_KeepsLowByte() =>
        await Assert.That(RunA(Fn(IrType.I8, b => b.Conv(IrConvOp.Trunc, I16(0x1234), IrType.I8)))).IsEqualTo((byte)0x34);

    // ---- memory -------------------------------------------------------------

    [Test]
    public async Task Alloca_Store_Load_RoundTrips()
    {
        // p = alloca i8 ; *p = 42 ; return *p + 1  => 43
        await Assert.That(RunA(Fn(IrType.I8, b =>
        {
            var p = b.Alloca(IrType.I8);
            b.Store(I8(42), p);
            var v = b.Load(p);
            return b.Add(v, I8(1));
        }))).IsEqualTo((byte)43);
    }

    [Test]
    public async Task Gep_IndexesArrayElements()
    {
        // a = alloca [4 x i8] ; a[0] = 10 ; a[2] = 20 ; return a[0] + a[2]  => 30
        await Assert.That(RunA(Fn(IrType.I8, b =>
        {
            var arr = b.Alloca(IrType.Array(IrType.I8, 4));
            var p0 = b.Gep(arr, I16(0), IrType.I8);
            var p2 = b.Gep(arr, I16(2), IrType.I8);
            b.Store(I8(10), p0);
            b.Store(I8(20), p2);
            return b.Add(b.Load(p0), b.Load(p2));
        }))).IsEqualTo((byte)30);
    }

    // ---- switch -------------------------------------------------------------

    /// <summary>@classify(x) = 11 if x==1, 22 if x==2, else 99.</summary>
    private static IrModule Classify()
    {
        var module = new IrModule("t");
        var x = new IrParameter("x", IrType.I8);
        var fn = new IrFunction("classify", IrType.I8, [x]);
        module.Functions.Add(fn);
        var entry = fn.AppendBlock("entry");
        var one = fn.AppendBlock("one");
        var two = fn.AppendBlock("two");
        var other = fn.AppendBlock("other");
        var b = new IrBuilder();

        b.PositionAtEnd(entry);
        b.Switch(x, other, [(I8(1), one), (I8(2), two)]);
        b.PositionAtEnd(one); b.Ret(I8(11));
        b.PositionAtEnd(two); b.Ret(I8(22));
        b.PositionAtEnd(other); b.Ret(I8(99));
        return module;
    }

    [Test]
    public async Task Switch_SelectsCaseOrDefault()
    {
        await Assert.That(RunA(Classify(), gb => gb.DebugWriteByte(Sm83Backend.WramBase, 1))).IsEqualTo((byte)11);
        await Assert.That(RunA(Classify(), gb => gb.DebugWriteByte(Sm83Backend.WramBase, 2))).IsEqualTo((byte)22);
        await Assert.That(RunA(Classify(), gb => gb.DebugWriteByte(Sm83Backend.WramBase, 7))).IsEqualTo((byte)99);
    }

    // ---- control flow: a real loop -----------------------------------------

    /// <summary>@sum(n) = 0+1+...+(n-1) — a loop with two i16 phis and an unsigned compare.</summary>
    private static IrModule SumLoop()
    {
        var module = new IrModule("t");
        var n = new IrParameter("n", IrType.I16);
        var fn = new IrFunction("sum", IrType.I16, [n]);
        module.Functions.Add(fn);

        var entry = fn.AppendBlock("entry");
        var loop = fn.AppendBlock("loop");
        var done = fn.AppendBlock("done");
        var b = new IrBuilder();

        b.PositionAtEnd(entry);
        b.Br(loop);

        b.PositionAtEnd(loop);
        var i = b.Phi(IrType.I16);
        var acc = b.Phi(IrType.I16);
        var accNext = b.Add(acc, i);
        var iNext = b.Add(i, I16(1));
        var cond = b.Compare(IrCompareOp.Ult, iNext, n);
        b.CondBr(cond, loop, done);

        i.AddIncoming(I16(0), entry);
        i.AddIncoming(iNext, loop);
        acc.AddIncoming(I16(0), entry);
        acc.AddIncoming(accNext, loop);

        b.PositionAtEnd(done);
        b.Ret(accNext);

        return module;
    }

    /// <summary>Two phis that swap each other across the back-edge — miscompiles without a temp.</summary>
    private static IrModule SwapLoop()
    {
        var module = new IrModule("t");
        var fn = new IrFunction("main", IrType.I16, []);
        module.Functions.Add(fn);
        var entry = fn.AppendBlock("entry");
        var loop = fn.AppendBlock("loop");
        var done = fn.AppendBlock("done");
        var b = new IrBuilder();

        b.PositionAtEnd(entry);
        b.Br(loop);

        b.PositionAtEnd(loop);
        var a = b.Phi(IrType.I16);
        var c = b.Phi(IrType.I16);
        var i = b.Phi(IrType.I16);
        var iNext = b.Add(i, I16(1));
        var cond = b.Compare(IrCompareOp.Ult, iNext, I16(3)); // two back-edge traversals
        b.CondBr(cond, loop, done);

        a.AddIncoming(I16(1), entry); a.AddIncoming(c, loop); // a, c = c, a  (swap)
        c.AddIncoming(I16(2), entry); c.AddIncoming(a, loop);
        i.AddIncoming(I16(0), entry); i.AddIncoming(iNext, loop);

        b.PositionAtEnd(done);
        b.Ret(a);
        return module;
    }

    [Test]
    public async Task PhiSwap_RealizedInParallel()
    {
        // a starts at 1, swaps with c twice -> back to 1. Sequential copies would wrongly give 2.
        await Assert.That(RunHL(SwapLoop())).IsEqualTo((ushort)1);
    }

    [Test]
    public async Task SumLoop_RunsToCompletion()
    {
        // sum(10) = 0+1+...+9 = 45. The parameter is written to its WRAM slot (WramBase).
        var result = RunHL(SumLoop(), gb =>
        {
            gb.DebugWriteByte(Sm83Backend.WramBase, 10);       // n low
            gb.DebugWriteByte(Sm83Backend.WramBase + 1, 0);    // n high
        });
        await Assert.That(result).IsEqualTo((ushort)45);
    }
}
