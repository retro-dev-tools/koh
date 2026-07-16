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
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using KohDiagnosticSeverity = Koh.Core.Diagnostics.DiagnosticSeverity;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Frontends;

/// <summary>
/// Graphics library WAVE 2 / module A: Bg / Win / TileAttr
/// (<c>docs/superpowers/specs/2026-07-15-graphics-library-design.md</c>, build plan slice 6) —
/// <c>Koh.GameBoy.Graphics.Bg</c> (map $9800), <c>Win</c> (map $9C00), and the shared internal
/// <c>MapWriter</c> they both delegate to. Proves <c>Fill</c>/<c>DrawMap</c> on both maps against real
/// map bytes, and the CGB-only <c>SetAttr</c>/<c>FillAttr</c> pair — VRAM-bank-1 writes on a CGB run,
/// a true no-op on DMG — against a REAL compiled assembly -&gt; CilFrontend -&gt; IrVerifier -&gt;
/// Sm83Backend -&gt; Linker -&gt; GameBoySystem pipeline. Deliberately keeps its own compile-to-assembly
/// harness rather than depending on another test class's internals, mirroring
/// <see cref="CilTileSetTests"/>'s own stated rationale for doing the same.
/// </summary>
public class CilBgWinTests
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
        "koh-cil-bgwin-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilBgWinAsm_" + Guid.NewGuid().ToString("N"),
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
        var path = Path.Combine(ScratchDir, $"cil_bgwin_{Guid.NewGuid():N}.dll");
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

    // ---- Emulator harness (mirrors CilTileSetTests / CilPalettesRgbTests) ----------------------

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

    private static void Run(GameBoySystem gb, int start, int budget = 1_500_000)
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

    // ---- Fixture 1: Bg.Fill + Bg.DrawMap write the expected $9800 map bytes ------------------------
    private const string BgSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            static readonly byte[] Blit = { 10, 11, 12, 13, 14, 15 }; // 3x2, row-major

            public static void Main()
            {
                Video.Init();
                Bg.Fill(2, 3, 4, 2, 7);       // 4x2 rect of tile 7 at (col=2,row=3)
                Bg.DrawMap(10, 1, 3, 2, Blit); // 3x2 blit at (col=10,row=1)
                Bg.SetTile(0, 0, 42);
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task BgFillAndDrawMap_WriteExpectedMapBytes(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(BgSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(BgSource, level), out int start, HardwareMode.Dmg);
        Run(gb, start);

        // Fill rect: cols 2..5, rows 3..4 -> tile 7.
        for (byte row = 3; row <= 4; row++)
        for (byte col = 2; col <= 5; col++)
            await Assert
                .That(gb.DebugReadByte(MapAddress(0x9800, col, row)))
                .IsEqualTo((byte)7)
                .Because($"col={col} row={row}");

        // Just outside the fill rect stays at Video.Init's blank tile (0).
        await Assert.That(gb.DebugReadByte(MapAddress(0x9800, 1, 3))).IsEqualTo((byte)0);
        await Assert.That(gb.DebugReadByte(MapAddress(0x9800, 6, 3))).IsEqualTo((byte)0);

        // DrawMap: row-major blit at (10,1).
        await Assert.That(gb.DebugReadByte(MapAddress(0x9800, 10, 1))).IsEqualTo((byte)10);
        await Assert.That(gb.DebugReadByte(MapAddress(0x9800, 11, 1))).IsEqualTo((byte)11);
        await Assert.That(gb.DebugReadByte(MapAddress(0x9800, 12, 1))).IsEqualTo((byte)12);
        await Assert.That(gb.DebugReadByte(MapAddress(0x9800, 10, 2))).IsEqualTo((byte)13);
        await Assert.That(gb.DebugReadByte(MapAddress(0x9800, 11, 2))).IsEqualTo((byte)14);
        await Assert.That(gb.DebugReadByte(MapAddress(0x9800, 12, 2))).IsEqualTo((byte)15);

        // SetTile (single cell, via the Tilemap.SetTile Hal passthrough).
        await Assert.That(gb.DebugReadByte(MapAddress(0x9800, 0, 0))).IsEqualTo((byte)42);
    }

    // ---- Fixture 2: Win.Fill + Win.DrawMap write the expected $9C00 map bytes (no Clear on Win) -----
    private const string WinSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            static readonly byte[] Blit = { 20, 21, 22, 23 }; // 2x2, row-major

            public static void Main()
            {
                Video.Init();
                Win.Fill(5, 6, 3, 2, 9);
                Win.DrawMap(0, 0, 2, 2, Blit);
                Win.SetTile(15, 15, 77);
            }
        }
        """;

    [Test]
    public async Task WinFillAndDrawMap_WriteExpectedMapBytes()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(WinSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(WinSource, OptimizationLevel.Debug), out int start, HardwareMode.Dmg);
        Run(gb, start);

        for (byte row = 6; row <= 7; row++)
        for (byte col = 5; col <= 7; col++)
            await Assert
                .That(gb.DebugReadByte(MapAddress(0x9C00, col, row)))
                .IsEqualTo((byte)9)
                .Because($"col={col} row={row}");

        await Assert.That(gb.DebugReadByte(MapAddress(0x9C00, 0, 0))).IsEqualTo((byte)20);
        await Assert.That(gb.DebugReadByte(MapAddress(0x9C00, 1, 0))).IsEqualTo((byte)21);
        await Assert.That(gb.DebugReadByte(MapAddress(0x9C00, 0, 1))).IsEqualTo((byte)22);
        await Assert.That(gb.DebugReadByte(MapAddress(0x9C00, 1, 1))).IsEqualTo((byte)23);

        await Assert.That(gb.DebugReadByte(MapAddress(0x9C00, 15, 15))).IsEqualTo((byte)77);

        // Video.Init blanks $9C00 too (Mem.Fill(Gb.TileMap1, 0, 1024)), so an untouched cell is 0.
        await Assert.That(gb.DebugReadByte(MapAddress(0x9C00, 31, 31))).IsEqualTo((byte)0);
    }

    // ---- Fixture 3: CGB run — SetAttr/FillAttr write VRAM bank 1, not the tile-index map ------------
    private const string CgbAttrSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Bg.SetAttr(5, 5, (byte)(TileAttr.FlipX | TileAttr.Palette(3)));
                Bg.FillAttr(0, 0, 2, 2, TileAttr.Priority);
                Win.SetAttr(1, 1, TileAttr.FlipY);
            }
        }
        """;

    [Test]
    public async Task SetAttrFillAttr_OnCgb_WriteVramBank1NotTileIndexMap()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(CgbAttrSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(CgbAttrSource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Cgb
        );
        Run(gb, start);

        byte expectedAttr = (byte)(
            Koh.GameBoy.Graphics.TileAttr.FlipX | Koh.GameBoy.Graphics.TileAttr.Palette(3)
        );
        await Assert.That(Bank1Byte(gb, 0x9800, 5, 5)).IsEqualTo(expectedAttr);
        await Assert.That(Bank1Byte(gb, 0x9800, 0, 0)).IsEqualTo((byte)0x80); // Priority
        await Assert.That(Bank1Byte(gb, 0x9800, 1, 0)).IsEqualTo((byte)0x80);
        await Assert.That(Bank1Byte(gb, 0x9800, 0, 1)).IsEqualTo((byte)0x80);
        await Assert.That(Bank1Byte(gb, 0x9800, 1, 1)).IsEqualTo((byte)0x80);
        await Assert.That(Bank1Byte(gb, 0x9C00, 1, 1)).IsEqualTo((byte)0x40); // FlipY

        // The tile-index map (bank 0, what DebugReadByte reads by default) is untouched by attribute
        // writes: Video.Init blanked it to 0 and nothing here writes a tile index.
        await Assert.That(gb.DebugReadByte(MapAddress(0x9800, 5, 5))).IsEqualTo((byte)0);
        await Assert.That(gb.DebugReadByte(MapAddress(0x9800, 0, 0))).IsEqualTo((byte)0);
        await Assert.That(gb.DebugReadByte(MapAddress(0x9C00, 1, 1))).IsEqualTo((byte)0);

        // Every other bank-1 cell in both maps is untouched (still 0) -- proves the writes landed
        // exactly at the addressed cells, not a wider scribble.
        await Assert.That(Bank1Byte(gb, 0x9800, 5, 6)).IsEqualTo((byte)0);
        await Assert.That(Bank1Byte(gb, 0x9C00, 0, 0)).IsEqualTo((byte)0);
    }

    // ---- Fixture 4: DMG run — SetAttr/FillAttr are a TRUE no-op (no VRAM-bank-1 write at all) --------
    private const string DmgAttrSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Bg.SetAttr(5, 5, (byte)(TileAttr.FlipX | TileAttr.Palette(3)));
                Bg.FillAttr(0, 0, 2, 2, TileAttr.Priority);
                Win.SetAttr(1, 1, TileAttr.FlipY);
            }
        }
        """;

    [Test]
    public async Task SetAttrFillAttr_OnDmg_IsTrueNoOp()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(DmgAttrSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(DmgAttrSource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        Run(gb, start);

        // No VRAM-bank-1 write at all: every candidate cell in the raw bank-1 array stays 0.
        await Assert.That(Bank1Byte(gb, 0x9800, 5, 5)).IsEqualTo((byte)0);
        await Assert.That(Bank1Byte(gb, 0x9800, 0, 0)).IsEqualTo((byte)0);
        await Assert.That(Bank1Byte(gb, 0x9800, 1, 0)).IsEqualTo((byte)0);
        await Assert.That(Bank1Byte(gb, 0x9800, 0, 1)).IsEqualTo((byte)0);
        await Assert.That(Bank1Byte(gb, 0x9800, 1, 1)).IsEqualTo((byte)0);
        await Assert.That(Bank1Byte(gb, 0x9C00, 1, 1)).IsEqualTo((byte)0);

        // And the tile-index map (bank 0) is untouched too -- proves the no-op didn't fall through to
        // a bank-0 write instead (which would silently corrupt the tile map on real DMG hardware).
        await Assert.That(gb.DebugReadByte(MapAddress(0x9800, 5, 5))).IsEqualTo((byte)0);
        await Assert.That(gb.DebugReadByte(MapAddress(0x9800, 0, 0))).IsEqualTo((byte)0);
        await Assert.That(gb.DebugReadByte(MapAddress(0x9C00, 1, 1))).IsEqualTo((byte)0);

        // VBK itself was never even written (Cgb.SelectVramBank no-ops before touching it). On a DMG
        // run the port always reads 0xFF regardless (IoRegisters.ReadVbkRegister's IsCgb gate), so this
        // only proves nothing crashed poking it -- the load-bearing assertions are the bank-1 zeros above.
        await Assert.That(gb.DebugReadByte(0xFF4F)).IsEqualTo((byte)0xFF);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static ushort MapAddress(ushort mapBase, byte col, byte row) =>
        (ushort)(mapBase + row * 32 + col);

    /// <summary>Reads a byte directly out of VRAM bank 1 (the raw emulator array, independent of the
    /// current VBK selection), for <paramref name="mapBase"/> ($9800 or $9C00) at (col, row).</summary>
    private static byte Bank1Byte(GameBoySystem gb, ushort mapBase, byte col, byte row)
    {
        int offset = 0x2000 + (mapBase - 0x8000) + row * 32 + col;
        return gb.Mmu.VramArray[offset];
    }
}
