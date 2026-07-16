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
/// <c>samples/gb-3d/verify/Mode3WriteGuard.cs</c> and <see cref="CilTileSetTests"/>'s own private copy;
/// kept as its own copy here too (each test file keeps its own harness — see this class's remarks).
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
/// Graphics library WAVE 4 / the last v1 module: <c>Koh.GameBoy.Graphics.Canvas</c>
/// (<c>docs/superpowers/specs/2026-07-15-graphics-library-design.md</c>, §8 resolved decision 1,
/// build plan slice 9) — the tile-backed pixel surface consolidating the three
/// <c>samples/gb-3d/*/Surface.cs</c> + <c>shared/SpanFill.cs</c> files. Proves
/// <c>Init</c>/<c>Clear</c>/<c>SetPixel</c>/<c>FillRect</c>/<c>FillSpan</c>/<c>DrawLine</c>/
/// <c>FillTriangle</c>/<c>Present</c> against a REAL compiled assembly -&gt; CilFrontend -&gt;
/// IrVerifier -&gt; Sm83Backend -&gt; Linker -&gt; GameBoySystem pipeline, on both DMG and CGB, and a
/// Mode3WriteGuard-style check that <c>Present</c> never writes VRAM during PPU mode 3. Deliberately
/// keeps its own compile-to-assembly harness rather than depending on another test class's internals,
/// mirroring <see cref="CilTileSetTests"/>'s own stated rationale for doing the same.
/// </summary>
public class CilCanvasTests
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
        "koh-cil-canvas-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilCanvasAsm_" + Guid.NewGuid().ToString("N"),
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
        var path = Path.Combine(ScratchDir, $"cil_canvas_{Guid.NewGuid():N}.dll");
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

    // ---- Emulator harness (mirrors CilTileSetTests / CilSpritesTests) -------------------------

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

    /// <summary>Runs to completion. Generous budget: an LCD-on fixture chunks a Present across several
    /// vblanks (Canvas's DMG CPU chunk is 4 bytes/vblank, same conservative figure as TileSet's own
    /// LCD-on fixture, which needed real headroom for the same reason).</summary>
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

    private static void AssertNoErrors(string source, OptimizationLevel level, out IrModule module)
    {
        var diagnostics = new DiagnosticBag();
        module = Frontend(source, level, diagnostics);
        if (diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "frontend reported errors:\n  "
                    + string.Join("\n  ", diagnostics.Select(d => d.Message))
            );
    }

    // ---- Fixture 1: Init + Clear + SetPixel, LCD off — exact byte match, 2x2-tile canvas -----------
    private const string SetPixelSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Canvas.Init(2, 2, CanvasMode.SingleBuffered);
                Canvas.Clear(0);
                Canvas.SetPixel(0, 0, 3);   // tile 0, byte 0/1
                Canvas.SetPixel(8, 3, 2);   // tile 1, byte 22/23
                Canvas.SetPixel(15, 15, 1); // tile 3, byte 62/63
                Canvas.Present();
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task Init_Clear_SetPixel_Present_LcdOff_MatchesExpectedBuffer(
        OptimizationLevel level
    )
    {
        AssertNoErrors(SetPixelSource, level, out var module);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(SetPixelSource, level), out int start, HardwareMode.Dmg);
        Run(gb, start);

        var expected = new byte[64];
        expected[0] = 0x80;
        expected[1] = 0x80;
        expected[22] = 0x00;
        expected[23] = 0x80;
        expected[62] = 0x01;
        expected[63] = 0x00;

        for (int i = 0; i < expected.Length; i++)
            await Assert
                .That(gb.DebugReadByte((ushort)(0x8000 + i)))
                .IsEqualTo(expected[i])
                .Because($"byte {i}");
    }

    // ---- Fixture 2: FillRect, solid shade (even) — exact byte match, 1x1-tile canvas ---------------
    private const string FillRectSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Canvas.Init(1, 1, CanvasMode.SingleBuffered);
                Canvas.FillRect(0, 0, 8, 8, 6); // shade 6 = solid color 3
                Canvas.Present();
            }
        }
        """;

    [Test]
    public async Task FillRect_SolidShade_FillsEveryPlaneByteFF()
    {
        AssertNoErrors(FillRectSource, OptimizationLevel.Debug, out var module);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(FillRectSource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        Run(gb, start);

        for (int i = 0; i < 16; i++)
            await Assert
                .That(gb.DebugReadByte((ushort)(0x8000 + i)))
                .IsEqualTo((byte)0xFF)
                .Because($"byte {i}");
    }

    // ---- Fixture 3: FillSpan, dithered (odd) shade — exact byte match across two rows --------------
    private const string FillSpanDitherSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Canvas.Init(1, 1, CanvasMode.SingleBuffered);
                Canvas.Clear(0);
                Canvas.FillSpan(0, 0, 7, 3); // shade 3 = dither(color1, color2), row 0
                Canvas.FillSpan(1, 0, 7, 3); // same shade, row 1 (different dither phase)
                Canvas.Present();
            }
        }
        """;

    [Test]
    public async Task FillSpan_DitherShade_ProducesExpectedPlaneBytesPerRow()
    {
        AssertNoErrors(FillSpanDitherSource, OptimizationLevel.Debug, out var module);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(FillSpanDitherSource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        Run(gb, start);

        // row 0: dither = 0x88 >> (0&3) = 0x88; colorLo=1 (bit0 set), colorHi=2 (bit1 set) ->
        // plane0 = 0x00 ^ (0x88 & (0x00^0xFF)) = 0x88; plane1 = 0xFF ^ (0x88 & (0xFF^0x00)) = 0x77.
        await Assert.That(gb.DebugReadByte(0x8000)).IsEqualTo((byte)0x88);
        await Assert.That(gb.DebugReadByte(0x8001)).IsEqualTo((byte)0x77);
        // row 1: dither = 0x88 >> (1&3) = 0x44 -> plane0 = 0x44, plane1 = 0xFF ^ 0x44 = 0xBB.
        await Assert.That(gb.DebugReadByte(0x8002)).IsEqualTo((byte)0x44);
        await Assert.That(gb.DebugReadByte(0x8003)).IsEqualTo((byte)0xBB);
    }

    // ---- Fixture 4: DrawLine, LCD on — Mode3WriteGuard + exact diagonal pixels ----------------------
    private const string DrawLineSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Canvas.Init(2, 2, CanvasMode.SingleBuffered);
                Video.Start();
                Canvas.Clear(0);
                Canvas.DrawLine(0, 0, 15, 15, 3); // perfect diagonal: dx==dy
                Canvas.Present();
            }
        }
        """;

    [Test]
    public async Task DrawLine_LcdOn_NeverWritesDuringMode3_AndDrawsExpectedDiagonal()
    {
        AssertNoErrors(DrawLineSource, OptimizationLevel.Debug, out var module);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(DrawLineSource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        var guard = new Mode3WriteGuard(gb);
        gb.Mmu.Hook = guard;
        Run(gb, start, budget: 6_000_000);

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

        // (0,0): tile 0, byte 0/1, color 3 -> both planes bit 0x80 set.
        await Assert.That(gb.DebugReadByte(0x8000)).IsEqualTo((byte)0x80);
        await Assert.That(gb.DebugReadByte(0x8001)).IsEqualTo((byte)0x80);
        // (8,8): tile 3 (row 1 * widthTiles 2 + col 1), byte offset 48, color 3 -> both planes 0x80.
        await Assert.That(gb.DebugReadByte((ushort)(0x8000 + 48))).IsEqualTo((byte)0x80);
        await Assert.That(gb.DebugReadByte((ushort)(0x8000 + 49))).IsEqualTo((byte)0x80);
        // (15,15): tile 3, byte offset 62/63, color 3 -> both planes mask 0x01 set.
        await Assert.That(gb.DebugReadByte((ushort)(0x8000 + 62))).IsEqualTo((byte)0x01);
        await Assert.That(gb.DebugReadByte((ushort)(0x8000 + 63))).IsEqualTo((byte)0x01);
    }

    // ---- Fixture 5: FillTriangle — exact byte match + Mode3WriteGuard, LCD on ------------------------
    private const string FillTriangleSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Canvas.Init(1, 1, CanvasMode.SingleBuffered);
                Video.Start();
                Canvas.Clear(0);
                Canvas.FillTriangle(0, 0, 7, 0, 7, 7, 6); // shade 6 = solid color 3
                Canvas.Present();
            }
        }
        """;

    [Test]
    public async Task FillTriangle_LcdOn_NeverWritesDuringMode3_AndFillsExpectedSpans()
    {
        AssertNoErrors(FillTriangleSource, OptimizationLevel.Debug, out var module);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(FillTriangleSource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        var guard = new Mode3WriteGuard(gb);
        gb.Mmu.Hook = guard;
        Run(gb, start, budget: 6_000_000);

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

        // row 0: full-width span (xa=0, xb=7) -> both plane bytes 0xFF.
        await Assert.That(gb.DebugReadByte(0x8000)).IsEqualTo((byte)0xFF);
        await Assert.That(gb.DebugReadByte(0x8001)).IsEqualTo((byte)0xFF);
        // row 3: span x=3..7 -> cover mask 0x1F, both plane bytes 0x1F.
        await Assert.That(gb.DebugReadByte(0x8006)).IsEqualTo((byte)0x1F);
        await Assert.That(gb.DebugReadByte(0x8007)).IsEqualTo((byte)0x1F);
        // row 7: single pixel x=7 -> cover mask 0x01, both plane bytes 0x01.
        await Assert.That(gb.DebugReadByte(0x800E)).IsEqualTo((byte)0x01);
        await Assert.That(gb.DebugReadByte(0x800F)).IsEqualTo((byte)0x01);
    }

    // ---- Fixture 6: DoubleBuffered Present — LCDC.4 flip + correct hidden-page targeting, LCD on -----
    private const string DoubleBufferedSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Canvas.Init(1, 1, CanvasMode.DoubleBuffered);
                Video.Start();
                Canvas.Clear(0);
                Canvas.SetPixel(0, 0, 3);
                Canvas.Present(); // 1st: hidden page 1 ($9000) -> flip to $8800 addressing (LCDC.4 clear)
                Canvas.Clear(0);
                Canvas.SetPixel(1, 0, 3);
                Canvas.Present(); // 2nd: hidden page 0 ($8000) -> flip to $8000 addressing (LCDC.4 set)
            }
        }
        """;

    [Test]
    public async Task Present_DoubleBuffered_LcdOn_FlipsLcdcBit4_NeverWritesDuringMode3()
    {
        AssertNoErrors(DoubleBufferedSource, OptimizationLevel.Debug, out var module);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(DoubleBufferedSource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        var guard = new Mode3WriteGuard(gb);
        gb.Mmu.Hook = guard;
        Run(gb, start, budget: 6_000_000);

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

        // After the 2nd Present, LCDC bit 4 is SET ($8000 addressing) — the 2nd draw landed at $8000.
        await Assert.That((gb.Ppu.LCDC & 0x10) != 0).IsTrue();
        // 1st draw (pixel (0,0)) landed at the hidden page of the FIRST present, $9000.
        await Assert.That(gb.DebugReadByte(0x9000)).IsEqualTo((byte)0x80);
        await Assert.That(gb.DebugReadByte(0x9001)).IsEqualTo((byte)0x80);
        // 2nd draw (pixel (1,0)) landed at $8000 (the hidden page of the SECOND present).
        await Assert.That(gb.DebugReadByte(0x8000)).IsEqualTo((byte)0x40);
        await Assert.That(gb.DebugReadByte(0x8001)).IsEqualTo((byte)0x40);
    }

    // ---- Fixture 7: CGB — GDMA present path, exact byte match + Mode3WriteGuard ----------------------
    private const string CgbSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Canvas.Init(1, 1, CanvasMode.SingleBuffered);
                Video.Start();
                Canvas.Clear(0);
                Canvas.SetPixel(0, 0, 3);
                Canvas.Present();
            }
        }
        """;

    [Test]
    public async Task Present_Cgb_UsesGdma_NeverWritesDuringMode3_AndMatchesExpectedBuffer()
    {
        AssertNoErrors(CgbSource, OptimizationLevel.Debug, out var module);
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(CgbSource, OptimizationLevel.Debug), out int start, HardwareMode.Cgb);
        var guard = new Mode3WriteGuard(gb);
        gb.Mmu.Hook = guard;
        Run(gb, start, budget: 6_000_000);

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

        await Assert.That(gb.DebugReadByte(0x8000)).IsEqualTo((byte)0x80);
        await Assert.That(gb.DebugReadByte(0x8001)).IsEqualTo((byte)0x80);
    }
}
