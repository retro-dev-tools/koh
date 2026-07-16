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
using Koh.Emulator.Core.Ppu;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using KohDiagnosticSeverity = Koh.Core.Diagnostics.DiagnosticSeverity;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Frontends;

/// <summary>
/// Records every CPU write that lands in VRAM ($8000-$9FFF) while the PPU owns the bus (mode 3,
/// "Drawing", with the LCD on) — the same thing real hardware silently drops. Mirrors
/// <c>samples/gb-3d/verify/Mode3WriteGuard.cs</c> (not referenced directly: that's a sample project,
/// not something this test project depends on) exactly, including its "LCD off -&gt; PPU doesn't own the
/// bus -&gt; no lockout" and "mode != Drawing -&gt; no lockout" early-outs.
/// </summary>
file sealed class Mode3WriteGuard(GameBoySystem system) : MemoryHook
{
    public List<(ushort Address, byte Value, byte Ly)> Violations { get; } = new();

    public override void OnRead(ushort address, byte value) { }

    public override void OnWrite(ushort address, byte value)
    {
        if (address < 0x8000 || address >= 0xA000)
            return;
        if ((system.Ppu.LCDC & 0x80) == 0)
            return;
        if (system.Ppu.Mode != PpuMode.Drawing)
            return;
        Violations.Add((address, value, system.Ppu.LY));
    }
}

/// <summary>
/// Graphics library WAVE 2 / module A: TileSet
/// (<c>docs/superpowers/specs/2026-07-15-graphics-library-design.md</c>, build plan slice 5) —
/// <c>Koh.GameBoy.Graphics.TileSet</c>. Proves <c>Load</c> (LCD-off straight copy, LCD-on vblank-chunked
/// drip), the sub-range overload, and <c>Load1bpp</c>'s 1bpp-&gt;2bpp planar expansion against a REAL
/// compiled assembly -&gt; CilFrontend -&gt; IrVerifier -&gt; Sm83Backend -&gt; Linker -&gt; GameBoySystem
/// pipeline. Deliberately keeps its own compile-to-assembly harness rather than depending on another
/// test class's internals, mirroring <see cref="CilPalettesRgbTests"/>'s own stated rationale for doing
/// the same.
///
/// See <c>TileSet.cs</c>'s class remarks for why every overload takes an explicit tile count instead of
/// the design's zero-count <c>Load(byte firstTile, byte[] data)</c> sketch: <c>.Length</c> on a
/// <c>byte[]</c> PARAMETER is unsupported by the CIL frontend (confirmed by
/// <c>CilLoweringTests.ArrayLength_OnUntraceableArray_ReportsDiagnostic_DoesNotThrow</c>), so the count
/// must come from the caller, which DOES have the array traced to its own declaration.
/// </summary>
public class CilTileSetTests
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
        "koh-cil-tileset-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilTileSetAsm_" + Guid.NewGuid().ToString("N"),
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
        var path = Path.Combine(ScratchDir, $"cil_tileset_{Guid.NewGuid():N}.dll");
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

    // ---- Emulator harness (mirrors CilPalettesRgbTests / CilVideoJoypadTests) -------------------

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

    /// <summary>Runs to completion. The LCD-on chunked fixture drips several bytes per vblank, so it
    /// needs real headroom over a single <c>Video.Init()</c> (measured ~241K steps alone elsewhere in
    /// this suite) — several vblank waits stack up.</summary>
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

    // ---- Fixture 1: Load, LCD off — straight copy, VRAM bytes equal the source --------------------
    private const string LoadLcdOffSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            static readonly byte[] Tiles =
            {
                0x3C, 0x7E, 0x42, 0x42, 0x42, 0x42, 0x42, 0x42, 0x7E, 0x5E, 0x7E, 0x0A, 0x7C, 0x56, 0x38, 0x7C,
                0xFF, 0x00, 0x7E, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0x7E, 0xFF,
            };

            public static void Main()
            {
                Video.Init();
                TileSet.Load(3, Tiles, 2);
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task Load_LcdOff_CopiesTileBytesVerbatimIntoVram(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(LoadLcdOffSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(LoadLcdOffSource, level), out int start, HardwareMode.Dmg);
        Run(gb, start);

        byte[] expected =
        [
            0x3C,
            0x7E,
            0x42,
            0x42,
            0x42,
            0x42,
            0x42,
            0x42,
            0x7E,
            0x5E,
            0x7E,
            0x0A,
            0x7C,
            0x56,
            0x38,
            0x7C,
            0xFF,
            0x00,
            0x7E,
            0x81,
            0x81,
            0x81,
            0x81,
            0x81,
            0x81,
            0x81,
            0x81,
            0x81,
            0x81,
            0x81,
            0x7E,
            0xFF,
        ];
        ushort baseAddr = (ushort)(0x8000 + 3 * 16); // firstTile = 3
        for (int i = 0; i < expected.Length; i++)
            await Assert
                .That(gb.DebugReadByte((ushort)(baseAddr + i)))
                .IsEqualTo(expected[i])
                .Because($"byte {i}");
    }

    // ---- Fixture 2: Load, sub-range — copies from startTile within data, not data[0] ---------------
    private const string LoadSubRangeSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            static readonly byte[] Tiles =
            {
                0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x11,
                0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22,
                0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33, 0x33,
            };

            public static void Main()
            {
                Video.Init();
                TileSet.Load(10, Tiles, 1, 2); // skip tile 0 (0x11), copy tiles 1,2 (0x22, 0x33)
            }
        }
        """;

    [Test]
    public async Task Load_SubRange_CopiesFromStartTileNotFromDataStart()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(LoadSubRangeSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(LoadSubRangeSource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        Run(gb, start);

        ushort tile10 = (ushort)(0x8000 + 10 * 16);
        ushort tile11 = (ushort)(0x8000 + 11 * 16);
        for (int i = 0; i < 16; i++)
        {
            await Assert.That(gb.DebugReadByte((ushort)(tile10 + i))).IsEqualTo((byte)0x22);
            await Assert.That(gb.DebugReadByte((ushort)(tile11 + i))).IsEqualTo((byte)0x33);
        }
    }

    // ---- Fixture 3: Load1bpp — set bits -> ink, clear bits -> paper, expanded to 2bpp planar --------
    private const string Load1bppSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            // One tile, 8 rows: row 0 = 0b10101010, rest 0 (irrelevant to the assertion).
            static readonly byte[] Glyph = { 0xAA, 0, 0, 0, 0, 0, 0, 0 };

            public static void Main()
            {
                Video.Init();
                TileSet.Load1bpp(5, Glyph, 3, 0, 1); // ink = color 3 (0b11), paper = color 0 (0b00)
            }
        }
        """;

    [Test]
    public async Task Load1bpp_ExpandsSetBitsToInkClearBitsToPaper()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(Load1bppSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(Load1bppSource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        Run(gb, start);

        // ink = 3 (0b11) -> low=1,high=1 where source bit set; paper = 0 (0b00) -> low=0,high=0
        // where clear. Row 0 source = 0xAA (0b10101010) -> low plane = 0xAA, high plane = 0xAA.
        ushort tileBase = (ushort)(0x8000 + 5 * 16);
        await Assert.That(gb.DebugReadByte(tileBase)).IsEqualTo((byte)0xAA); // row 0 low
        await Assert.That(gb.DebugReadByte((ushort)(tileBase + 1))).IsEqualTo((byte)0xAA); // row 0 high
        // Row 1 source = 0x00 (all paper=0) -> low=0, high=0.
        await Assert.That(gb.DebugReadByte((ushort)(tileBase + 2))).IsEqualTo((byte)0x00);
        await Assert.That(gb.DebugReadByte((ushort)(tileBase + 3))).IsEqualTo((byte)0x00);
    }

    // ---- Fixture 4: Load1bpp with a non-degenerate ink/paper pair (proves the mask math, not just
    // the 3/0 all-ones/all-zeros degenerate case) ----------------------------------------------------
    private const string Load1bppMixedSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            static readonly byte[] Glyph = { 0xF0, 0, 0, 0, 0, 0, 0, 0 }; // 0b11110000

            public static void Main()
            {
                Video.Init();
                TileSet.Load1bpp(5, Glyph, 1, 2, 1); // ink = 1 (0b01), paper = 2 (0b10)
            }
        }
        """;

    [Test]
    public async Task Load1bpp_MixedInkPaper_ProducesCorrectPlaneMasks()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(Load1bppMixedSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(Load1bppMixedSource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        Run(gb, start);

        // source = 0xF0 (bits 7..4 set -> ink=1, bits 3..0 clear -> paper=2).
        // ink=1 (0b01): low bit=1, high bit=0 where source bit set.
        // paper=2 (0b10): low bit=0, high bit=1 where source bit clear.
        // low plane:  set bits -> 1 (from ink), clear bits -> 0 (from paper) = 0xF0.
        // high plane: set bits -> 0 (from ink), clear bits -> 1 (from paper) = 0x0F.
        ushort tileBase = (ushort)(0x8000 + 5 * 16);
        await Assert.That(gb.DebugReadByte(tileBase)).IsEqualTo((byte)0xF0); // row 0 low
        await Assert.That(gb.DebugReadByte((ushort)(tileBase + 1))).IsEqualTo((byte)0x0F); // row 0 high
    }

    // ---- Fixture 5: Load, LCD on — vblank-chunked drip, never writes VRAM during mode 3 -------------
    //
    // A Mode3WriteGuard-style hook (mirrors samples/gb-3d/verify/Mode3WriteGuard.cs) attached to the
    // Mmu records every write into $8000-$9FFF while LCDC bit 7 is set and the PPU is in mode 3
    // (Drawing). A timing-safe chunked load never trips it. LCD is left ON (Video.Start()) so the
    // fixture's own Load call must run the vblank-chunked path, not the LCD-off straight copy.
    private const string LoadLcdOnSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            static readonly byte[] Tiles =
            {
                0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
                0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20,
            };

            public static void Main()
            {
                Video.Init();
                Video.Start();
                TileSet.Load(7, Tiles, 2);
            }
        }
        """;

    [Test]
    public async Task Load_LcdOn_ChunksAcrossVblanksAndNeverWritesDuringMode3()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(LoadLcdOnSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(LoadLcdOnSource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        var guard = new Mode3WriteGuard(gb);
        gb.Mmu.Hook = guard;
        Run(gb, start);

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

        byte[] expected =
        [
            0x01,
            0x02,
            0x03,
            0x04,
            0x05,
            0x06,
            0x07,
            0x08,
            0x09,
            0x0A,
            0x0B,
            0x0C,
            0x0D,
            0x0E,
            0x0F,
            0x10,
            0x11,
            0x12,
            0x13,
            0x14,
            0x15,
            0x16,
            0x17,
            0x18,
            0x19,
            0x1A,
            0x1B,
            0x1C,
            0x1D,
            0x1E,
            0x1F,
            0x20,
        ];
        ushort baseAddr = (ushort)(0x8000 + 7 * 16);
        for (int i = 0; i < expected.Length; i++)
            await Assert
                .That(gb.DebugReadByte((ushort)(baseAddr + i)))
                .IsEqualTo(expected[i])
                .Because($"byte {i}");
    }

    // ---- Fixture 6: SetRow/Clear passthrough ------------------------------------------------------
    private const string SetRowClearSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                TileSet.SetRow(9, 0, 0xAB, 0xCD);
                TileSet.Clear(9);
                TileSet.SetRow(9, 3, 0x12, 0x34);
            }
        }
        """;

    [Test]
    public async Task SetRowAndClear_PassThroughToTileData()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(SetRowClearSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(SetRowClearSource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        Run(gb, start);

        ushort tileBase = (ushort)(0x8000 + 9 * 16);
        // row 0 was cleared by TileSet.Clear after being set -> 0.
        await Assert.That(gb.DebugReadByte(tileBase)).IsEqualTo((byte)0);
        await Assert.That(gb.DebugReadByte((ushort)(tileBase + 1))).IsEqualTo((byte)0);
        // row 3 set after the clear -> survives.
        await Assert.That(gb.DebugReadByte((ushort)(tileBase + 6))).IsEqualTo((byte)0x12);
        await Assert.That(gb.DebugReadByte((ushort)(tileBase + 7))).IsEqualTo((byte)0x34);
    }
}
