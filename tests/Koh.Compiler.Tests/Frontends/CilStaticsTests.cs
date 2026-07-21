using System.Collections.Immutable;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.Cil;
using Koh.Compiler.Ir;
using Koh.Compiler.Ir.Optimization;
using Koh.Compiler.Targets;
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
/// Task 1 of the CIL-frontend gap-closing phase (see
/// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>): static fields and ROM data —
/// without this, no real game compiles (gb-2048-cs keeps its board in static WRAM fields). Follows
/// <see cref="CilEndToEndTests"/>'s own harness shape (its own compile-to-assembly pipeline, per that
/// file's remarks) rather than depending on it.
/// </summary>
public class CilStaticsTests
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
        "koh-cil-statics-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilStaticsAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_statics_{Guid.NewGuid():N}.dll");
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

    private static EmitModel Compile(IrModule module)
    {
        IrOptimizer.Optimize(module); // CompilerDriver's default path (Mem2RegPass does SSA construction).
        return new Sm83Backend().Compile(module, new DiagnosticBag());
    }

    private static (IrModule Module, EmitModel Model) FrontendAndCompile(
        string source,
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(source, level, diagnostics);
        if (diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "frontend reported errors:\n  "
                    + string.Join("\n  ", diagnostics.Select(d => d.Message))
            );
        // Optimize a fresh copy's worth of information is unnecessary here: the caller only needs the
        // pre-optimization module (for Globals assertions) and the compiled model, so compile the SAME
        // module (IrOptimizer.Optimize runs in place) and return both views.
        var model = Compile(module);
        return (module, model);
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

    // ---- Fixture 1: a mutable static counter, incremented across several calls ----------------

    private const string CounterSource = """
        using Koh.GameBoy;

        public static class Program
        {
            public static byte Counter;

            public static void Bump()
            {
                Counter = (byte)(Counter + 1);
            }

            public static void Main()
            {
                Bump();
                Bump();
                Bump();
                Bump();
                Bump();
                Hardware.LCDC = Counter;
            }
        }
        """;

    private static byte RunCounter(OptimizationLevel level)
    {
        var (_, model) = FrontendAndCompile(CounterSource, level);
        var gb = Load(model, out int s, out int l);
        Run(gb, s, l);
        return gb.DebugReadByte(0xFF40);
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task MutableStaticCounter_IncrementedAcrossCallsAndReadBack(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(CounterSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        // A mutable static's own global is a real, writable WRAM cell, zero-initialized by the
        // backend's boot clear (never a ROM constant) — the whole point of this fixture.
        var counterGlobal = module.Globals.Single(g => g.Name.EndsWith(".Counter"));
        await Assert.That(counterGlobal.AddressSpace).IsEqualTo(AddressSpace.Wram);

        await Assert.That(RunCounter(level)).IsEqualTo((byte)5);
    }

    [Test]
    public async Task MutableStaticCounter_DebugAndReleaseProduceIdenticalObservableState()
    {
        await Assert
            .That(RunCounter(OptimizationLevel.Release))
            .IsEqualTo(RunCounter(OptimizationLevel.Debug));
    }

    // ---- Fixture 2: a static readonly byte[] table, indexed at runtime ------------------------

    private const string TableSource = """
        using Koh.GameBoy;

        public static class Program
        {
            public static readonly byte[] Table = { 10, 20, 30, 40, 50 };

            public static byte ReadTable(int i)
            {
                return Table[i];
            }

            public static void Main()
            {
                Hardware.LCDC = ReadTable(2);
                Hardware.BGP = ReadTable(4);
            }
        }
        """;

    // E4 (length-carrying arrays): the ROM global is [u16 element count][payload].
    private static readonly byte[] ExpectedTableBytes = [5, 0, 10, 20, 30, 40, 50];

    private static (byte Lcdc, byte Bgp) RunTable(OptimizationLevel level)
    {
        var (_, model) = FrontendAndCompile(TableSource, level);
        var gb = Load(model, out int s, out int l);
        Run(gb, s, l);
        return (gb.DebugReadByte(0xFF40), gb.DebugReadByte(0xFF47));
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task StaticReadonlyByteArray_LandsInRomWithInitialBytes(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(TableSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        // Direct proof it "landed in ROM" (not rebuilt from a runtime heap allocation + a
        // per-element store loop at every boot): the module carries a ROM global whose OWN
        // initializer already holds the literal's bytes, straight from Cecil's RVA blob.
        var tableGlobal = module.Globals.Single(g => g.Name.EndsWith(".Table"));
        await Assert.That(tableGlobal.AddressSpace).IsEqualTo(AddressSpace.Rom);
        await Assert.That(tableGlobal.Initializer).IsNotNull();
        await Assert.That(tableGlobal.Initializer!).IsEquivalentTo(ExpectedTableBytes);

        var (lcdc, bgp) = RunTable(level);
        await Assert.That(lcdc).IsEqualTo((byte)30); // Table[2]
        await Assert.That(bgp).IsEqualTo((byte)50); // Table[4]
    }

    [Test]
    public async Task StaticReadonlyByteArray_DebugAndReleaseProduceIdenticalObservableState()
    {
        await Assert
            .That(RunTable(OptimizationLevel.Release))
            .IsEqualTo(RunTable(OptimizationLevel.Debug));
    }

    // ---- Fixture 3: a const, folded by Roslyn itself (no static field emitted at all) ---------

    private const string ConstSource = """
        using Koh.GameBoy;

        public static class Program
        {
            public const byte ConstVal = 42;

            public static byte ReadConst()
            {
                return ConstVal;
            }

            public static void Main()
            {
                Hardware.LCDC = ReadConst();
            }
        }
        """;

    private static byte RunConst(OptimizationLevel level)
    {
        var (_, model) = FrontendAndCompile(ConstSource, level);
        var gb = Load(model, out int s, out int l);
        Run(gb, s, l);
        return gb.DebugReadByte(0xFF40);
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task ConstField_FoldedByRoslynWithNoBackingStorage(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(ConstSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        // A 'const' field has no IL storage at all — Roslyn inlines it as an immediate 'ldc' at every
        // use site, so the CIL frontend never even sees a field reference for it; no global should
        // exist.
        await Assert.That(module.Globals.Any(g => g.Name.EndsWith(".ConstVal"))).IsFalse();

        await Assert.That(RunConst(level)).IsEqualTo((byte)42);
    }

    [Test]
    public async Task ConstField_DebugAndReleaseProduceIdenticalObservableState()
    {
        await Assert
            .That(RunConst(OptimizationLevel.Release))
            .IsEqualTo(RunConst(OptimizationLevel.Debug));
    }

    // ---- Fixture 4 (bonus coverage): a fixed-size mutable static array field (the gb-2048-cs
    // "board" shape — 'static byte[] cells = new byte[16];') gets a dedicated WRAM Array global,
    // not a heap allocation, matching CSharpFrontend's own static-array placement. ------------

    private const string MutableArraySource = """
        using Koh.GameBoy;

        public static class Program
        {
            public static byte[] Cells = new byte[4];

            public static void SetCell(int i, byte v)
            {
                Cells[i] = v;
            }

            public static void Main()
            {
                SetCell(0, 7);
                SetCell(3, 9);
                Hardware.LCDC = Cells[0];
                Hardware.BGP = Cells[3];
            }
        }
        """;

    private static (byte Lcdc, byte Bgp) RunMutableArray(OptimizationLevel level)
    {
        var (_, model) = FrontendAndCompile(MutableArraySource, level);
        var gb = Load(model, out int s, out int l);
        Run(gb, s, l);
        return (gb.DebugReadByte(0xFF40), gb.DebugReadByte(0xFF47));
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task MutableStaticArray_GetsDedicatedWramGlobalNotHeap(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(MutableArraySource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var cellsGlobal = module.Globals.Single(g => g.Name.EndsWith(".Cells"));
        await Assert.That(cellsGlobal.AddressSpace).IsEqualTo(AddressSpace.Wram);

        var (lcdc, bgp) = RunMutableArray(level);
        await Assert.That(lcdc).IsEqualTo((byte)7);
        await Assert.That(bgp).IsEqualTo((byte)9);
    }

    [Test]
    public async Task MutableStaticArray_DebugAndReleaseProduceIdenticalObservableState()
    {
        await Assert
            .That(RunMutableArray(OptimizationLevel.Release))
            .IsEqualTo(RunMutableArray(OptimizationLevel.Debug));
    }

    // ---- Regression coverage: a compiler-generated type's own '.cctor' (the no-capture-lambda
    // cache singleton `<>c::.cctor`, which builds a write-only `<>9` cache field this frontend's
    // delegate-cache-idiom interception never actually reads) must NOT be pulled into Pass 1.5's
    // eager static-constructor sweep — see CilModuleLowerer.Lower's remarks. -------------------

    private const string NoCaptureLambdaSource = """
        using System;
        using Koh.GameBoy;

        public static class Program
        {
            private static int NoCapture()
            {
                Func<int, int> f = x => x + 1;
                return f(5);
            }

            public static void Main()
            {
                Hardware.BGP = (byte)NoCapture();
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task NoCaptureLambda_DoesNotResurrectCompilerGeneratedCctor(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(NoCaptureLambdaSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        // The lambda BODY itself (`<>c.<NoCapture>b__0_0`) is genuinely called and must still be
        // lowered; only the cache singleton's OWN construction (its '.cctor'/'.ctor', which exist
        // purely to populate the never-actually-read `<>9` cache field) must not be.
        await Assert
            .That(module.Functions.Any(f => f.Name is "<>c..cctor" or "<>c..ctor"))
            .IsFalse();
        await Assert.That(module.Globals.Any(g => g.Name.Contains("<>9"))).IsFalse();

        var (_, model) = FrontendAndCompile(NoCaptureLambdaSource, level);
        var gb = Load(model, out int s, out int l);
        Run(gb, s, l);
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)6); // f(5) = 5 + 1
    }
}
