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
/// Graphics library WAVE 2 / module A: Palettes + Rgb
/// (<c>docs/superpowers/specs/2026-07-15-graphics-library-design.md</c>, build plan slice 3, as
/// amended by §8 resolved decision 2 — EXPLICIT DUAL AUTHORING, no luminance auto-quantize).
/// <c>Koh.GameBoy.Graphics.Palettes.SetBg/SetObj</c> take the four CGB RGB555 colors AND a DMG shade
/// byte; on DMG the shade byte lands in BGP/OBP0/OBP1, on CGB the RGB555 colors land in palette RAM
/// (BCPS/BCPD, OCPS/OCPD). Proves both dispatch paths against a REAL compiled assembly -&gt;
/// CilFrontend -&gt; IrVerifier -&gt; Sm83Backend -&gt; Linker -&gt; GameBoySystem pipeline. Deliberately keeps
/// its own compile-to-assembly harness rather than depending on another test class's internals,
/// mirroring <see cref="CilSpritePaletteHardwareTests"/>'s own stated rationale for doing the same.
/// </summary>
public class CilPalettesRgbTests
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
        "koh-cil-palettes-rgb-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilPalettesRgbAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_palettes_rgb_{Guid.NewGuid():N}.dll");
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

    // ---- Emulator harness (mirrors CilVideoJoypadTests) ----------------------------------------

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

    /// <summary>Runs to completion. Every fixture here calls <c>Video.Init()</c>, which passes through
    /// <c>Lcd.Off</c> -&gt; <c>Ppu.WaitVBlank</c> (the emulator's PPU powers on with LCDC = 0x91,
    /// matching real post-boot-ROM state) plus Init's own map/OAM clear loops, so the step budget needs
    /// the same headroom as <c>CilVideoJoypadTests</c> (measured ~241K steps for <c>Video.Init()</c>
    /// alone in Debug IL).</summary>
    private static void Run(GameBoySystem gb, int start)
    {
        for (int steps = 0; steps < 1_000_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= 0x8000)
                return;
            gb.StepInstruction();
        }
        throw new InvalidOperationException("program did not finish within the step budget");
    }

    // ---- Fixture 1: DMG path — SetBg/SetObj write the dmgShades byte to BGP/OBP0/OBP1 -------------
    //
    // slot 0 for both Bg and Obj; the RGB555 arguments are deliberately non-degenerate (not 0/White)
    // to prove the DMG branch ignores them entirely rather than happening to derive the right shade
    // byte from them.
    private const string DmgSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Palettes.SetBg(0, Rgb.Make(10, 20, 30), Rgb.Red, Rgb.Green, Rgb.Blue, 0x1B);
                Palettes.SetObj(0, 0, Rgb.Red, Rgb.Green, Rgb.Blue, 0xC6);
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task SetBgSetObj_OnDmg_WriteDmgShadesToBgpAndObp0(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(DmgSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(DmgSource, level), out int start, HardwareMode.Dmg);
        Run(gb, start);

        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)0x1B); // BGP <- SetBg dmgShades
        await Assert.That(gb.DebugReadByte(0xFF48)).IsEqualTo((byte)0xC6); // OBP0 <- SetObj slot 0
    }

    // ---- Fixture 2: DMG path — SetObj slot 1 writes OBP1 ------------------------------------------
    private const string DmgObp1Source = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Palettes.SetObj(1, 0, Rgb.Red, Rgb.Green, Rgb.Blue, 0x93);
            }
        }
        """;

    [Test]
    public async Task SetObj_OnDmg_Slot1_WritesObp1()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(DmgObp1Source, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(DmgObp1Source, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        Run(gb, start);

        await Assert.That(gb.DebugReadByte(0xFF49)).IsEqualTo((byte)0x93); // OBP1 <- SetObj slot 1
    }

    // ---- Fixture 3: CGB path — SetBg/SetObj write RGB555 colors into palette RAM -------------------
    //
    // Slot 1 (not 0) proves the index math, not just a degenerate slot-0 write. Reads back through
    // BOTH the I/O port (BCPD/OCPD, which tracks the auto-increment index the writes left behind) and
    // the underlying palette RAM directly (gb.Ppu.BgPalette/ObjPalette), the same double-check
    // CilSpritePaletteHardwareTests uses for OCPD.
    private const string CgbSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Palettes.SetBg(1, Rgb.White, Rgb.Make(20, 25, 20), Rgb.Make(8, 14, 8), Rgb.Black, 0xE4);
                Palettes.SetObj(1, 0, Rgb.Red, Rgb.Green, Rgb.Blue, 0xE4);
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task SetBgSetObj_OnCgb_WriteRgb555ColorsToPaletteRam(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(CgbSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(CgbSource, level), out int start, HardwareMode.Cgb);
        Run(gb, start);

        // BG palette slot 1, colors 0..3: White, Make(20,25,20), Make(8,14,8), Black.
        await Assert
            .That(gb.Ppu.BgPalette.GetColor(1, 0))
            .IsEqualTo(Koh.GameBoy.Graphics.Rgb.White);
        await Assert
            .That(gb.Ppu.BgPalette.GetColor(1, 1))
            .IsEqualTo(Koh.GameBoy.Graphics.Rgb.Make(20, 25, 20));
        await Assert
            .That(gb.Ppu.BgPalette.GetColor(1, 2))
            .IsEqualTo(Koh.GameBoy.Graphics.Rgb.Make(8, 14, 8));
        await Assert
            .That(gb.Ppu.BgPalette.GetColor(1, 3))
            .IsEqualTo(Koh.GameBoy.Graphics.Rgb.Black);

        // OBJ palette slot 1, colors 0..3: (0 = transparent, value irrelevant on hardware, still
        // stored verbatim), Red, Green, Blue.
        await Assert.That(gb.Ppu.ObjPalette.GetColor(1, 1)).IsEqualTo(Koh.GameBoy.Graphics.Rgb.Red);
        await Assert
            .That(gb.Ppu.ObjPalette.GetColor(1, 2))
            .IsEqualTo(Koh.GameBoy.Graphics.Rgb.Green);
        await Assert
            .That(gb.Ppu.ObjPalette.GetColor(1, 3))
            .IsEqualTo(Koh.GameBoy.Graphics.Rgb.Blue);

        // The BCPD/OCPD port readback tracks whichever index each SetBg/SetObj call last left behind
        // (index+1 for the color-3 write, i.e. slot*8 + 3*2 + 1): proves the port and the underlying
        // RAM are in lockstep, not just the RAM.
        await Assert.That(gb.DebugReadByte(0xFF69)).IsEqualTo((byte)(gb.Ppu.BgPalette.RawData[15]));
        await Assert
            .That(gb.DebugReadByte(0xFF6B))
            .IsEqualTo((byte)(gb.Ppu.ObjPalette.RawData[15]));
    }

    // ---- Fixture 4: DMG path — slot > 0 for SetBg, slot > 1 for SetObj are silent no-ops -----------
    //
    // Seeds BGP/OBP0/OBP1 to a known sentinel via a slot-0/1 call, then calls higher slots with
    // DIFFERENT dmgShades and asserts the sentinel survives untouched.
    private const string DmgHigherSlotsSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Palettes.SetBg(0, Rgb.White, Rgb.Red, Rgb.Green, Rgb.Blue, 0x1B);
                Palettes.SetObj(0, 0, Rgb.Red, Rgb.Green, Rgb.Blue, 0x1B);
                Palettes.SetObj(1, 0, Rgb.Red, Rgb.Green, Rgb.Blue, 0x1B);
                Palettes.SetBg(2, Rgb.White, Rgb.Red, Rgb.Green, Rgb.Blue, 0xC6);
                Palettes.SetObj(2, 0, Rgb.Red, Rgb.Green, Rgb.Blue, 0xC6);
            }
        }
        """;

    [Test]
    public async Task SetBgSetObj_OnDmg_SlotsAboveHardwareRange_AreNoOps()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(DmgHigherSlotsSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(DmgHigherSlotsSource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        Run(gb, start);

        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)0x1B); // BGP unchanged by slot 2
        await Assert.That(gb.DebugReadByte(0xFF48)).IsEqualTo((byte)0x1B); // OBP0 unchanged by slot 2
        await Assert.That(gb.DebugReadByte(0xFF49)).IsEqualTo((byte)0x1B); // OBP1 unchanged by slot 2
    }

    // ---- Fixture 5: Rgb.Make packs r | (g << 5) | (b << 10) ---------------------------------------
    private const string RgbMakeSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Hardware.SCX = (byte)Rgb.Make(31, 20, 5);
                Hardware.SCY = (byte)(Rgb.Make(31, 20, 5) >> 8);
            }
        }
        """;

    [Test]
    public async Task RgbMake_PacksChannelsIntoRgb555()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(RgbMakeSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(
            Compile(RgbMakeSource, OptimizationLevel.Debug),
            out int start,
            HardwareMode.Dmg
        );
        Run(gb, start);

        ushort expected = Koh.GameBoy.Graphics.Rgb.Make(31, 20, 5);
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)expected); // SCX = low byte
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)(expected >> 8)); // SCY = high byte
    }
}
