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
/// THE phase-1 proof (see <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>, task 3):
/// a game's compiled ASSEMBLY (not its source) goes frontend -&gt; IR -&gt; verifier -&gt; optimizer -&gt;
/// SM83 backend -&gt; linker -&gt; a real ROM run on <see cref="GameBoySystem"/>, for both Debug and
/// Release IL of the same C# source, and both are asserted to produce byte-identical observable
/// state. <see cref="CilLoweringTests"/> covers opcode/shape breadth; this file is the end-to-end
/// contract the phase exists to satisfy, so it deliberately keeps its own compile-to-assembly harness
/// (mirroring <c>CSharpEndToEndTests</c>'s shape) rather than depending on another test class's
/// internals.
/// </summary>
public class CilEndToEndTests
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
        "koh-cil-end-to-end-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilEndToEndAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true // the region-intrinsic fixture below compares a Gb.* byte* pointer
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_e2e_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    // ---- Frontend -> IR, verified -------------------------------------------------------------

    private static IrModule Frontend(
        string source,
        OptimizationLevel level,
        DiagnosticBag diagnostics
    )
    {
        var assemblyPath = CompileToAssembly(source, level);
        var input = CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        var module = new CilFrontend().Lower(input, diagnostics);
        if (!diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
        {
            var errors = IrVerifier.Verify(module);
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "IR verification failed:\n  " + string.Join("\n  ", errors)
                );
        }
        return module;
    }

    private static EmitModel Compile(string source, OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(source, level, diagnostics);
        if (diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "frontend reported errors:\n  "
                    + string.Join("\n  ", diagnostics.Select(d => d.Message))
            );
        IrOptimizer.Optimize(module); // CompilerDriver's default path (Mem2RegPass does SSA construction).
        return new Sm83Backend().Compile(module, new DiagnosticBag());
    }

    // ---- Emulator harness (mirrors CSharpEndToEndTests) ---------------------------------------

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

    private static void Run(GameBoySystem gb, int start, int length)
    {
        for (int steps = 0; steps < 200_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= 0x8000)
                break;
            gb.StepInstruction();
        }
    }

    // ---- The fixture: real C# that pokes Hardware registers in a loop -------------------------
    //
    // Writes LCDC to a known "LCD on" value directly, runs a loop that accumulates a counter into a
    // second local, and writes the accumulated result to BGP. Exercises: a plain register-setter
    // call (LCDC), a for-loop with a comparison branch and an add (the counter), and a second
    // register-setter call fed by the loop's result (BGP) — i.e. exactly "pokes Hardware registers in
    // a loop", per the task brief.
    private const string RegisterPokeLoopSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                Hardware.LCDC = 0x91;
                byte total = 0;
                for (int i = 0; i < 10; i++)
                {
                    total = (byte)(total + i);
                }
                Hardware.BGP = total;
            }
        }
        """;

    private readonly record struct ObservedState(byte Lcdc, byte Bgp);

    private static ObservedState RunAndObserve(OptimizationLevel level)
    {
        var gb = Load(Compile(RegisterPokeLoopSource, level), out int s, out int l);
        Run(gb, s, l);
        return new ObservedState(gb.DebugReadByte(0xFF40), gb.DebugReadByte(0xFF47));
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task RegisterPokeLoop_VerifiesCleanAndProducesExpectedState(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(RegisterPokeLoopSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var state = RunAndObserve(level);
        // LCDC = 0x91 (written directly); BGP = 0+1+...+9 = 45 (accumulated by the loop).
        await Assert.That(state.Lcdc).IsEqualTo((byte)0x91);
        await Assert.That(state.Bgp).IsEqualTo((byte)45);
    }

    [Test]
    public async Task RegisterPokeLoop_DebugAndReleaseProduceIdenticalObservableState()
    {
        // The whole point of phase 1: the SAME source, compiled to genuinely different IL by Roslyn's
        // two optimization levels, must lower to a ROM that behaves identically on the emulator. This
        // is not implied by the two parameterized runs above each independently matching the hand-
        // computed expected value — it is asserted directly, one run's observed state against the
        // other's.
        var debugState = RunAndObserve(OptimizationLevel.Debug);
        var releaseState = RunAndObserve(OptimizationLevel.Release);
        await Assert.That(releaseState).IsEqualTo(debugState);
    }

    // ---- Coverage for the other two [KohIntrinsic] kinds the fixture above doesn't touch ------
    //
    // RegisterPokeLoopSource only exercises "register" getters/setters. Item 1 of this task also
    // covers "region" (a fixed-address pointer, learned from the attribute the same way) and the
    // address-less control intrinsics ("ei"/"di"/"halt"/"nop"/"stop"). This fixture compares two
    // DISTINCT region pointers (Gb.Vram vs. Gb.Wram) rather than casting a numeric literal to a
    // pointer — a `(byte*)0x8000` literal cast lowers to CIL's native-int `conv.i`, an opcode outside
    // phase 1's stated subset (see CilMethodLowerer's opcode coverage notes); comparing two intrinsic
    // pointer values directly needs only `ceq`, which phase 1 already supports. It also calls
    // Hardware.DisableInterrupts(), asserted below by the emitted DI opcode.
    private const string RegionAndControlIntrinsicSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static unsafe void Main()
            {
                Hardware.DisableInterrupts();
                byte* vram = Gb.Vram;
                byte* wram = Gb.Wram;
                if (vram == wram)
                    Hardware.BGP = 0;
                else
                    Hardware.BGP = 1;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task RegionPointerAndControlIntrinsic_LoweredFromAttributeAlone(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(RegionAndControlIntrinsicSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        // The frontend must have learned BOTH region addresses straight from their [KohIntrinsic]
        // attributes — nothing else in this program states 0x8000 or 0xC000.
        await Assert.That(module.Globals.Any(g => g.FixedAddress == 0x8000)).IsTrue();
        await Assert.That(module.Globals.Any(g => g.FixedAddress == 0xC000)).IsTrue();

        var model = new Sm83Backend().Compile(module, new DiagnosticBag());
        // DI (0xF3): Hardware.DisableInterrupts() must have lowered to the real SM83 opcode.
        await Assert.That(model.Sections[0].Data.Contains((byte)0xF3)).IsTrue();

        var gb = Load(model, out int s, out int l);
        Run(gb, s, l);
        // BGP = 1 only if the two region pointers the frontend produced are genuinely distinct
        // addresses (0x8000 vs. 0xC000), not both defaulting to the same placeholder.
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)1);
    }
}
