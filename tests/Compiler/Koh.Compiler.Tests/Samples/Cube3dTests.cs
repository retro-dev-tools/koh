using System.Collections.Immutable;
using Koh.Common;
using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends;
using Koh.Compiler.Frontends.Cil;
using Koh.Emulator;
using Koh.Emulator.Cartridge;
using Koh.Emulator.Ppu;
using Koh.Native;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using LinkerType = Koh.Native.NativeLinker;

namespace Koh.Compiler.Tests.Samples;

/// <summary>
/// Compiles the real <c>samples/csharp/gb-3d</c> demos - the shared renderer/entry point and each demo's own
/// <c>Surface.cs</c>, compiled by Roslyn to a real assembly referencing <c>Koh.GameBoy.dll</c> (whose Hal
/// framework - Cgb, Ppu, Lcd, ... - the demos call unqualified, exactly as the Koh SDK's <c>cil</c> build
/// path does; see <c>CilGame2048Tests</c>) - through the real pipeline (<see cref="CilFrontend"/> -&gt; IR
/// -&gt; SM83 backend -&gt; linker -&gt; ROM), and runs them on <see cref="GameBoySystem"/> for a bounded
/// number of frames. Regression guard: earlier coverage here compiled a synthetic inline Surface scaffold
/// that stood in for the real demo, so a break in the actual shipped files (or in how they combine with
/// shared/) could pass this suite while the samples themselves failed to build or render a
/// blank/garbage screen.
/// </summary>
public class Cube3dTests
{
    private static string Root() => TestSupport.TestRepo.Root;

    /// <summary>The real demo source, read as SEPARATE files the way the Koh SDK compiles it (each its
    /// own <c>&lt;Compile&gt;</c> item / Roslyn syntax tree, per-file <c>using</c> directives legal at
    /// each file's own top - see <see cref="CompileToAssembly"/>, mirroring <c>CilGame2048Tests</c>'s own
    /// multi-tree pattern): the shared renderer + entry point (Game.cs), then the demo's own files
    /// (Surface.cs, or - for double-buffered, since its graphics-library retrofit - its own local
    /// Game.cs/CubeRenderer.cs). A shared file is skipped when the demo directory has a same-named file
    /// of its own (mirrors CubeDoubleBuffered.csproj's <Compile Exclude="..."> for those exact files -
    /// see its comment), so a variant-local override doesn't get compiled twice alongside the shared
    /// definition it replaces. The framework Hal is NOT read here - it is already compiled into
    /// Koh.GameBoy.dll, referenced below, and CilFrontend lowers its IL on demand.</summary>
    private static IReadOnlyList<string> ReadDemo(string variant)
    {
        var root = Root();
        var shared = Path.Combine(root, "samples", "csharp", "gb-3d", "shared");
        var demo = Path.Combine(root, "samples", "csharp", "gb-3d", variant);

        var demoFileNames = Directory
            .GetFiles(demo, "*.cs")
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sources = new List<string>();
        foreach (
            var file in Directory
                .GetFiles(shared, "*.cs")
                .Where(f => !demoFileNames.Contains(Path.GetFileName(f)))
                .OrderBy(f => f, StringComparer.Ordinal)
        )
            sources.Add(File.ReadAllText(file));
        foreach (
            var file in Directory.GetFiles(demo, "*.cs").OrderBy(f => f, StringComparer.Ordinal)
        )
            sources.Add(File.ReadAllText(file));
        return sources;
    }

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
        "koh-cube3d-tests"
    );

    // The Koh SDK brings Koh.GameBoy into scope everywhere via a global <Using> (see Sdk.props); compiling
    // the demo files directly with Roslyn (no SDK) needs the same global using injected explicitly.
    private const string GlobalUsings = "global using Koh.GameBoy;\n";

    private static string CompileToAssembly(IReadOnlyList<string> sources)
    {
        var trees = sources
            .Append(GlobalUsings)
            .Select(s => CSharpSyntaxTree.ParseText(s))
            .ToArray();
        var compilation = CSharpCompilation.Create(
            "Cube3dAsm_" + Guid.NewGuid().ToString("N"),
            trees,
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                // Release IL, not the default Debug: the frame-budget checks below are calibrated
                // against measured real-ROM (Release-compiled) render+present cadence, and Debug IL runs
                // several times slower, which would blow through the bounded frame counts before any
                // content renders.
                optimizationLevel: OptimizationLevel.Release,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cube3d_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    private static CompilerInput InputFor(IReadOnlyList<string> sources)
    {
        var assemblyPath = CompileToAssembly(sources);
        return CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
    }

    private static byte[] Compile(IReadOnlyList<string> sources)
    {
        // Mirror Koh.Build.Tasks.CompileKohRom exactly (CompilerDriver.Compile, which optimizes by
        // default) rather than calling the frontend/backend directly - the real ROM build runs the IR
        // optimizer, and unoptimized code renders dramatically slower, throwing off frame-budget checks.
        var diagnostics = new DiagnosticBag();
        var model = CompilerDriver.Compile(
            new CilFrontend(),
            new Sm83Backend(),
            InputFor(sources),
            diagnostics
        );
        if (diagnostics.Any(d => d.Severity == Koh.Common.DiagnosticSeverity.Error))
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

    /// <summary>Legacy bounded ROM capture.</summary>
    private static byte[] Boot(byte[] rom, int frames, HardwareMode mode = HardwareMode.Dmg)
    {
        var gb = new GameBoySystem(CartridgeFactory.Load(rom), mode);
        Array.Clear(gb.Mmu.VramArray); // no boot ROM runs: stand in for its VRAM clear (both banks)
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

    /// <summary>Legacy structural capture checks.</summary>
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
        var module = new CilFrontend().Lower(InputFor(ReadDemo(variant)), diagnostics);
        new Sm83Backend().Compile(module, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == Koh.Common.DiagnosticSeverity.Error))
            .IsFalse();
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
    [Arguments("double-buffered", 200, HardwareMode.Cgb)]
    [Arguments("full-frame", 250, HardwareMode.Dmg)]
    public async Task Demo_RunOnHardwareRendersARealCube(
        string variant,
        int frames,
        HardwareMode mode
    )
    {
        // Native replacement: rust/koh-compile/tests/cubes.rs.
        var rgb = Boot(Compile(ReadDemo(variant)), frames, mode);
        var (distinctShades, litPixels) = Analyze(rgb);

        await Assert.That(distinctShades).IsGreaterThanOrEqualTo(2); // dithered cube faces
        await Assert.That(litPixels).IsGreaterThanOrEqualTo(50); // not a handful of stray pixels
        await Assert
            .That(litPixels)
            .IsLessThanOrEqualTo((int)(Framebuffer.Width * Framebuffer.Height * 0.85)); // not full-screen noise
    }
}
