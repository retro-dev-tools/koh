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
/// Task 2 of the phase's real-game-parity work (see
/// <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>, §3/§8): the CIL frontend lowers
/// code from a REFERENCED assembly on demand, transitively, rather than only the game module's own
/// statics. Koh.GameBoy's Hal (<c>Lcd</c>, <c>Tilemap</c>, …) and <c>Mem.Copy</c>/<c>Mem.Fill</c> are
/// ordinary compiled code in a referenced assembly — no interception, no string, no hardcoded name —
/// so a call into them must lower the same way a game-module call does. This file deliberately keeps
/// its own compile-to-assembly harness (mirroring <c>CilEndToEndTests</c>'s shape) rather than
/// depending on another test class's internals.
///
/// Every fixture verifies its own work INSIDE the compiled program (comparing buffer contents,
/// tilemap cells) and crosses only a small pass/fail verdict out through a Hardware register —
/// deliberately never a raw literal WRAM pointer (e.g. <c>Gb.Wram</c>/<c>(byte*)0xC000</c>) as scratch
/// storage: <c>Sm83Backend.WramBase</c> (0xC000) is also where the backend's own static per-function
/// frame allocation starts (locals/params are NESFab-style statically assigned WRAM, not stack-based —
/// see <c>MemRuntimeTests</c>'s own remarks on this exact hazard), so a literal address there is
/// silently the storage for some function's own locals, not a safe scratch buffer. A
/// <c>stackalloc</c> buffer is a function-local (part of that same frame machinery, address chosen by
/// the backend, safe by construction) rather than a hand-picked literal.
/// </summary>
public class CilReferenceTests
{
    // ---- Roslyn: compile real C# to a real assembly on disk, referencing Koh.GameBoy -----------

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
        "koh-cil-reference-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilReferenceAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true // the fixtures below do raw byte* arithmetic (stackalloc/Gb.TileMap)
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_ref_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    // ---- Frontend -> IR, verified ------------------------------------------------------------

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

    // ---- Emulator harness (mirrors CilEndToEndTests/CSharpEndToEndTests) ----------------------

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

    private static void Run(GameBoySystem gb, int start, int length, int stepBudget = 200_000)
    {
        for (int steps = 0; steps < stepBudget; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= 0x8000)
                break;
            gb.StepInstruction();
        }
    }

    // ============================================================================================
    // Fixture 1: a real Koh.GameBoy Hal method (Lcd.SetPalette) plus Mem.Copy/Mem.Fill — the literal
    // ask ("a fixture calling a real Koh.GameBoy Hal method AND Mem.Copy/Mem.Fill"). Both Mem.Copy's
    // move and Mem.Fill's fill are verified INSIDE the program (byte-by-byte) against stackalloc
    // buffers, and only a pass/fail verdict (SCY) plus a completion marker (SCX, so a run that
    // silently fell off the rails before finishing is distinguishable from one that finished with a
    // wrong verdict) cross out through hardware registers. BGP carries Lcd.SetPalette's own directly
    // observable effect.
    // ============================================================================================
    private const string HalAndMemSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static unsafe void Main()
            {
                Lcd.SetPalette(0x1B);

                byte* src = stackalloc byte[8];
                for (int i = 0; i < 8; i++)
                    src[i] = (byte)(i + 1);

                byte* dst = stackalloc byte[8];
                Mem.Copy(dst, src, 8);

                byte* fillDst = stackalloc byte[8];
                Mem.Fill(fillDst, 0x7A, 8);

                byte ok = 1;
                for (int i = 0; i < 8; i++)
                {
                    if (dst[i] != (byte)(i + 1))
                        ok = 0;
                    if (fillDst[i] != 0x7A)
                        ok = 0;
                }

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE; // completion marker: Main ran to its natural end
            }
        }
        """;

    private readonly record struct HalAndMemState(byte Bgp, byte Verdict, byte Completed);

    private static HalAndMemState RunHalAndMem(OptimizationLevel level)
    {
        var gb = Load(Compile(HalAndMemSource, level), out int s, out int l);
        Run(gb, s, l);
        return new HalAndMemState(
            gb.DebugReadByte(0xFF47), // BGP
            gb.DebugReadByte(0xFF42), // SCY
            gb.DebugReadByte(0xFF43) // SCX
        );
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task HalAndMemMethods_LowerOnDemandAndProduceExpectedState(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(HalAndMemSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var state = RunHalAndMem(level);
        // Main ran to completion (not: fell off the rails / exhausted the step budget mid-flight).
        await Assert.That(state.Completed).IsEqualTo((byte)0xEE);
        // Lcd.SetPalette(0x1B) writes Hardware.BGP directly — the Hal-method call itself.
        await Assert.That(state.Bgp).IsEqualTo((byte)0x1B);
        // Mem.Copy moved src (1..8) into dst, and Mem.Fill wrote 0x7A across fillDst — both checked
        // byte-by-byte inside the program; a nonzero verdict means every comparison passed.
        await Assert.That(state.Verdict).IsEqualTo((byte)1);
    }

    [Test]
    public async Task HalAndMemMethods_DebugAndReleaseProduceIdenticalObservableState()
    {
        var debugState = RunHalAndMem(OptimizationLevel.Debug);
        var releaseState = RunHalAndMem(OptimizationLevel.Release);
        await Assert.That(releaseState).IsEqualTo(debugState);
    }

    // ============================================================================================
    // Fixture 2: ONE transitive framework->framework on-demand lowering, isolated — Tilemap.SetTile
    // called once (no loop, so no step-budget question), verified straight off VRAM (real hardware
    // address space, disjoint from the WRAM frame region, so no collision risk reading it directly).
    // ============================================================================================
    private const string SingleSetTileSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                Tilemap.SetTile(5, 3, 9);
                Hardware.SCX = 0xEE; // completion marker
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task SingleSetTile_LowersOnDemandAndWritesTheRightCell(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(SingleSetTileSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(SingleSetTileSource, level), out int s, out int l);
        Run(gb, s, l);
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)0xEE); // completed
        // row 3, col 5 -> offset 3*32+5 = 101 -> 0x9800 + 101 = 0x9865.
        await Assert.That(gb.DebugReadByte(0x9865)).IsEqualTo((byte)9);
        // An untouched cell must stay at its ROM-boot-clear default (0).
        await Assert.That(gb.DebugReadByte(0x9800)).IsEqualTo((byte)0);
    }

    // ============================================================================================
    // Fixture 3: the full transitive case — Tilemap.Clear (one referenced-assembly method) calling
    // Tilemap.SetTile (another) 1024 times in a nested loop — proving on-demand lowering handles a
    // loop-heavy call site, not just a single call. Given its own generous step budget (the 200k
    // default is sized for the other, much smaller fixtures) and its own completion marker so a
    // budget shortfall reads as "did not finish", never silently as "finished but wrong". The LCD is
    // switched off first: on real hardware (and this emulator) a VRAM write that lands while the PPU
    // is actively rendering (mode 3) is dropped — Pan Docs, and stated directly in this same Hal
    // (Cgb.CopyToVram's/Lcd.Off's own doc comments) — so bulk-writing the tilemap with the LCD on is
    // invalid Game Boy code, not a case this test needs to tolerate.
    // ============================================================================================
    private const string ClearSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                Hardware.LCDC = 0; // LCD off: bulk VRAM writes are only defined with the PPU stopped
                Tilemap.Clear(7);
                Hardware.SCX = 0xEE; // completion marker
            }
        }
        """;

    private readonly record struct ClearState(
        byte Completed,
        byte Corner,
        byte FarCorner,
        byte Middle
    );

    private static ClearState RunClear(OptimizationLevel level)
    {
        var gb = Load(Compile(ClearSource, level), out int s, out int l);
        Run(gb, s, l, stepBudget: 4_000_000);
        return new ClearState(
            gb.DebugReadByte(0xFF43), // completion marker
            gb.DebugReadByte(0x9800), // row 0, col 0
            gb.DebugReadByte((ushort)(0x9800 + 31 * 32 + 31)), // row 31, col 31 (the map's last cell)
            gb.DebugReadByte((ushort)(0x9800 + 15 * 32 + 10)) // row 15, col 10 (an interior cell)
        );
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task TilemapClear_TransitivelyLowersSetTileAndClearsEveryCell(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(ClearSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var state = RunClear(level);
        await Assert.That(state.Completed).IsEqualTo((byte)0xEE);
        await Assert.That(state.Corner).IsEqualTo((byte)7);
        await Assert.That(state.FarCorner).IsEqualTo((byte)7);
        await Assert.That(state.Middle).IsEqualTo((byte)7);
    }

    [Test]
    public async Task TilemapClear_DebugAndReleaseProduceIdenticalObservableState()
    {
        var debugState = RunClear(OptimizationLevel.Debug);
        var releaseState = RunClear(OptimizationLevel.Release);
        await Assert.That(releaseState).IsEqualTo(debugState);
    }

    // ---- Pruning: a Hal method a fixture never calls must not be lowered into the module -------
    //
    // On-demand lowering already implies this (a method is only ever added to the module the first
    // time some call site resolves to it — see CilLoweringContext.EnsureLowered's remarks), so this
    // test is really evidence for "the frontend lowers only what it's asked to reach", which is what
    // makes the later, explicit IrOptimizer.RemoveUnreachableFunctions pruning call in
    // CilModuleLowerer.Lower largely redundant for THIS fixture (it exists for the call-graph-level
    // case per the task's own wording, not proven directly here). Checked straight off Frontend()'s
    // output, before IrOptimizer.Optimize runs, so this is specifically the frontend's own behavior
    // under test, not the general-purpose optimizer's unconditional dead-function sweep. Combines
    // all three call shapes above (Hal, Mem, transitive Hal->Hal) into one fixture purely to name
    // every "must be present" target in a single pass — never run on the emulator.
    private const string AllFrameworkCallsSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static unsafe void Main()
            {
                Lcd.SetPalette(0x1B);

                byte* src = stackalloc byte[8];
                byte* dst = stackalloc byte[8];
                Mem.Copy(dst, src, 8);
                Mem.Fill(dst, 0x7A, 8);

                Tilemap.Clear(7);
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task UncalledHalMethods_AreNeverLoweredIntoTheModule(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(AllFrameworkCallsSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();

        var names = module.Functions.Select(f => f.Name).ToHashSet();

        // Called (directly or transitively) — must be present.
        string[] called =
        [
            "Lcd.SetPalette",
            "Mem.Copy",
            "Mem.Fill",
            "Tilemap.Clear",
            "Tilemap.SetTile", // transitive: Clear calls SetTile
        ];
        foreach (var name in called)
            await Assert.That(names.Contains(name)).IsTrue();

        // Never called — must be ABSENT, proving the frontend didn't sweep Koh.GameBoy eagerly.
        string[] uncalled =
        [
            "Lcd.On",
            "Lcd.Off",
            "Lcd.Scroll",
            "Lcd.SelectTileData",
            "Joypad.Read",
            "Joypad.Held",
            "Ppu.WaitVBlank",
            "Ppu.WaitForVramAccess",
            "Ppu.WaitForHBlank",
            "Cgb.IsColor",
            "Cgb.TryEnableDoubleSpeed",
            "Cgb.SelectVramBank",
            "Cgb.CopyToVram",
            "Cgb.SetBackgroundColor",
            "Mem.Alloc",
            "Mem.Reset",
        ];
        foreach (var name in uncalled)
            await Assert.That(names.Contains(name)).IsFalse();
    }

    // ---- The BCL stays a diagnostic, never lowered ----------------------------------------------
    //
    // Reachable only when the BCL assembly is actually RESOLVABLE — this test adds
    // typeof(object).Assembly.Location to the reference paths so Cecil can resolve System.Math
    // at all; without that, calleeRef.Resolve() itself throws first and the CilModuleLowerer.
    // IsBclMethod guard is never reached (a real gap this test closes: every OTHER fixture in this
    // file only references Koh.GameBoy, so a genuinely BCL-resolvable call site is otherwise
    // untested).
    private const string BclCallSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                int m = System.Math.Max(3, 5);
                Hardware.BGP = (byte)m;
            }
        }
        """;

    [Test]
    public async Task CallIntoBcl_IsADiagnosticNeverLoweredOrMiscompiled()
    {
        var diagnostics = new DiagnosticBag();
        var assemblyPath = CompileToAssembly(BclCallSource, OptimizationLevel.Release);
        var input = CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location, typeof(object).Assembly.Location]
        );
        var module = new CilFrontend().Lower(input, diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error)).IsTrue();
        // Not an exact-count check (the BCL guard fires in more than one place — LowerCall's own
        // message, and EnsureLowered's defense-in-depth guard — so more than one diagnostic for the
        // same root cause is expected, not a bug): just that at least one names the real reason.
        await Assert
            .That(diagnostics.Any(d => d.Message.Contains("BCL", StringComparison.Ordinal)))
            .IsTrue();
    }

    // ---- Mem.Alloc/Mem.Reset: NOT yet the compiler-owned [KohIntrinsic] the design calls for -----
    //
    // docs/superpowers/specs/2026-07-14-cil-frontend-design.md's task 2 says Mem.Alloc/Mem.Reset
    // "stay [KohIntrinsic]" (the arena heap is compiler-owned — 'new' bumps the same global). That
    // is the INTENDED end state, but as of this task neither method is actually attributed
    // [KohIntrinsic] in Koh.GameBoy, and this frontend has no "alloc"/"reset" intrinsic kind (only
    // register/region/ei/di/halt/nop/stop). A Mem.Alloc call therefore falls through to ordinary
    // on-demand lowering — ordinary and correct as far as it goes, but Mem.Alloc's body reaches
    // Gb.Base's getter, which calls System.Runtime.CompilerServices.Unsafe.AsPointer (a genuine BCL
    // generic method) — so it still ends in a diagnostic, per the same non-negotiable proven above,
    // just a less direct one than a plain unsupported-call message. Pinned here as "diagnoses,
    // never miscompiles" so this is a known, tracked gap (see notesForNextAgent — samples/gb-3d
    // DOES call Mem.Alloc, so this blocks that sample on the CIL path until closed) rather than a
    // silent one a future change could regress without any test noticing.
    private const string MemAllocSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static unsafe void Main()
            {
                byte* p = Mem.Alloc(4);
                Hardware.BGP = *p;
            }
        }
        """;

    [Test]
    public async Task MemAlloc_NotYetAnIntrinsic_DiagnosesRatherThanMiscompiles()
    {
        var diagnostics = new DiagnosticBag();
        Frontend(MemAllocSource, OptimizationLevel.Release, diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error)).IsTrue();
    }
}
