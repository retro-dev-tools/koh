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
using Koh.Emulator.Core.Joypad;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using KohDiagnosticSeverity = Koh.Core.Diagnostics.DiagnosticSeverity;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Frontends;

/// <summary>
/// Graphics library WAVE 2 / module A: the frame-loop spine
/// (<c>docs/superpowers/specs/2026-07-15-graphics-library-design.md</c>, build plan slice 4) —
/// <c>Koh.GameBoy.Graphics.Video</c> (lifecycle, layer toggles, <c>EndFrame</c>) and the additive
/// <c>Koh.GameBoy.Joypad</c> members (<c>ReadAll</c>/<c>Pressed</c>/<c>IsPressed</c>). Proves each
/// against a REAL compiled assembly -&gt; CilFrontend -&gt; IrVerifier -&gt; Sm83Backend -&gt; Linker -&gt;
/// GameBoySystem pipeline. Deliberately keeps its own compile-to-assembly harness rather than depending
/// on another test class's internals, mirroring <see cref="CilSpritePaletteHardwareTests"/>'s own
/// stated rationale for doing the same.
/// </summary>
public class CilVideoJoypadTests
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
        "koh-cil-video-joypad-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilVideoJoypadAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_video_joypad_{Guid.NewGuid():N}.dll");
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

    // ---- Emulator harness (mirrors CilSpritePaletteHardwareTests / CilGraphicsSlice2Tests) -----

    private static GameBoySystem Load(EmitModel model, out int start)
    {
        var link = new LinkerType().Link([new Koh.Linker.Core.LinkerInput("cil", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        start = 0x100;
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start;
        return gb;
    }

    /// <summary>Runs to completion. Every fixture's <c>Main</c> that touches <c>Video.Init</c> passes
    /// through <c>Lcd.Off</c>, which spins in <c>Ppu.WaitVBlank</c> when the LCD starts on (the
    /// emulator's PPU powers on with LCDC = 0x91, matching real post-boot-ROM state, per
    /// <c>Koh.Emulator.Core.Ppu.Ppu</c>) — one full vblank wait (~17.5K M-cycles) on top of
    /// <c>Init</c>'s own map/OAM clears (unoptimized SM83 loop bodies, not raw M-cycles), so the step
    /// budget needs real headroom over a handful of instructions (measured ~241K steps for
    /// <c>Video.Init()</c> alone in Debug IL).
    ///
    /// Also allows the HRAM OAM-DMA trampoline (0xFF80+, <c>Sm83Backend.OamDmaTrampoline</c>) — since
    /// the Sprites module's wiring into <c>Video.EndFrame</c> (graphics-library design doc, slice 8),
    /// <c>EndFrame</c> legitimately calls <c>Hardware.RunOamDma</c> whenever the shadow OAM is dirty
    /// (every fixture here: <c>Video.Init</c> itself dirties it via <c>Sprites.HideAll</c>), so PC
    /// visits HRAM, not just ROM, before this loop's own "left ROM -&gt; done" exit condition would
    /// otherwise fire early and strand a fixture mid-<c>EndFrame</c> — mirrors
    /// <c>CilGraphicsSlice2Tests.Run</c>'s and <c>CilSpritesTests.Run</c>'s own identical allowance.</summary>
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

    // ---- Fixture 1: LCDC composes across Video calls, not just the Start() default ---------------
    //
    // ShowSprites(Size8x16) then ShowWindow(24, 40) then Start() must all land in the SAME LCDC byte:
    // bit7 (on) | bit6 (window map $9C00) | bit5 (window enable) | bit4 (tile data $8000, set by
    // Init) | bit2 (OBJ size 8x16) | bit1 (OBJ enable) | bit0 (BG enable, set by Init) = 0xF7. A plain
    // Lcd.On()-style constant (0x91) would NOT reflect the sprite/window calls, so this is the proof
    // the internal LCDC mirror actually composes rather than each Show* call clobbering the others.
    private const string LcdcCompositionSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Video.ShowSprites(SpriteSize.Size8x16);
                Video.ShowWindow(24, 40);
                Video.Start();
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task LcdcComposesAcrossShowCallsAndStart(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(LcdcCompositionSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(LcdcCompositionSource, level), out int start);
        Run(gb, start);

        await Assert.That(gb.DebugReadByte(0xFF40)).IsEqualTo((byte)0xF7); // LCDC
        await Assert.That(gb.DebugReadByte(0xFF4B)).IsEqualTo((byte)31); // WX = x(24) + 7
        await Assert.That(gb.DebugReadByte(0xFF4A)).IsEqualTo((byte)40); // WY
    }

    // ---- Fixture 2: Init clears both tile maps + OAM and sets the default DMG palette -------------
    //
    // The emulator poisons VRAM/OAM with 0xFF at power-on (Koh.Emulator.Core.Bus.Mmu ctor), so reading
    // back 0 after Init proves the fill actually ran (not a vacuous "happened to start at 0" pass).
    private const string InitSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
            }
        }
        """;

    [Test]
    public async Task Init_ClearsBothTileMapsAndOamAndSetsDefaultDmgPalette()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(InitSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(InitSource, OptimizationLevel.Debug), out int start);
        Run(gb, start);

        await Assert.That(gb.DebugReadByte(0x9800)).IsEqualTo((byte)0); // BG map ($9800)
        await Assert.That(gb.DebugReadByte(0x9BFF)).IsEqualTo((byte)0); // BG map, last cell
        await Assert.That(gb.DebugReadByte(0x9C00)).IsEqualTo((byte)0); // window map ($9C00)
        await Assert.That(gb.DebugReadByte(0x9FFF)).IsEqualTo((byte)0); // window map, last cell
        await Assert.That(gb.DebugReadByte(0xFE00)).IsEqualTo((byte)0); // OAM sprite 0's Y -> hidden
        await Assert.That(gb.DebugReadByte(0xFE9F)).IsEqualTo((byte)0); // OAM, last byte
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)0xE4); // BGP
        await Assert.That(gb.DebugReadByte(0xFF48)).IsEqualTo((byte)0xE4); // OBP0
        await Assert.That(gb.DebugReadByte(0xFF49)).IsEqualTo((byte)0xE4); // OBP1
        await Assert.That((gb.DebugReadByte(0xFF40) & 0x80)).IsEqualTo(0); // screen stays off (bit7)
    }

    // ---- Fixture 3: EndFrame waits for vblank and advances FrameCount ----------------------------
    //
    // FrameCount is read back through an otherwise-unused register (SCX) rather than a linker symbol
    // lookup, mirroring CilStaticsTests' own "write the static into a register, read the register
    // back" pattern.
    private const string EndFrameSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Video.Start();
                Video.EndFrame();
                Hardware.SCX = Video.FrameCount;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task EndFrame_WaitsForVblankAndAdvancesFrameCount(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(EndFrameSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(EndFrameSource, level), out int start);
        Run(gb, start);

        byte ly = gb.DebugReadByte(0xFF44);
        await Assert.That(ly is >= 144 and <= 153).IsTrue();
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)1); // SCX <- FrameCount
    }

    // ---- Fixture 4: Joypad.Pressed reports a rising edge, not the held level ----------------------
    //
    // The joypad state is scripted BEFORE running (GameBoySystem.JoypadPress), held constant for the
    // whole run: Pressed()'s own previous-mask starts at 0, so the FIRST read is a rising edge and the
    // SECOND read (same held button) is not — exactly the "held across two calls reports once"
    // contract, with no mid-run script needed.
    private const string PressedSource = """
        using Koh.GameBoy;

        public class Program
        {
            public static void Main()
            {
                Hardware.SCX = Joypad.Pressed();
                Hardware.SCY = Joypad.Pressed();
            }
        }
        """;

    [Test]
    public async Task Pressed_ReportsRisingEdgeOnceForAHeldButton()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(PressedSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(PressedSource, OptimizationLevel.Debug), out int start);
        gb.JoypadPress(JoypadButton.Right);
        Run(gb, start);

        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)0x01); // SCX: rising edge
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)0x00); // SCY: still held, not rising
    }
}
