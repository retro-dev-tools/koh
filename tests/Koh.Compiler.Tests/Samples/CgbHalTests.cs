using System.Collections.Immutable;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.Cil;
using Koh.Core.Diagnostics;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Samples;

/// <summary>
/// The Koh.GameBoy/Hal framework (Cgb, Ppu, ...) is ordinary compiled code already shipped inside
/// Koh.GameBoy.dll (see CLAUDE.md: "framework code ... lowered on demand from REFERENCED ASSEMBLIES"),
/// so unlike the pre-CIL version of this file, no Hal source needs concatenating here — a test just
/// compiles its own small entry point with Roslyn, referencing Koh.GameBoy.dll, and
/// <see cref="CilFrontend"/> pulls in Cgb/Ppu's IL on demand.
/// </summary>
public class CgbHalTests
{
    // ---- Roslyn: compile real C# to a real assembly on disk, referencing Koh.GameBoy.dll -----------

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
        "koh-cgb-hal-tests"
    );

    private const string GlobalUsings = "global using Koh.GameBoy;\n";

    private static string CompileToAssembly(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(GlobalUsings + source);
        var compilation = CSharpCompilation.Create(
            "CgbHalAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cgbhal_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    [Test]
    public async Task CgbHal_CompilesThroughTheRealBackend()
    {
        const string source =
            "static class Program { static byte Main() { "
            + "bool color = Cgb.IsColor(); "
            + "Cgb.SelectVramBank(1); "
            + "Cgb.SetBackgroundColor(0, 0, 0x1234); "
            + "Cgb.TryEnableDoubleSpeed(); "
            + "Ppu.WaitForVramAccess(); Ppu.WaitForHBlank(); "
            + "return (byte)(color ? 1 : 0); } }\n";

        var assemblyPath = CompileToAssembly(source);
        var input = CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        var diagnostics = new DiagnosticBag();
        var module = new CilFrontend().Lower(input, diagnostics);
        new Sm83Backend().Compile(module, diagnostics);

        var errors = diagnostics
            .Where(d => d.Severity == Koh.Core.Diagnostics.DiagnosticSeverity.Error)
            .Select(d => d.Message)
            .ToArray();
        await Assert.That(errors).IsEmpty();
    }

    // ---- End-to-end: run the compiled Cgb.* calls on the emulator and observe the CGB effects it
    // models, so a wrong register offset or bit mask (which would still compile clean) fails a test.
    //
    // What the emulator (src/Koh.Emulator.Core) models for CGB and what these tests check:
    //   - KEY1 ($FF4D) double-speed switch (KeyOneRegister) -> checked below.
    //   - VBK ($FF4F) VRAM bank select (VramWramBanking) -> checked below.
    //   - BCPS/BCPD ($FF68/$FF69) background palette RAM (CgbPalette) -> checked below.
    // What it does NOT model (so these tests can't exercise it, and don't try to fake it):
    //   - The Pan-Docs speed-switch hazard where a pending/firing interrupt right at STOP derails the
    //     switch: Sm83.Stop() only ever consults KeyOneRegister.SwitchArmed, never IME/IF, so the
    //     DI / JOYP=$30 / IF=0 / EI sequence Cgb.TryEnableDoubleSpeed() now performs (Pan Docs' canonical
    //     sequence) always succeeds here whether or not it runs that sequence. The sequence is exercised
    //     for effect (it must still leave the switch working, checked below) but the specific hazard it
    //     guards against is unmodeled and unverifiable through this harness.
    //   - The ~130k T-cycle PLL relock stall after a real speed switch (deliberately not modelled by the
    //     emulator either, per Sm83.Stop()'s comment).
    //   - OBJ palette RAM (OCPS/OCPD) and HDMA are modeled too but unused by Cgb.cs, so not asserted here.

    /// <summary>Compile <paramref name="testMain"/> (wrapped in a class - Hal's Cgb/Ppu resolve from the
    /// referenced Koh.GameBoy.dll on demand, see <see cref="CilFrontend"/>), link it, boot it on a
    /// <see cref="GameBoySystem"/> in <paramref name="mode"/>, and run it to completion. Returns both the
    /// result (HL) and the live system so a test can inspect emulator-side CGB state directly.</summary>
    private static (ushort Hl, GameBoySystem Gb) Run(string testMain, HardwareMode mode)
    {
        var assemblyPath = CompileToAssembly("static class Program {\n" + testMain + "\n}");
        var input = CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        var module = new CilFrontend().Lower(input, new DiagnosticBag());
        var model = new Sm83Backend().Compile(module, new DiagnosticBag());
        var rom = new LinkerType().Link([new LinkerInput("t", model)]).RomData!;

        int start = Sm83Backend.CodeBase;
        int length = model.Sections[0].Data.Length;
        var gb = new GameBoySystem(mode, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start; // the test Main is emitted first -> entry is at CodeBase
        for (int steps = 0; steps < 1_000_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= start + length)
                break;
            gb.StepInstruction();
        }
        return (gb.Registers.HL, gb);
    }

    [Test]
    public async Task TryEnableDoubleSpeed_FlipsKey1Bit7_InCgbMode()
    {
        // ushort/HL return (not byte/A) to match Run()'s Game2048Tests-style harness below.
        const string main =
            "static ushort Main() { return (ushort)(Cgb.TryEnableDoubleSpeed() ? 1 : 0); }";
        var (result, gb) = Run(main, HardwareMode.Cgb);

        await Assert.That(result).IsEqualTo((ushort)1); // TryEnableDoubleSpeed() reported success
        await Assert.That(gb.KeyOne.DoubleSpeed).IsTrue(); // and the emulator's speed state flipped
        await Assert.That((gb.DebugReadByte(0xFF4D) & 0x80) != 0).IsTrue(); // KEY1 bit 7 reads back set
        await Assert.That(gb.KeyOne.SwitchArmed).IsFalse(); // the switch disarms itself after firing
    }

    [Test]
    public async Task TryEnableDoubleSpeed_DoesNothingHarmful_InDmgMode()
    {
        const string main =
            "static ushort Main() { return (ushort)(Cgb.TryEnableDoubleSpeed() ? 1 : 0); }";
        var (result, gb) = Run(main, HardwareMode.Dmg);

        await Assert.That(result).IsEqualTo((ushort)0); // IsColor() is false, so it bails out early
        await Assert.That(gb.KeyOne.DoubleSpeed).IsFalse(); // no speed state to flip on DMG
        await Assert.That(gb.Cpu.Stopped).IsFalse(); // and STOP never actually parked the CPU
    }

    [Test]
    public async Task SelectVramBank_SelectsBank1_InCgbMode()
    {
        const string main = "static byte Main() { Cgb.SelectVramBank(1); return 0; }";
        var (_, gb) = Run(main, HardwareMode.Cgb);

        await Assert.That(gb.Mmu.Banking.VramBank).IsEqualTo((byte)1);
        await Assert.That(gb.DebugReadByte(0xFF4F) & 1).IsEqualTo(1); // VBK reads back the selected bank
    }

    [Test]
    public async Task SelectVramBank_IsInertOnDmg()
    {
        const string main = "static byte Main() { Cgb.SelectVramBank(1); return 0; }";
        var (_, gb) = Run(main, HardwareMode.Dmg);

        await Assert.That(gb.Mmu.Banking.VramBank).IsEqualTo((byte)0); // IsColor() false -> no VBK write
    }

    [Test]
    public async Task SetBackgroundColor_WritesPaletteRam_InCgbMode()
    {
        // Palette 0, color 0 -> index 0/1; rgb555 $1234 -> low byte $34, high byte $12.
        const string main =
            "static byte Main() { Cgb.SetBackgroundColor(0, 0, 0x1234); return 0; }";
        var (_, gb) = Run(main, HardwareMode.Cgb);

        gb.Ppu.BgPalette.IndexRegister = 0;
        var low = gb.Ppu.BgPalette.ReadData();
        gb.Ppu.BgPalette.IndexRegister = 1;
        var high = gb.Ppu.BgPalette.ReadData();

        await Assert.That(low).IsEqualTo((byte)0x34);
        await Assert.That(high).IsEqualTo((byte)0x12);
    }

    [Test]
    public async Task SetBackgroundColor_WritesTheRequestedPaletteAndColorSlot()
    {
        // Palette 2, color 3 -> index (2*8 + 3*2) = 22/23; rgb555 $7FFF (white-ish) -> low $FF, high $7F.
        const string main =
            "static byte Main() { Cgb.SetBackgroundColor(2, 3, 0x7FFF); return 0; }";
        var (_, gb) = Run(main, HardwareMode.Cgb);

        gb.Ppu.BgPalette.IndexRegister = 22;
        var low = gb.Ppu.BgPalette.ReadData();
        gb.Ppu.BgPalette.IndexRegister = 23;
        var high = gb.Ppu.BgPalette.ReadData();

        await Assert.That(low).IsEqualTo((byte)0xFF);
        await Assert.That(high).IsEqualTo((byte)0x7F);
    }
}
