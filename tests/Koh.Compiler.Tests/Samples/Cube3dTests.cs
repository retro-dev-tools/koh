using System.Text;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends.CSharp;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Emulator.Core.Ppu;
using Koh.Linker.Core;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Samples;

/// <summary>
/// Compiles the real <c>samples/gb-3d</c> demos - the framework HAL, the shared renderer/entry point, and
/// each demo's own <c>Surface.cs</c> - through the real pipeline (Koh C# frontend -> IR -> SM83 backend ->
/// linker -> ROM), the same way <see cref="Game2048Tests"/> exercises gb-2048-cs, and runs them on
/// <see cref="GameBoySystem"/> for a bounded number of frames. Regression guard: earlier coverage here
/// compiled a synthetic inline Surface scaffold that stood in for the real demo, so a break in the actual
/// shipped files (or in how they combine with shared/) could pass this suite while the samples themselves
/// failed to build or render a blank/garbage screen.
/// </summary>
public class Cube3dTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Koh.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    /// <summary>The real demo source, concatenated the way the Koh SDK compiles it: the framework HAL,
    /// then the shared renderer + entry point (Game.cs), then the demo's own Surface.cs.</summary>
    private static string ReadDemo(string variant)
    {
        var root = Root();
        var frameworkHal = Path.Combine(root, "src", "Koh.GameBoy", "Hal");
        var shared = Path.Combine(root, "samples", "gb-3d", "shared");
        var demo = Path.Combine(root, "samples", "gb-3d", variant);

        var sb = new StringBuilder();
        foreach (var dir in new[] { frameworkHal, shared, demo })
        foreach (
            var file in Directory.GetFiles(dir, "*.cs").OrderBy(f => f, StringComparer.Ordinal)
        )
            sb.Append(File.ReadAllText(file)).Append('\n');
        return sb.ToString();
    }

    private static byte[] Compile(string source)
    {
        // Mirror Koh.Build.Tasks.CompileKohRom exactly (CompilerDriver.Compile, which optimizes by
        // default) rather than calling the frontend/backend directly - the real ROM build runs the IR
        // optimizer, and unoptimized code renders dramatically slower, throwing off frame-budget checks.
        var diagnostics = new DiagnosticBag();
        var model = CompilerDriver.Compile(
            new CSharpFrontend(),
            new Sm83Backend(),
            SourceText.From(source, "cube.cs"),
            diagnostics
        );
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException(
                string.Join("; ", diagnostics.Select(d => d.Message))
            );
        var link = new LinkerType().Link([new LinkerInput("cube", model)]);
        if (!link.Success || link.RomData is null)
            throw new InvalidOperationException(
                string.Join("; ", link.Diagnostics.Select(d => d.Message))
            );
        return link.RomData;
    }

    /// <summary>Boot a compiled ROM and run it for a bounded number of hardware frames, returning the RGB
    /// framebuffer at the end (mirrors samples/gb-3d/verify/Cube3dVerify's capture, simplified).</summary>
    private static byte[] Boot(byte[] rom, int frames)
    {
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Pc = 0x100; // boot: NOP; JP entry
        gb.Registers.Sp = 0xFFFE;
        for (var i = 0; i < frames; i++)
            gb.RunFrame();

        var fb = gb.Framebuffer.FrontArray;
        var rgb = new byte[Framebuffer.Width * Framebuffer.Height * 3];
        for (var p = 0; p < Framebuffer.Width * Framebuffer.Height; p++)
        {
            rgb[p * 3 + 0] = fb[p * 4 + 0];
            rgb[p * 3 + 1] = fb[p * 4 + 1];
            rgb[p * 3 + 2] = fb[p * 4 + 2];
        }
        return rgb;
    }

    /// <summary>Simplified structural check (see Cube3dVerify.CubeFrameChecks for the fuller version this
    /// mirrors): the corner pixel is background, and at least two distinct non-background shades exist
    /// within a lit region that neither is a handful of stray pixels nor covers the whole screen. That is
    /// a shape a blank or garbage framebuffer cannot satisfy by accident.</summary>
    private static (int distinctShades, int litPixels) Analyze(byte[] rgb)
    {
        int Color(int x, int y)
        {
            var i = (y * Framebuffer.Width + x) * 3;
            return (rgb[i] << 16) | (rgb[i + 1] << 8) | rgb[i + 2];
        }

        var background = Color(0, 0);
        var shades = new HashSet<int>();
        var lit = 0;
        for (var y = 0; y < Framebuffer.Height; y++)
        for (var x = 0; x < Framebuffer.Width; x++)
        {
            var c = Color(x, y);
            if (c == background)
                continue;
            shades.Add(c);
            lit++;
        }
        return (shades.Count, lit);
    }

    // ---- The real demos compile without diagnostics and link to a bootable ROM ----------------------

    [Test]
    [Arguments("double-buffered")]
    [Arguments("full-frame")]
    public async Task Demo_CompilesWithoutDiagnostics(string variant)
    {
        var diagnostics = new DiagnosticBag();
        var module = new CSharpFrontend().Lower(
            SourceText.From(ReadDemo(variant), "cube.cs"),
            diagnostics
        );
        new Sm83Backend().Compile(module, diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsFalse();
    }

    [Test]
    [Arguments("double-buffered")]
    [Arguments("full-frame")]
    public async Task Demo_LinksToBootableRom(string variant)
    {
        var rom = Compile(ReadDemo(variant));

        // A DMG cartridge boots through 0x0100 (nop; jp entry) and the boot ROM verifies the logo.
        await Assert.That(rom[0x100]).IsEqualTo((byte)0x00); // NOP
        await Assert.That(rom[0x101]).IsEqualTo((byte)0xC3); // JP a16
        await Assert.That(rom[0x104]).IsEqualTo((byte)0xCE); // first byte of the Nintendo logo
        await Assert.That(rom[0x105]).IsEqualTo((byte)0xED);
    }

    // ---- Running the real demo renders an actual, non-trivial cube -----------------------------------

    [Test]
    [Arguments("double-buffered")]
    [Arguments("full-frame")]
    public async Task Demo_RunOnHardwareRendersARealCube(string variant)
    {
        // The software rasterizer is slow relative to hardware vblank: from a cold boot, the first full
        // render+present pass (tileset setup plus one whole-cube transform/sort/rasterize) does not land
        // until frame ~200 for either demo, measured directly. The budget below leaves clear margin.
        var rgb = Boot(Compile(ReadDemo(variant)), frames: 300);
        var (distinctShades, litPixels) = Analyze(rgb);

        await Assert.That(distinctShades).IsGreaterThanOrEqualTo(2); // dithered cube faces
        await Assert.That(litPixels).IsGreaterThanOrEqualTo(50); // not a handful of stray pixels
        await Assert
            .That(litPixels)
            .IsLessThanOrEqualTo((int)(Framebuffer.Width * Framebuffer.Height * 0.85)); // not full-screen noise
    }
}
