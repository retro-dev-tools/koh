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
/// CGB hardware coverage for <c>Koh.GameBoy.Graphics.Sprites</c>/<c>Sprite</c>/<c>ObjAttr</c> — closes a
/// gap <see cref="CilSpritesTests"/> left open (it boots DMG only, so <c>ObjAttr.CgbPalette</c> and
/// <see cref="Koh.GameBoy.Graphics.Palettes.SetObj"/>'s CGB branch had no end-to-end hardware coverage).
/// Proves a CGB-palette sprite write lands correctly in real OAM AND that the four authored colors land
/// in real CGB object palette RAM, against a REAL compiled assembly -&gt; CilFrontend -&gt; IrVerifier
/// -&gt; Sm83Backend -&gt; Linker -&gt; GameBoySystem pipeline. Kept as its own file rather than folded
/// into <see cref="CilSpritesTests"/>: that class's <c>Load</c> helper hardcodes
/// <see cref="HardwareMode.Dmg"/> and every existing fixture there is deliberately DMG-only, so a CGB
/// fixture needs its own harness rather than reshaping an established DMG-only file, mirroring how
/// <see cref="CilMapWriterGdmaTests"/> and <see cref="CilBgWinTests"/> each keep their own harness too.
/// </summary>
public class CilSpritesCgbTests
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
        "koh-cil-sprites-cgb-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilSpritesCgbAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_sprites_cgb_{Guid.NewGuid():N}.dll");
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

    // ---- Emulator harness (mirrors CilSpritesTests, but boots CGB) ------------------------------

    private static GameBoySystem Load(EmitModel model, out int start)
    {
        var link = new LinkerType().Link([new Koh.Linker.Core.LinkerInput("cil", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        start = 0x100;
        var gb = new GameBoySystem(HardwareMode.Cgb, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start;
        return gb;
    }

    /// <summary>Allows both ROM and the HRAM OAM-DMA trampoline, exactly like
    /// <c>CilSpritesTests.Run</c> — every fixture here reaches <c>Video.EndFrame</c> -&gt;
    /// <c>Sprites.Flush</c> -&gt; <c>Hardware.RunOamDma</c>.</summary>
    private static void Run(GameBoySystem gb, int start)
    {
        for (int steps = 0; steps < 1_000_000; steps++)
        {
            int pc = gb.Registers.Pc;
            bool inRom = pc >= start && pc < 0x8000;
            bool inHram = pc >= Sm83Backend.OamDmaTrampoline && pc <= 0xFFFF;
            if (!inRom && !inHram)
                return;
            gb.StepInstruction();
        }
        throw new InvalidOperationException("program did not finish within the step budget");
    }

    // ---- Fixture: CGB-palette sprite — real OAM bytes + real CGB OBJ palette RAM ---------------------
    //
    // Four distinct RGB555 colors on OBJ palette slot 0, a sprite drawn at (20, 30) with tile 5 and
    // CgbPalette(0), one EndFrame. SCX (0xFF43) is a completion marker (the register-verdict pattern
    // this suite's sibling fixtures use, e.g. CilMapWriterGdmaTests/CilSpritePaletteHardwareTests'
    // RunUntilDmaTriggered), written after the flush so a test failure that hangs mid-program (rather
    // than landing wrong data) is distinguishable in the step-budget error message.
    private const string CgbSpriteSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Palettes.SetObj(0, 0x001F, 0x03E0, 0x7C00, 0x1234, 0xE4);
                Video.Start();

                Sprite s;
                Sprites.Get(0, out s);
                s.Set(20, 30, 5);
                s.SetAttr(ObjAttr.CgbPalette(0));

                Video.EndFrame();
                Hardware.SCX = 1; // completion marker
            }
        }
        """;

    [Test]
    public async Task CgbPaletteSprite_FlushesRealOam_AndWritesRealObjPaletteRam()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(CgbSpriteSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(CgbSpriteSource, OptimizationLevel.Debug), out int start);
        Run(gb, start);

        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)1); // completion marker reached

        // OAM slot 0: Y = 30+16 = 46, X = 20+8 = 28, Tile = 5, Attr low 3 bits (CGB palette) = 0.
        await Assert.That(gb.DebugReadByte(0xFE00)).IsEqualTo((byte)46); // Y
        await Assert.That(gb.DebugReadByte(0xFE01)).IsEqualTo((byte)28); // X
        await Assert.That(gb.DebugReadByte(0xFE02)).IsEqualTo((byte)5); // Tile
        await Assert.That((byte)(gb.DebugReadByte(0xFE03) & 0x07)).IsEqualTo((byte)0); // CgbPalette(0)

        // CGB OBJ palette RAM, slot 0: index math mirrors Palettes.SetObj's private SetObjectColor
        // ((palette&7)*8 + (color&3)*2) — color 0 -> index 0/1, color 1 -> 2/3, color 2 -> 4/5,
        // color 3 -> 6/7. Each RGB555 color's low byte then high byte, exactly as authored.
        await Assert.That(gb.Ppu.ObjPalette.RawData[0]).IsEqualTo((byte)0x1F); // color0 low  (0x001F)
        await Assert.That(gb.Ppu.ObjPalette.RawData[1]).IsEqualTo((byte)0x00); // color0 high
        await Assert.That(gb.Ppu.ObjPalette.RawData[2]).IsEqualTo((byte)0xE0); // color1 low  (0x03E0)
        await Assert.That(gb.Ppu.ObjPalette.RawData[3]).IsEqualTo((byte)0x03); // color1 high
        await Assert.That(gb.Ppu.ObjPalette.RawData[4]).IsEqualTo((byte)0x00); // color2 low  (0x7C00)
        await Assert.That(gb.Ppu.ObjPalette.RawData[5]).IsEqualTo((byte)0x7C); // color2 high
        await Assert.That(gb.Ppu.ObjPalette.RawData[6]).IsEqualTo((byte)0x34); // color3 low  (0x1234)
        await Assert.That(gb.Ppu.ObjPalette.RawData[7]).IsEqualTo((byte)0x12); // color3 high
    }
}
