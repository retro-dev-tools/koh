using Koh.Compiler.Backends.Sm83;
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

    /// <summary>Link one EmitModel to a ROM and run @main, returning the final accumulator.</summary>
    private static byte RunMain(EmitModel model)
    {
        var link = new LinkerType().Link([new LinkerInput("mvp", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("link produced no ROM");

        int start = Sm83Backend.CodeBase;
        int length = model.Sections[0].Data.Length;

        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start;

        // Step while the PC stays within the emitted function; the trailing RET exits the range.
        for (int steps = 0; steps < 1000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= start + length)
                break;
            gb.StepInstruction();
        }

        return gb.Registers.A;
    }

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

        // Reloads are elided (A still holds it), and %1 reuses %0's slot (their ranges don't overlap).
        byte[] expected =
        [
            0x3E,
            0x03, // LD A, 3
            0xC6,
            0x04, // ADD A, 4
            0xEA,
            0x00,
            0xC0, // LD (C000), A     ; %0
            0xC6,
            0x64, // ADD A, 100
            0xEA,
            0x00,
            0xC0, // LD (C000), A     ; %1 reuses %0's slot
            0xC9, // RET
        ];
        await Assert.That(data).IsEquivalentTo(expected);
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
