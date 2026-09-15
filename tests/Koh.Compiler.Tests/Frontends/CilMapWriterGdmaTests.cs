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
using Koh.Emulator.Core.Debug;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using KohDiagnosticSeverity = Koh.Core.Diagnostics.DiagnosticSeverity;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Frontends;

/// <summary>
/// The BG/window tilemap flush (<c>MapWriter</c>, see its own class remarks "Single-vblank GDMA flush on
/// CGB, split into PREPARE (outside vblank) + COMMIT (inside vblank)") now lands the WHOLE dirty run of a
/// map in one GDMA transfer on CGB, instead of draining it over several frames the way the DMG path
/// (<c>FlushRun</c>, unchanged) still does. <c>MapWriter.PrepareFlush</c> computes every HDMA1-5 register
/// byte BEFORE <c>Ppu.WaitVBlank</c>; <c>MapWriter.Flush</c>'s CGB branch, after the wait, is then just a
/// safety-gated five-register-store commit. Proves this against a REAL compiled assembly -&gt;
/// CilFrontend -&gt; IrVerifier -&gt; Sm83Backend -&gt; Linker -&gt; GameBoySystem pipeline: a CGB run
/// lands a scattered, ~230-byte-wide dirty span after exactly ONE
/// <see cref="Koh.GameBoy.Graphics.Video.EndFrame"/>, with zero Mode3WriteGuard violations, and a
/// completed flush never spuriously re-commits over hardware-side changes; the same program on DMG still
/// needs several frames, proving the drip path is untouched. Deliberately keeps its own compile-to-
/// assembly harness rather than depending on another test class's internals, mirroring
/// <see cref="CilBgWinTests"/>'s own stated rationale for doing the same.
/// </summary>
public class CilMapWriterGdmaTests
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
        "koh-cil-mapwriter-gdma-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilMapWriterGdmaAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_mapwriter_gdma_{Guid.NewGuid():N}.dll");
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
        IrOptimizer.Optimize(module);
        return new Sm83Backend().Compile(module, new DiagnosticBag());
    }

    // ---- Emulator harness (mirrors CilBgWinTests / CilSpritePaletteHardwareTests) ----------------

    private static GameBoySystem Load(EmitModel model, out int start, HardwareMode mode)
    {
        var link = new LinkerType().Link([new Koh.Linker.Core.LinkerInput("cil", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        start = 0x100;
        var gb = new GameBoySystem(mode, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start;
        return gb;
    }

    private static void Run(GameBoySystem gb, int start, int budget = 3_000_000)
    {
        for (int steps = 0; steps < budget; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= 0x8000)
                return;
            gb.StepInstruction();
        }
        throw new InvalidOperationException("program did not finish within the step budget");
    }

    /// <summary>Steps the CPU until it writes <paramref name="value"/> into <paramref name="addr"/> —
    /// the "register-verdict" completion-marker pattern <c>CilSpritePaletteHardwareTests.RunUntilDmaTriggered</c>
    /// uses to drive the emulator up to a precise point mid-program rather than only to natural
    /// completion. Used here so the DMG fixture can sample tilemap state exactly ONE
    /// <see cref="Koh.GameBoy.Graphics.Video.EndFrame"/> in, then keep driving and sample again after
    /// each subsequent frame, without guessing how many CPU steps one frame costs. Allows both ROM and
    /// the HRAM OAM-DMA trampoline, per <c>CilSpritesTests.Run</c>'s own remarks — every fixture here
    /// reaches <c>Video.EndFrame</c> -&gt; <c>Sprites.Flush</c> -&gt; <c>Hardware.RunOamDma</c> at least
    /// once (the shadow OAM starts dirty from <c>Video.Init</c>'s <c>Sprites.HideAll</c>), which
    /// legitimately visits HRAM (0xFF80+), not just ROM.</summary>
    private static void RunUntilRegisterEquals(
        GameBoySystem gb,
        int start,
        ushort addr,
        byte value,
        int budget = 3_000_000
    )
    {
        for (int steps = 0; steps < budget; steps++)
        {
            int pc = gb.Registers.Pc;
            bool inRom = pc >= start && pc < 0x8000;
            bool inHram = pc >= Sm83Backend.OamDmaTrampoline && pc <= 0xFFFF;
            if (!inRom && !inHram)
                throw new InvalidOperationException(
                    $"program ended before {addr:X4} reached {value:X2}"
                );
            gb.StepInstruction();
            if (gb.DebugReadByte(addr) == value)
                return;
        }
        throw new InvalidOperationException(
            $"{addr:X4} never reached {value:X2} within the step budget"
        );
    }

    // ---- Shared fixture shape: 3 scattered Bg cells spanning idx 98 (=3*32+2) .. 325 (=10*32+5), a
    // ~230-byte dirty run once MapWriter's single-contiguous-range tracking sees all three. -----------

    private static ushort CellAddr(byte col, byte row) => (ushort)(0x9800 + row * 32 + col);

    private const byte Col1 = 2,
        Row1 = 3,
        Tile1 = 7; // idx 98
    private const byte Col2 = 19,
        Row2 = 3,
        Tile2 = 9; // idx 115
    private const byte Col3 = 5,
        Row3 = 10,
        Tile3 = 4; // idx 325 (far cell — spans the whole ~230-byte run from idx 98)

    // SCX (0xFF43, an otherwise-unused-by-this-fixture DMG/CGB register) is the completion marker: the
    // register-verdict pattern this suite's sibling fixtures use (RunUntilDmaTriggered watches DMA/
    // 0xFF46 the same way). Written once right after the first EndFrame (checkpoint 1), then once more
    // per loop iteration on the DMG fixture (checkpoint N = N total EndFrame calls done).
    private const ushort Marker = 0xFF43;

    // ---- Fixture 1: CGB — the whole scattered run lands after exactly ONE EndFrame, and a second
    // EndFrame with nothing newly dirty never spuriously re-commits over a hardware-side change. -------
    private const string CgbSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Video.Start();
                Bg.SetTile(2, 3, 7);
                Bg.SetTile(19, 3, 9);
                Bg.SetTile(5, 10, 4);
                Video.EndFrame();
                Hardware.SCX = 1; // checkpoint: exactly one EndFrame has run
                Video.EndFrame();
                Hardware.SCX = 2; // checkpoint: a second EndFrame, nothing newly dirtied
            }
        }
        """;

    [Test]
    public async Task Cgb_ScatteredRun_LandsCompletelyAfterOneEndFrame_ViaGdma()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(CgbSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(CgbSource, OptimizationLevel.Debug), out int start, HardwareMode.Cgb);
        var guard = new Mode3WriteGuard(gb);
        gb.Mmu.Hook = guard;

        RunUntilRegisterEquals(gb, start, Marker, 1);

        // All three scattered cells landed in VRAM after exactly one EndFrame — on the DMG drip a
        // ~230-byte span could not have finished in one vblank (see the DMG fixture below), so this is
        // only possible via the single-shot GDMA flush.
        await Assert.That(gb.DebugReadByte(CellAddr(Col1, Row1))).IsEqualTo(Tile1);
        await Assert.That(gb.DebugReadByte(CellAddr(Col2, Row2))).IsEqualTo(Tile2);
        await Assert.That(gb.DebugReadByte(CellAddr(Col3, Row3))).IsEqualTo(Tile3);

        // No-spurious-reflush check: the first flush must have fully drained the dirty state (both the
        // DirtyBg flag and the CGB-only PreparedBgReady flag) — poke ONE of the just-flushed cells to a
        // value the shadow does NOT hold, then run a second EndFrame with nothing newly dirtied. If
        // MapWriter still thought the map (or that GDMA transfer) was pending, this second flush would
        // re-copy the shadow's ORIGINAL value over the poke; if drain was really complete, the poke
        // survives untouched.
        gb.DebugWriteByte(CellAddr(Col1, Row1), 0xAA);
        await Assert
            .That(gb.DebugReadByte(CellAddr(Col1, Row1)))
            .IsEqualTo((byte)0xAA)
            .Because("the debug poke itself must have landed to be a valid probe");

        RunUntilRegisterEquals(gb, start, Marker, 2);
        await Assert
            .That(gb.DebugReadByte(CellAddr(Col1, Row1)))
            .IsEqualTo((byte)0xAA)
            .Because(
                "a second EndFrame with nothing newly dirty must not re-commit the earlier GDMA transfer "
                    + "over a hardware-side change — DirtyBg/PreparedBgReady must both be fully drained"
            );

        await Assert
            .That(guard.Violations)
            .IsEmpty()
            .Because(
                "no VRAM write may land during PPU mode 3 while the LCD is on: "
                    + string.Join(
                        ", ",
                        guard.Violations.Select(v => $"${v.Address:X4}=${v.Value:X2}@LY={v.Ly}")
                    )
            );
    }

    // ---- Fixture 2: DMG — the same scattered run still needs several frames (the drip is untouched) ---
    //
    // Writes SCX=1 right after the first EndFrame, then loops up to 60 more times, writing SCX=(2+i)
    // after each subsequent EndFrame — a per-frame checkpoint sequence the harness drives through with
    // RunUntilRegisterEquals, sampling tilemap state at every checkpoint. This measures (rather than
    // assumes) how many frames the drip needs, per CLAUDE.md's "measure, don't guess" instruction.
    private const string DmgSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Video.Start();
                Bg.SetTile(2, 3, 7);
                Bg.SetTile(19, 3, 9);
                Bg.SetTile(5, 10, 4);
                Video.EndFrame();
                Hardware.SCX = 1; // checkpoint: exactly 1 EndFrame done
                for (byte i = 0; i < 60; i++)
                {
                    Video.EndFrame();
                    Hardware.SCX = (byte)(2 + i); // checkpoint: (2+i) total EndFrame calls done
                }
            }
        }
        """;

    [Test]
    public async Task Dmg_ScatteredRun_StillDripsAcrossSeveralFrames_DripPathUnchanged()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(DmgSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(DmgSource, OptimizationLevel.Debug), out int start, HardwareMode.Dmg);

        bool AllLanded() =>
            gb.DebugReadByte(CellAddr(Col1, Row1)) == Tile1
            && gb.DebugReadByte(CellAddr(Col2, Row2)) == Tile2
            && gb.DebugReadByte(CellAddr(Col3, Row3)) == Tile3;

        // Checkpoint 1: exactly one EndFrame has run. The drip must NOT have landed the whole run yet —
        // this is the load-bearing assertion that distinguishes the DMG path from the CGB one above.
        RunUntilRegisterEquals(gb, start, Marker, 1);
        await Assert
            .That(AllLanded())
            .IsFalse()
            .Because(
                "a ~230-byte scattered run should not fit in a single DMG vblank drip — if it does, "
                    + "the DMG path may have regressed onto the CGB GDMA behavior, or the drip rate "
                    + "changed enough that this fixture's span needs to be widened"
            );

        // Keep driving through each subsequent frame's checkpoint (2, 3, 4, ...) until AllLanded()
        // measures true, or the program's own 60-iteration loop bound is exhausted. This is a
        // measurement, not an assumed frame count.
        int? framesToLand = null;
        for (byte checkpoint = 2; checkpoint < 62; checkpoint++)
        {
            RunUntilRegisterEquals(gb, start, Marker, checkpoint);
            if (AllLanded())
            {
                framesToLand = checkpoint;
                break;
            }
        }

        await Assert
            .That(framesToLand.HasValue)
            .IsTrue()
            .Because(
                "the DMG drip should still finish the run within the fixture's 61-frame budget"
            );
        await Assert
            .That(framesToLand!.Value)
            .IsGreaterThan(1)
            .Because(
                "landing in exactly 1 frame would mean the drip is no longer draining — unexpected"
            );
    }
}
