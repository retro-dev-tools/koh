using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Ir;
using Koh.Compiler.Targets;
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

    // ---- multiply / divide / remainder -------------------------------------

    [Test]
    public async Task Mul_I8() =>
        await Assert.That(RunA(Fn(IrType.I8, b => b.Mul(I8(6), I8(7))))).IsEqualTo((byte)42);

    [Test]
    public async Task Mul_I16() =>
        await Assert.That(RunHL(Fn(IrType.I16, b => b.Mul(I16(300), I16(4))))).IsEqualTo((ushort)1200);

    [Test]
    public async Task Mul_Signed_I8_LowByteMatches() =>
        // -3 * 4 = -12 = 0xF4 (unsigned low byte identical for two's complement)
        await Assert.That(RunA(Fn(IrType.I8, b => b.Mul(I8(-3), I8(4))))).IsEqualTo((byte)0xF4);

    [Test]
    public async Task UDiv_I16() =>
        await Assert.That(RunHL(Fn(IrType.I16, b => b.Binary(IrBinaryOp.UDiv, I16(1000), I16(7))))).IsEqualTo((ushort)142);

    [Test]
    public async Task URem_I16() =>
        await Assert.That(RunHL(Fn(IrType.I16, b => b.Binary(IrBinaryOp.URem, I16(1000), I16(7))))).IsEqualTo((ushort)6);

    [Test]
    public async Task UDiv_I8() =>
        await Assert.That(RunA(Fn(IrType.I8, b => b.Binary(IrBinaryOp.UDiv, I8(100), I8(9))))).IsEqualTo((byte)11);

    [Test]
    public async Task SDiv_I16_NegativeDividend() =>
        // -1000 / 7 = -142 = 0xFF72
        await Assert.That(RunHL(Fn(IrType.I16, b => b.Binary(IrBinaryOp.SDiv, I16(-1000), I16(7))))).IsEqualTo((ushort)0xFF72);

    [Test]
    public async Task SRem_I16_SignOfDividend() =>
        // -1000 % 7 = -6 = 0xFFFA
        await Assert.That(RunHL(Fn(IrType.I16, b => b.Binary(IrBinaryOp.SRem, I16(-1000), I16(7))))).IsEqualTo((ushort)0xFFFA);

    [Test]
    public async Task SDiv_I16_BothNegative() =>
        await Assert.That(RunHL(Fn(IrType.I16, b => b.Binary(IrBinaryOp.SDiv, I16(-1000), I16(-7))))).IsEqualTo((ushort)142);

    // ---- shifts -------------------------------------------------------------

    [Test]
    public async Task Shl_Const_I8() =>
        await Assert.That(RunA(Fn(IrType.I8, b => b.Binary(IrBinaryOp.Shl, I8(1), I8(4))))).IsEqualTo((byte)16);

    [Test]
    public async Task LShr_Const_I8() =>
        await Assert.That(RunA(Fn(IrType.I8, b => b.Binary(IrBinaryOp.LShr, I8(0x80), I8(3))))).IsEqualTo((byte)0x10);

    [Test]
    public async Task AShr_Const_I8_SignFills() =>
        // 0xF8 (-8) >> 1 arithmetic = 0xFC (-4)
        await Assert.That(RunA(Fn(IrType.I8, b => b.Binary(IrBinaryOp.AShr, I8(-8), I8(1))))).IsEqualTo((byte)0xFC);

    [Test]
    public async Task Shl_Const_I16() =>
        await Assert.That(RunHL(Fn(IrType.I16, b => b.Binary(IrBinaryOp.Shl, I16(1), I16(12))))).IsEqualTo((ushort)4096);

    [Test]
    public async Task LShr_Const_I16() =>
        await Assert.That(RunHL(Fn(IrType.I16, b => b.Binary(IrBinaryOp.LShr, I16(0x8000), I16(15))))).IsEqualTo((ushort)1);

    [Test]
    public async Task AShr_Const_I16_SignFills() =>
        await Assert.That(RunHL(Fn(IrType.I16, b => b.Binary(IrBinaryOp.AShr, I16(-16), I16(2))))).IsEqualTo((ushort)0xFFFC);

    [Test]
    public async Task Shl_Variable()
    {
        // vshift(v, sh) = v << sh, amount supplied at runtime; 3 << 4 = 48
        var m = new IrModule("t");
        var v = new IrParameter("v", IrType.I8);
        var sh = new IrParameter("sh", IrType.I8);
        var fn = new IrFunction("main", IrType.I8, [v, sh]);
        m.Functions.Add(fn);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        b.Ret(b.Binary(IrBinaryOp.Shl, v, sh));

        byte result = RunA(m, gb =>
        {
            gb.DebugWriteByte(Sm83Backend.WramBase, 3);      // v
            gb.DebugWriteByte(Sm83Backend.WramBase + 1, 4);  // sh
        });
        await Assert.That(result).IsEqualTo((byte)48);
    }

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

    // ---- calls --------------------------------------------------------------

    [Test]
    public async Task Call_PassesArgs_ReturnsI8()
    {
        var m = new IrModule("t");
        var main = new IrFunction("main", IrType.I8, []);
        m.Functions.Add(main); // entry point must be first
        var a = new IrParameter("a", IrType.I8);
        var bb = new IrParameter("b", IrType.I8);
        var cc = new IrParameter("c", IrType.I8);
        var add3 = new IrFunction("add3", IrType.I8, [a, bb, cc]);
        m.Functions.Add(add3);

        var b = new IrBuilder();
        b.PositionAtEnd(add3.AppendBlock("entry"));
        b.Ret(b.Add(b.Add(a, bb), cc));

        b.PositionAtEnd(main.AppendBlock("entry"));
        b.Ret(b.Call(add3, [I8(10), I8(20), I8(12)]));

        await Assert.That(RunA(m)).IsEqualTo((byte)42); // 10 + 20 + 12
    }

    [Test]
    public async Task Call_ReturnsI16_InHL()
    {
        var m = new IrModule("t");
        var main = new IrFunction("main", IrType.I16, []);
        m.Functions.Add(main);
        var a = new IrParameter("a", IrType.I16);
        var bb = new IrParameter("b", IrType.I16);
        var sum2 = new IrFunction("sum2", IrType.I16, [a, bb]);
        m.Functions.Add(sum2);

        var b = new IrBuilder();
        b.PositionAtEnd(sum2.AppendBlock("entry"));
        b.Ret(b.Add(a, bb));
        b.PositionAtEnd(main.AppendBlock("entry"));
        b.Ret(b.Call(sum2, [I16(300), I16(250)]));

        await Assert.That(RunHL(m)).IsEqualTo((ushort)550);
    }

    [Test]
    public async Task Call_Nested_ChainOfFrames()
    {
        // g(x) = x + 1 ; f(x) = g(g(x)) = x + 2 ; main = f(40) = 42
        var m = new IrModule("t");
        var main = new IrFunction("main", IrType.I8, []);
        m.Functions.Add(main);
        var fx = new IrParameter("x", IrType.I8);
        var f = new IrFunction("f", IrType.I8, [fx]);
        m.Functions.Add(f);
        var gx = new IrParameter("x", IrType.I8);
        var g = new IrFunction("g", IrType.I8, [gx]);
        m.Functions.Add(g);

        var b = new IrBuilder();
        b.PositionAtEnd(g.AppendBlock("entry"));
        b.Ret(b.Add(gx, I8(1)));
        b.PositionAtEnd(f.AppendBlock("entry"));
        b.Ret(b.Call(g, [b.Call(g, [fx])]));
        b.PositionAtEnd(main.AppendBlock("entry"));
        b.Ret(b.Call(f, [I8(40)]));

        await Assert.That(RunA(m)).IsEqualTo((byte)42);
    }

    [Test]
    public async Task Recursion_IsRejected()
    {
        var m = new IrModule("t");
        var self = new IrFunction("self", IrType.I8, []);
        m.Functions.Add(self);
        var b = new IrBuilder();
        b.PositionAtEnd(self.AppendBlock("entry"));
        b.Ret(b.Call(self, []));

        bool threw = false;
        try { Compile(m); }
        catch (NotSupportedException) { threw = true; }
        await Assert.That(threw).IsTrue();
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

    // ---- globals ------------------------------------------------------------

    [Test]
    public async Task Global_RomTable_IndexedRead()
    {
        var m = new IrModule("t");
        var table = new IrGlobal("table", IrType.Array(IrType.I8, 4), AddressSpace.Rom,
            initializer: new byte[] { 5, 10, 15, 20 });
        m.Globals.Add(table);
        var fn = new IrFunction("main", IrType.I8, []);
        m.Functions.Add(fn);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        b.Ret(b.Load(b.Gep(IrBuilder.GlobalRef(table), I16(2), IrType.I8)));

        await Assert.That(RunA(m)).IsEqualTo((byte)15); // table[2]
    }

    [Test]
    public async Task Global_RomI16Constant()
    {
        var m = new IrModule("t");
        var val = new IrGlobal("val", IrType.I16, AddressSpace.Rom, initializer: new byte[] { 0xE8, 0x03 }); // 1000 LE
        m.Globals.Add(val);
        var fn = new IrFunction("main", IrType.I16, []);
        m.Functions.Add(fn);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        b.Ret(b.Load(IrBuilder.GlobalRef(val)));

        await Assert.That(RunHL(m)).IsEqualTo((ushort)1000);
    }

    [Test]
    public async Task Global_WramState_MutatedAcrossCalls()
    {
        // counter = 0 ; inc() twice ; return counter  => 2 (shared mutable module state)
        var m = new IrModule("t");
        var counter = new IrGlobal("counter", IrType.I8, AddressSpace.Wram);
        m.Globals.Add(counter);
        var main = new IrFunction("main", IrType.I8, []);
        m.Functions.Add(main);
        var inc = new IrFunction("inc", IrType.Void, []);
        m.Functions.Add(inc);

        var b = new IrBuilder();
        b.PositionAtEnd(inc.AppendBlock("entry"));
        b.Store(b.Add(b.Load(IrBuilder.GlobalRef(counter)), I8(1)), IrBuilder.GlobalRef(counter));
        b.Ret();

        b.PositionAtEnd(main.AppendBlock("entry"));
        b.Store(I8(0), IrBuilder.GlobalRef(counter));
        b.Call(inc, []);
        b.Call(inc, []);
        b.Ret(b.Load(IrBuilder.GlobalRef(counter)));

        await Assert.That(RunA(m)).IsEqualTo((byte)2);
    }

    // ---- runtime pointers / dynamic gep ------------------------------------

    [Test]
    public async Task Gep_DynamicIndex_I8Array()
    {
        // a[i] = (i+1)*10 ; return a[idx] with idx supplied at runtime; idx=2 -> 30
        var m = new IrModule("t");
        var idx = new IrParameter("idx", IrType.I8);
        var fn = new IrFunction("main", IrType.I8, [idx]);
        m.Functions.Add(fn);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        var arr = b.Alloca(IrType.Array(IrType.I8, 4));
        for (int i = 0; i < 4; i++)
            b.Store(I8((i + 1) * 10), b.Gep(arr, I16(i), IrType.I8));
        b.Ret(b.Load(b.Gep(arr, idx, IrType.I8))); // dynamic index

        await Assert.That(RunA(m, gb => gb.DebugWriteByte(Sm83Backend.WramBase, 2))).IsEqualTo((byte)30);
    }

    [Test]
    public async Task Gep_DynamicIndex_I16Array_ScalesBySize()
    {
        // i16 elements -> index scaled by 2; a = {100,200,300}; idx=1 -> 200
        var m = new IrModule("t");
        var idx = new IrParameter("idx", IrType.I8);
        var fn = new IrFunction("main", IrType.I16, [idx]);
        m.Functions.Add(fn);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        var arr = b.Alloca(IrType.Array(IrType.I16, 3));
        b.Store(I16(100), b.Gep(arr, I16(0), IrType.I16));
        b.Store(I16(200), b.Gep(arr, I16(1), IrType.I16));
        b.Store(I16(300), b.Gep(arr, I16(2), IrType.I16));
        b.Ret(b.Load(b.Gep(arr, idx, IrType.I16)));

        await Assert.That(RunHL(m, gb => gb.DebugWriteByte(Sm83Backend.WramBase, 1))).IsEqualTo((ushort)200);
    }

    [Test]
    public async Task Pointer_Parameter_Dereferences()
    {
        // main allocs a byte, stores 77, passes its address to deref(p) = *p
        var m = new IrModule("t");
        var main = new IrFunction("main", IrType.I8, []);
        m.Functions.Add(main);
        var p = new IrParameter("p", IrType.Pointer(IrType.I8));
        var deref = new IrFunction("deref", IrType.I8, [p]);
        m.Functions.Add(deref);

        var b = new IrBuilder();
        b.PositionAtEnd(deref.AppendBlock("entry"));
        b.Ret(b.Load(p));

        b.PositionAtEnd(main.AppendBlock("entry"));
        var cell = b.Alloca(IrType.I8);
        b.Store(I8(77), cell);
        b.Ret(b.Call(deref, [cell]));

        await Assert.That(RunA(m)).IsEqualTo((byte)77);
    }

    [Test]
    public async Task Store_ThroughDynamicPointer_RoundTrips()
    {
        // a[idx] = 99 ; return a[idx]  (both through the same runtime pointer)
        var m = new IrModule("t");
        var idx = new IrParameter("idx", IrType.I8);
        var fn = new IrFunction("main", IrType.I8, [idx]);
        m.Functions.Add(fn);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        var arr = b.Alloca(IrType.Array(IrType.I8, 4));
        var pd = b.Gep(arr, idx, IrType.I8);
        b.Store(I8(99), pd);
        b.Ret(b.Load(pd));

        await Assert.That(RunA(m, gb => gb.DebugWriteByte(Sm83Backend.WramBase, 3))).IsEqualTo((byte)99);
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
