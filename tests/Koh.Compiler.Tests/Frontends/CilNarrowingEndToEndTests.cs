using System.Collections.Immutable;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.Cil;
using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using KohDiagnosticSeverity = Koh.Core.Diagnostics.DiagnosticSeverity;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Frontends;

/// <summary>
/// End-to-end proof for <see cref="NarrowPass"/> (spec §4 / task 3 — see
/// docs/superpowers/specs/2026-07-14-cil-frontend-design.md) through the real CIL pipeline: a
/// game's compiled ASSEMBLY, both Debug and Release IL, lowered, verified, optimized, linked to a
/// ROM, and run on <see cref="GameBoySystem"/>. Complements <c>NarrowPassTests</c> (hand-built IR
/// unit tests) with (a) a real computed value that would come out WRONG under a buggy
/// Slt-over-zext remap — the highest-value correctness check, since a wrong remap changes the
/// answer rather than merely failing to fire — and (b) a measured ROM-size/cycle-count delta
/// proving the pass does something on real byte-heavy code compiled through the CIL frontend.
/// Mirrors <c>CilEndToEndTests</c>'s harness shape (kept self-contained per that file's own
/// remarks on why each phase's E2E file owns its harness).
/// </summary>
public class CilNarrowingEndToEndTests
{
    // ---- Roslyn: compile real C# to a real assembly on disk -----------------------------------

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            foreach (
                var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            )
            {
                try
                {
                    builder.Add(MetadataReference.CreateFromFile(path));
                }
                catch (IOException) { }
                catch (BadImageFormatException) { }
            }
        }
        builder.Add(
            MetadataReference.CreateFromFile(typeof(Koh.GameBoy.Hardware).Assembly.Location)
        );
        return builder.ToImmutable();
    });

    private static readonly string ScratchDir = Path.Combine(
        Path.GetTempPath(),
        "koh-cil-narrowing-end-to-end-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilNarrowingE2EAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_narrow_e2e_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    // ---- Frontend -> IR, verified -------------------------------------------------------------

    private static IrModule Frontend(string source, OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var assemblyPath = CompileToAssembly(source, level);
        var input = CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        var module = new CilFrontend().Lower(input, diagnostics);
        if (diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "frontend reported errors:\n  "
                    + string.Join("\n  ", diagnostics.Select(d => d.Message))
            );
        var errors = IrVerifier.Verify(module);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "IR verification failed:\n  " + string.Join("\n  ", errors)
            );
        return module;
    }

    /// <summary>Pass list identical to <see cref="IrOptimizer.Passes"/> minus <see cref="NarrowPass"/>
    /// — the "off" arm of the with/without measurement. Production code always goes through
    /// <see cref="IrOptimizer.Optimize(IrModule)"/>'s fixed pipeline; this exists purely so the test
    /// can report the delta NarrowPass makes on real byte-heavy code.</summary>
    private static readonly IIrFunctionPass[] PassesWithoutNarrowing = IrOptimizer
        .Passes.Where(p => p is not NarrowPass)
        .ToArray();

    private static EmitModel Compile(string source, OptimizationLevel level, bool narrow)
    {
        var module = Frontend(source, level);
        IrOptimizer.Optimize(module, narrow ? IrOptimizer.Passes : PassesWithoutNarrowing);
        var errors = IrVerifier.Verify(module);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "IR verification failed after optimization:\n  " + string.Join("\n  ", errors)
            );
        return new Sm83Backend().Compile(module, new DiagnosticBag());
    }

    // ---- Emulator harness (mirrors CilEndToEndTests) -------------------------------------------

    private static GameBoySystem Load(EmitModel model, out int start, out int length)
    {
        var link = new LinkerType().Link([new LinkerInput("cil", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        start = 0x100;
        length = Sm83Backend.CodeBase + model.Sections[0].Data.Length - 0x100;
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start;
        return gb;
    }

    private static ulong Run(GameBoySystem gb, int start)
    {
        var startCycles = gb.Cpu.TotalTCycles;
        for (int steps = 0; steps < 500_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= 0x8000)
                break;
            gb.StepInstruction();
        }
        return gb.Cpu.TotalTCycles - startCycles;
    }

    // ---- Fixture 1: the Slt-over-zext remap, at end-to-end value granularity ------------------
    //
    // `a < b` for bytes 200 and 50 lowers (via CIL's always-int32 evaluation stack) to a *signed*
    // Slt over *zero-extended* operands. The wide comparison is unambiguously correct (200 is not
    // less than 50); a buggy narrowing that kept the Slt predicate at i8 would read 200 as -56 and
    // get the wrong answer. Unlike an arithmetic demotion (value-equivalent by construction, so a
    // value assertion alone can't tell whether the pass fired), a wrong compare remap changes the
    // OBSERVABLE VALUE — making this the discriminating end-to-end test for task 3.

    // a/b are built by counting loops, not literal assignments: a literal `byte a = 200;` becomes a
    // direct SSA constant after Mem2Reg, and ConstantFoldingPass folds the whole comparison away
    // before NarrowPass ever sees it (verified — an earlier version of this fixture did exactly
    // that and the compare vanished under optimization). A loop's exit value is a genuinely dynamic
    // phi that ConstantFoldingPass does not unroll/evaluate, so the comparison survives to be
    // narrowed while still landing on the same discriminating values (200 vs 50).
    private const string ByteCompareSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                byte a = 0;
                for (int i = 0; i < 200; i++) a++;
                byte b = 0;
                for (int i = 0; i < 50; i++) b++;
                byte lt = 0;
                if (a < b)
                    lt = 1;
                Hardware.BGP = lt;
            }
        }
        """;

    private static byte RunByteCompare(OptimizationLevel level)
    {
        var gb = Load(Compile(ByteCompareSource, level, narrow: true), out int s, out _);
        Run(gb, s);
        return gb.DebugReadByte(0xFF47);
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task ByteCompare_UnsignedOrderingIsCorrectThroughNarrowing(OptimizationLevel level)
    {
        // 200 is NOT less than 50 as unsigned bytes. A buggy remap (naively keeping Slt at i8) would
        // read 200 as the signed value -56 and set BGP to 1.
        await Assert.That(RunByteCompare(level)).IsEqualTo((byte)0);
    }

    [Test]
    public async Task ByteCompare_DebugAndReleaseAgree()
    {
        await Assert
            .That(RunByteCompare(OptimizationLevel.Release))
            .IsEqualTo(RunByteCompare(OptimizationLevel.Debug));
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task ByteCompare_NarrowPassActuallyFiredOnTheCompare(OptimizationLevel level)
    {
        // The value test above can't distinguish "narrowed correctly" from "never narrowed, ran the
        // whole comparison at i32" — at i32 width the comparison is correct either way. Assert
        // directly on the optimized IR that the compare's operands are i8, proving the pass fired
        // through the real CIL frontend (not just in the hand-built-IR unit tests).
        var module = Frontend(ByteCompareSource, level);
        IrOptimizer.Optimize(module);
        var compares = module
            .Functions.SelectMany(f => f.Blocks)
            .SelectMany(b => b.Instructions)
            .OfType<CompareInstruction>()
            .ToList();
        await Assert.That(compares).IsNotEmpty();
        // Two of the loop counters (`int i`) stay i32 — narrowing them would be illegal (they're
        // genuine ints, not extensions of a narrower value) and this pass correctly leaves them
        // alone. The `a < b` comparison must have demoted to an i8 compare with an UNSIGNED
        // predicate — Debug IL emits it as `blt.un` (-> Slt-over-zext -> remaps to Ult) while
        // Release IL emits the branch inverted as `bge.un` (-> Sge-over-zext -> remaps to Uge); both
        // are the same remap rule (see MapComparePredicate), just different source polarity. Either
        // way, a signed predicate surviving at i8 here would mean the remap didn't fire/was wrong.
        await Assert
            .That(
                compares.Any(c =>
                    c.Left.Type.Bits == 8
                    && c.Op
                        is IrCompareOp.Ult
                            or IrCompareOp.Ule
                            or IrCompareOp.Ugt
                            or IrCompareOp.Uge
                )
            )
            .IsTrue();
        await Assert
            .That(
                compares.Any(c =>
                    c.Left.Type.Bits == 8
                    && c.Op
                        is IrCompareOp.Slt
                            or IrCompareOp.Sle
                            or IrCompareOp.Sgt
                            or IrCompareOp.Sge
                )
            )
            .IsFalse(); // a signed predicate over an i8 zext source would be the exact miscompile
    }

    // ---- Fixture 2: byte-heavy arithmetic, measured with and without the pass -----------------
    //
    // A tight byte-counted loop accumulating a byte sum: every iteration's counter compare,
    // counter increment, and accumulation are all byte arithmetic promoted to int32 by CIL, so
    // narrowing (or not) is amplified by the iteration count into an easily reported delta.

    private const string ByteAccumulateLoopSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                byte sum = 0;
                byte step = 3;
                for (byte i = 0; i < 50; i++)
                {
                    sum = (byte)(sum + step);
                }
                Hardware.BGP = sum;
            }
        }
        """;

    private static (byte Result, int RomBytes, ulong Cycles) RunByteAccumulateLoop(
        OptimizationLevel level,
        bool narrow
    )
    {
        var model = Compile(ByteAccumulateLoopSource, level, narrow);
        var romBytes = model.Sections[0].Data.Length;
        var gb = Load(model, out int s, out _);
        var cycles = Run(gb, s);
        return (gb.DebugReadByte(0xFF47), romBytes, cycles);
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task ByteAccumulateLoop_ProducesTheCorrectSum(OptimizationLevel level)
    {
        // 3 * 50 mod 256 = 150.
        var (result, _, _) = RunByteAccumulateLoop(level, narrow: true);
        await Assert.That(result).IsEqualTo((byte)150);
    }

    [Test]
    public async Task ByteAccumulateLoop_DebugAndReleaseAgree()
    {
        var debug = RunByteAccumulateLoop(OptimizationLevel.Debug, narrow: true);
        var release = RunByteAccumulateLoop(OptimizationLevel.Release, narrow: true);
        await Assert.That(release.Result).IsEqualTo(debug.Result);
    }

    [Test]
    public async Task ByteAccumulateLoop_NarrowingReducesRomSizeAndCycles_MeasuredDelta()
    {
        var without = RunByteAccumulateLoop(OptimizationLevel.Release, narrow: false);
        var with = RunByteAccumulateLoop(OptimizationLevel.Release, narrow: true);

        // Same program, same result, regardless of whether the pass ran — the delta below is purely
        // mechanical (code shape/cost), not a behavior change.
        await Assert.That(with.Result).IsEqualTo(without.Result);
        await Assert.That(with.Result).IsEqualTo((byte)150);

        // The actual measurement this task asks for. Both are strict reductions: every iteration's
        // compare/increment/accumulate collapse from 4-byte-wide inline ALU sequences to 1-byte-wide
        // ones (see Sm83Backend.ArithmeticEmitter — Add/And/Or/Xor/compare are inlined per-byte
        // loops on this backend, not runtime calls, so the win shows up as fewer bytes/cycles per op
        // rather than a call disappearing).
        await Assert.That(with.RomBytes).IsLessThan(without.RomBytes);
        await Assert.That(with.Cycles).IsLessThan(without.Cycles);

        Console.WriteLine(
            $"[NarrowPass measurement] ROM bytes: without={without.RomBytes} with={with.RomBytes} "
                + $"(saved {without.RomBytes - with.RomBytes}); "
                + $"cycles: without={without.Cycles} with={with.Cycles} "
                + $"(saved {without.Cycles - with.Cycles}, "
                + $"{100.0 * (without.Cycles - with.Cycles) / without.Cycles:F1}%)"
        );
    }
}
