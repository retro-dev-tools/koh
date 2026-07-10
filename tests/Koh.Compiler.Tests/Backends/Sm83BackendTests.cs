using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Backends.Sm83.Mir;
using Koh.Compiler.Ir;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Backends;

public class Sm83BackendTests
{
    /// <summary>Compile a single-block IR function through the MVP SM83 backend.</summary>
    private static EmitModel Compile(IrModule module) =>
        new Sm83Backend().Compile(module, new DiagnosticBag());

    /// <summary>Build <c>func @main() : i8 { entry: body(builder) }</c>.</summary>
    private static IrModule Function(Func<IrBuilder, IrBasicBlock, IrValue> body)
    {
        var module = new IrModule("test");
        var fn = new IrFunction("main", IrType.I8, []);
        module.Functions.Add(fn);
        var entry = fn.AppendBlock("entry");
        var b = new IrBuilder();
        b.PositionAtEnd(entry);
        var result = body(b, entry);
        b.Ret(result);
        return module;
    }

    /// <summary>Link one EmitModel to a ROM and run @main until the PC leaves the emitted function (the
    /// trailing RET), returning the final machine state.</summary>
    private static GameBoySystem RunToExit(EmitModel model)
    {
        var link = new LinkerType().Link([new LinkerInput("mvp", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("link produced no ROM");

        int start = Sm83Backend.CodeBase;
        int length = model.Sections[0].Data.Length;

        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start;

        for (int steps = 0; steps < 1000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= start + length)
                break;
            gb.StepInstruction();
        }

        return gb;
    }

    /// <summary>Run @main and return the final accumulator (i8 return).</summary>
    private static byte RunMain(EmitModel model) => RunToExit(model).Registers.A;

    /// <summary>Run @main and return the final HL (i16 return).</summary>
    private static ushort RunMainHL(EmitModel model) => RunToExit(model).Registers.HL;

    [Test]
    public async Task Emits_ExpectedBytes_ForConstantFolding()
    {
        // %0 = add i8 3, 4 ; %1 = add i8 %0, 100 ; ret i8 %1
        var module = Function(
            (b, _) =>
            {
                var v0 = b.Add(IrBuilder.ConstInt(IrType.I8, 3), IrBuilder.ConstInt(IrType.I8, 4));
                return b.Add(v0, IrBuilder.ConstInt(IrType.I8, 100));
            }
        );

        var data = Compile(module).Sections[0].Data;

        // %0 is a gentle byte add whose only use (%1) is another gentle byte add in the same block, so it
        // is held in register E (LD E,A) instead of a WRAM slot, and %1 sources it with LD A,E. %1 itself
        // is not resident — its only use is the ret — so it goes to a WRAM slot, and the ret's reload is
        // elided since A still holds it.
        byte[] expected =
        [
            0x3E,
            0x03, // LD A, 3
            0xC6,
            0x04, // ADD A, 4
            0x5F, // LD E, A          ; %0 resident in E
            0x7B, // LD A, E          ; reload %0 for %1
            0xC6,
            0x64, // ADD A, 100
            0xEA,
            0x00,
            0xC0, // LD (C000), A     ; %1 -> WRAM slot
            0xC9, // RET
        ];
        await Assert.That(data).IsEquivalentTo(expected);
    }

    // ---- Register residency (SOTA item #2) --------------------------------

    [Test]
    public async Task Residency_AssignsShortLivedByteValueToRegister()
    {
        // %0 = add i8 3,4 ; %1 = add i8 %0,100 ; ret %1
        // %0's only use is the gentle byte add %1, so it is held in register E instead of a WRAM slot.
        var fn = new IrFunction("main", IrType.I8, []);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        var v0 = b.Add(IrBuilder.ConstInt(IrType.I8, 3), IrBuilder.ConstInt(IrType.I8, 4));
        var v1 = b.Add(v0, IrBuilder.ConstInt(IrType.I8, 100));
        b.Ret(v1);

        var alloc = FunctionAllocation.For(fn, 0xC000, allowResidency: true);

        await Assert.That(alloc.Register.ContainsKey(v0)).IsTrue();
        await Assert.That(alloc.Register[v0]).IsEqualTo(Sm83Register.E);
        await Assert.That(alloc.Slot.ContainsKey(v0)).IsFalse(); // resident -> no WRAM slot
        // %1 feeds only the ret (not a gentle byte binary), so it is not resident.
        await Assert.That(alloc.Register.ContainsKey(v1)).IsFalse();
    }

    [Test]
    public async Task Residency_IsDisabledByDefault()
    {
        // Same IR, but with residency off (the default): everything goes to WRAM.
        var fn = new IrFunction("main", IrType.I8, []);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        var v0 = b.Add(IrBuilder.ConstInt(IrType.I8, 3), IrBuilder.ConstInt(IrType.I8, 4));
        b.Ret(b.Add(v0, IrBuilder.ConstInt(IrType.I8, 100)));

        var alloc = FunctionAllocation.For(fn, 0xC000);
        await Assert.That(alloc.Register).IsEmpty();
    }

    [Test]
    public async Task Residency_ComputesCorrectResultThroughRegister()
    {
        // A chain of gentle byte adds: %0 and %1 are register-resident (they share E in sequence, each
        // dying as the next is born), %2 goes to WRAM. Runs on the emulator; result = 10+20+5+100 = 135.
        var module = Function(
            (b, _) =>
            {
                var v0 = b.Add(
                    IrBuilder.ConstInt(IrType.I8, 10),
                    IrBuilder.ConstInt(IrType.I8, 20)
                );
                var v1 = b.Add(v0, IrBuilder.ConstInt(IrType.I8, 5));
                return b.Add(v1, IrBuilder.ConstInt(IrType.I8, 100));
            }
        );
        await Assert.That(RunMain(Compile(module))).IsEqualTo((byte)135);
    }

    [Test]
    public async Task Residency_SkipsValueUsedByNonGentleOp()
    {
        // %0 = add i8 3,4 ; %1 = shl i8 %0,1 ; ret %1 — %0 feeds a shift, whose emitter uses D/E as
        // working storage, so %0 must not be resident.
        var fn = new IrFunction("main", IrType.I8, []);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        var v0 = b.Add(IrBuilder.ConstInt(IrType.I8, 3), IrBuilder.ConstInt(IrType.I8, 4));
        var v1 = b.Binary(IrBinaryOp.Shl, v0, IrBuilder.ConstInt(IrType.I8, 1));
        b.Ret(v1);

        var alloc = FunctionAllocation.For(fn, 0xC000, allowResidency: true);
        await Assert.That(alloc.Register).IsEmpty();
    }

    [Test]
    public async Task Residency_AssignsSixteenBitValueToHL()
    {
        // %0 = add i16 300,40 ; %1 = add i16 %0,100 ; ret %1 — %0's only use is a gentle 16-bit add, so
        // it is held in the HL pair rather than a WRAM slot.
        var fn = new IrFunction("main", IrType.I16, []);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        var v0 = b.Add(IrBuilder.ConstInt(IrType.I16, 300), IrBuilder.ConstInt(IrType.I16, 40));
        b.Ret(b.Add(v0, IrBuilder.ConstInt(IrType.I16, 100)));

        var alloc = FunctionAllocation.For(fn, 0xC000, allowResidency: true);
        await Assert.That(alloc.Register[v0]).IsEqualTo(Sm83Register.Hl);
        await Assert.That(alloc.Slot.ContainsKey(v0)).IsFalse();
    }

    [Test]
    public async Task Residency_ComputesCorrectSixteenBitResultThroughHL()
    {
        // %0 (=340) lives in HL, %1 (=440) sources it, returns i16 in HL. Runs on the emulator.
        var module = new IrModule("test");
        var fn = new IrFunction("main", IrType.I16, []);
        module.Functions.Add(fn);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        var v0 = b.Add(IrBuilder.ConstInt(IrType.I16, 300), IrBuilder.ConstInt(IrType.I16, 40));
        b.Ret(b.Add(v0, IrBuilder.ConstInt(IrType.I16, 100)));

        await Assert.That(RunMainHL(Compile(module))).IsEqualTo((ushort)440);
    }

    [Test]
    public async Task Residency_GivesSimultaneouslyLiveValuesDistinctRegisters()
    {
        // %0 = 3+4 ; %1 = 5+6 ; %2 = %0+%1 ; ret %2 — %0 and %1 are both live at %2, so they interfere
        // and must occupy different byte registers (E and D). The result must still be 3+4+5+6 = 18.
        var module = Function(
            (b, _) =>
            {
                var v0 = b.Add(IrBuilder.ConstInt(IrType.I8, 3), IrBuilder.ConstInt(IrType.I8, 4));
                var v1 = b.Add(IrBuilder.ConstInt(IrType.I8, 5), IrBuilder.ConstInt(IrType.I8, 6));
                return b.Add(v0, v1);
            }
        );
        var fn = module.Functions[0];
        var alloc = FunctionAllocation.For(fn, 0xC000, allowResidency: true);

        await Assert.That(alloc.Register.Count).IsEqualTo(2);
        await Assert
            .That(alloc.Register[fn.Blocks[0].Instructions[0]])
            .IsNotEqualTo(alloc.Register[fn.Blocks[0].Instructions[1]]);
        await Assert.That(RunMain(Compile(module))).IsEqualTo((byte)18);
    }

    [Test]
    public async Task Residency_CoalescesChainIntoOneRegister()
    {
        // A chain of gentle byte adds where each value dies as the next is born: they do not interfere,
        // so they all reuse the same register (E). Result = 10+20+5+7 = 42.
        var module = Function(
            (b, _) =>
            {
                var v0 = b.Add(
                    IrBuilder.ConstInt(IrType.I8, 10),
                    IrBuilder.ConstInt(IrType.I8, 20)
                );
                var v1 = b.Add(v0, IrBuilder.ConstInt(IrType.I8, 5));
                var v2 = b.Add(v1, IrBuilder.ConstInt(IrType.I8, 7));
                return b.Add(v2, IrBuilder.ConstInt(IrType.I8, 0));
            }
        );
        var fn = module.Functions[0];
        var alloc = FunctionAllocation.For(fn, 0xC000, allowResidency: true);
        var instrs = fn.Blocks[0].Instructions;

        // v0, v1, v2 are all resident and all in E (coalesced); v3 (feeds ret) is not resident.
        await Assert.That(alloc.Register[instrs[0]]).IsEqualTo(Sm83Register.E);
        await Assert.That(alloc.Register[instrs[1]]).IsEqualTo(Sm83Register.E);
        await Assert.That(alloc.Register[instrs[2]]).IsEqualTo(Sm83Register.E);
        await Assert.That(RunMain(Compile(module))).IsEqualTo((byte)42);
    }

    [Test]
    public async Task Residency_CoalescesSixteenBitChainInHL()
    {
        // A 16-bit chain: each value dies as the next is born, so they coalesce in HL (the wide-result
        // interference rule that blocks WRAM coalescing does not apply to full-register residents).
        // Result = 300+40+100+200 = 640.
        var module = new IrModule("test");
        var fn = new IrFunction("main", IrType.I16, []);
        module.Functions.Add(fn);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        var v0 = b.Add(IrBuilder.ConstInt(IrType.I16, 300), IrBuilder.ConstInt(IrType.I16, 40));
        var v1 = b.Add(v0, IrBuilder.ConstInt(IrType.I16, 100));
        b.Ret(b.Add(v1, IrBuilder.ConstInt(IrType.I16, 200)));
        var instrs = fn.Blocks[0].Instructions;

        var alloc = FunctionAllocation.For(fn, 0xC000, allowResidency: true);
        // v0 and v1 both live in HL (coalesced in sequence); the last add feeds the ret, so it is not.
        await Assert.That(alloc.Register[instrs[0]]).IsEqualTo(Sm83Register.Hl);
        await Assert.That(alloc.Register[instrs[1]]).IsEqualTo(Sm83Register.Hl);
        await Assert.That(RunMainHL(Compile(module))).IsEqualTo((ushort)640);
    }

    [Test]
    public async Task Residency_UsesHalfOfHLUnderBytePressure()
    {
        // Four byte values are simultaneously live (v0..v3 all feed later adds), exhausting C/D/E, so one
        // spills into L — a byte in half of the HL pair (the bytewise H:L allocation). Runs on the emulator
        // to prove the L path is correct; result = (30+3)+(7+11) = 51.
        var module = Function(
            (b, _) =>
            {
                var v0 = b.Add(
                    IrBuilder.ConstInt(IrType.I8, 10),
                    IrBuilder.ConstInt(IrType.I8, 20)
                ); // 30
                var v1 = b.Add(IrBuilder.ConstInt(IrType.I8, 1), IrBuilder.ConstInt(IrType.I8, 2)); // 3
                var v2 = b.Add(IrBuilder.ConstInt(IrType.I8, 3), IrBuilder.ConstInt(IrType.I8, 4)); // 7
                var v3 = b.Add(IrBuilder.ConstInt(IrType.I8, 5), IrBuilder.ConstInt(IrType.I8, 6)); // 11
                var v4 = b.Add(v0, v1); // 33
                var v5 = b.Add(v2, v3); // 18
                return b.Add(v4, v5); // 51
            }
        );
        var fn = module.Functions[0];
        var alloc = FunctionAllocation.For(fn, 0xC000, allowResidency: true);

        await Assert.That(alloc.Register.Values).Contains(Sm83Register.L);
        await Assert.That(RunMain(Compile(module))).IsEqualTo((byte)51);
    }

    // ---- Register calling convention (SOTA item #4) -----------------------

    [Test]
    public async Task Residency_ReceivesLeafParametersInRegisters()
    {
        // byte add(byte a, byte b) => a + b — a and b are used only by a gentle add in the entry block,
        // so they are received in CPU registers (distinct ones — both are live at entry) with no WRAM slot.
        var a = new IrParameter("a", IrType.I8);
        var bb = new IrParameter("b", IrType.I8);
        var fn = new IrFunction("add", IrType.I8, [a, bb]);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        b.Ret(b.Add(a, bb));

        var alloc = FunctionAllocation.For(
            fn,
            0xC000,
            allowResidency: true,
            allowParamResidency: true
        );

        await Assert.That(alloc.Register.ContainsKey(a)).IsTrue();
        await Assert.That(alloc.Register.ContainsKey(bb)).IsTrue();
        await Assert.That(alloc.Register[a]).IsNotEqualTo(alloc.Register[bb]);
        await Assert.That(alloc.Slot.ContainsKey(a)).IsFalse();
        await Assert.That(alloc.Slot.ContainsKey(bb)).IsFalse();
    }

    [Test]
    public async Task Residency_ParameterPassingIsOffWithoutOptIn()
    {
        // The same function without the param-residency opt-in (as for the entry, which has no caller to
        // set its registers): parameters go to WRAM slots.
        var a = new IrParameter("a", IrType.I8);
        var bb = new IrParameter("b", IrType.I8);
        var fn = new IrFunction("add", IrType.I8, [a, bb]);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        b.Ret(b.Add(a, bb));

        var alloc = FunctionAllocation.For(
            fn,
            0xC000,
            allowResidency: true,
            allowParamResidency: false
        );

        await Assert.That(alloc.Register.ContainsKey(a)).IsFalse();
        await Assert.That(alloc.Slot.ContainsKey(a)).IsTrue();
        await Assert.That(alloc.Slot.ContainsKey(bb)).IsTrue();
    }

    [Test]
    public async Task Residency_RejectsParameterWhenEntryIsLoopHeader()
    {
        // entry: %0 = a + 1 ; %1 = shl %0, 1 (clobbers D/E) ; condbr %1 -> entry | exit.  The parameter a
        // is read only by the gentle add at index 0, so a naive gentle-prefix scan would make it resident —
        // but the entry block loops, and the shift after a's last use would clobber a's register before the
        // next iteration re-reads it. Because the entry block has a predecessor, a must stay in WRAM.
        var a = new IrParameter("a", IrType.I8);
        var fn = new IrFunction("loopy", IrType.I8, [a]);
        var b = new IrBuilder();
        var entry = fn.AppendBlock("entry");
        var exit = fn.AppendBlock("exit");
        b.PositionAtEnd(entry);
        var v0 = b.Add(a, IrBuilder.ConstInt(IrType.I8, 1));
        var v1 = b.Binary(IrBinaryOp.Shl, v0, IrBuilder.ConstInt(IrType.I8, 1));
        b.CondBr(v1, entry, exit);
        b.PositionAtEnd(exit);
        b.Ret(v0);

        var alloc = FunctionAllocation.For(
            fn,
            0xC000,
            allowResidency: true,
            allowParamResidency: true
        );

        await Assert.That(alloc.Register.ContainsKey(a)).IsFalse();
        await Assert.That(alloc.Slot.ContainsKey(a)).IsTrue();
    }

    [Test]
    public async Task Residency_LeafCallPassesArgsInRegisters()
    {
        // main() => add(40, 2), where add receives its params in registers. Runs on the emulator through
        // the register calling convention; result = 42.
        var m = new IrModule("test");
        var main = new IrFunction("main", IrType.I8, []);
        m.Functions.Add(main); // entry point must be first
        var a = new IrParameter("a", IrType.I8);
        var bb = new IrParameter("b", IrType.I8);
        var add = new IrFunction("add", IrType.I8, [a, bb]);
        m.Functions.Add(add);

        var b = new IrBuilder();
        b.PositionAtEnd(add.AppendBlock("entry"));
        b.Ret(b.Add(a, bb));
        b.PositionAtEnd(main.AppendBlock("entry"));
        b.Ret(b.Call(add, [IrBuilder.ConstInt(IrType.I8, 40), IrBuilder.ConstInt(IrType.I8, 2)]));

        await Assert.That(RunMain(Compile(m))).IsEqualTo((byte)42);
    }

    [Test]
    public async Task Allocation_ReusesSlotsForNonOverlappingValues()
    {
        var fn = new IrFunction("chain", IrType.I8, []);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        IrValue acc = IrBuilder.ConstInt(IrType.I8, 0);
        for (int i = 0; i < 10; i++)
            acc = b.Add(acc, IrBuilder.ConstInt(IrType.I8, 1)); // each temp dies once the next consumes it
        b.Ret(acc);

        var allocation = FunctionAllocation.For(fn, 0xC000);
        // Ten temporaries, never two live at once -> they share a single WRAM byte (was 10 bytes).
        await Assert.That(allocation.FrameEnd - 0xC000).IsLessThanOrEqualTo(2);
    }

    [Test]
    public async Task LineMap_MapsInstructionsToSourceLines()
    {
        var module = new IrModule("t");
        var fn = new IrFunction("main", IrType.I8, []);
        module.Functions.Add(fn);
        var b = new IrBuilder();
        b.PositionAtEnd(fn.AppendBlock("entry"));
        var add = b.Add(IrBuilder.ConstInt(IrType.I8, 1), IrBuilder.ConstInt(IrType.I8, 2));
        add.Source = new IrSourceLocation("game.cs", 10);
        var ret = b.Ret(add);
        ret.Source = new IrSourceLocation("game.cs", 11);

        var model = Compile(module);
        var lineMap = model.Sections[0].LineMap;
        await Assert.That(lineMap.Any(e => e.File == "game.cs" && e.Line == 10)).IsTrue();
        await Assert.That(lineMap.Any(e => e.File == "game.cs" && e.Line == 11)).IsTrue();

        // The ranges resolve through the linker into .kdbg address-map entries.
        var link = new LinkerType().Link([new LinkerInput("t", model)]);
        await Assert.That(link.LineMap.Any(e => e.File == "game.cs" && e.Line == 10)).IsTrue();
    }

    [Test]
    public async Task Runs_ConstantChain_InEmulator()
    {
        // (3 + 4) + 100 = 107
        var module = Function(
            (b, _) =>
            {
                var v0 = b.Add(IrBuilder.ConstInt(IrType.I8, 3), IrBuilder.ConstInt(IrType.I8, 4));
                return b.Add(v0, IrBuilder.ConstInt(IrType.I8, 100));
            }
        );

        await Assert.That(RunMain(Compile(module))).IsEqualTo((byte)107);
    }

    [Test]
    public async Task Runs_RegisterPathSubtraction_InEmulator()
    {
        // a = 10 + 20 (=30) ; b = 5 + 6 (=11) ; a - b = 19  (exercises the ALU-register path)
        var module = Function(
            (builder, _) =>
            {
                var a = builder.Add(
                    IrBuilder.ConstInt(IrType.I8, 10),
                    IrBuilder.ConstInt(IrType.I8, 20)
                );
                var b = builder.Add(
                    IrBuilder.ConstInt(IrType.I8, 5),
                    IrBuilder.ConstInt(IrType.I8, 6)
                );
                return builder.Sub(a, b);
            }
        );

        await Assert.That(RunMain(Compile(module))).IsEqualTo((byte)19);
    }

    [Test]
    public async Task Runs_BitwiseOps_InEmulator()
    {
        // (0xF0 | 0x0F) & 0x3C = 0x3C ; ^ 0x03 = 0x3F
        var module = Function(
            (builder, _) =>
            {
                var or = builder.Binary(
                    IrBinaryOp.Or,
                    IrBuilder.ConstInt(IrType.I8, 0xF0),
                    IrBuilder.ConstInt(IrType.I8, 0x0F)
                );
                var and = builder.Binary(IrBinaryOp.And, or, IrBuilder.ConstInt(IrType.I8, 0x3C));
                return builder.Binary(IrBinaryOp.Xor, and, IrBuilder.ConstInt(IrType.I8, 0x03));
            }
        );

        await Assert.That(RunMain(Compile(module))).IsEqualTo((byte)0x3F);
    }
}
