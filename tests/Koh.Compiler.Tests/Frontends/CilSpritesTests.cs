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
/// Graphics library WAVE 3 / module B: the shadow-OAM <c>Sprites</c> module
/// (<c>docs/superpowers/specs/2026-07-15-graphics-library-design.md</c>, build plan slice 8) — the
/// biggest gap the design doc calls out ("zero C# code touches Gb.Oam today") — plus its wiring into
/// <c>Video.EndFrame</c>'s flush stub. Proves each against a REAL compiled assembly -&gt; CilFrontend -&gt;
/// IrVerifier -&gt; Sm83Backend -&gt; Linker -&gt; GameBoySystem pipeline. Deliberately keeps its own
/// compile-to-assembly harness rather than depending on another test class's internals, mirroring
/// <see cref="CilGraphicsSlice2Tests"/>'s own stated rationale for doing the same.
///
/// This is also where the struct-instance-method CIL-frontend fix (<c>CilLoweringContext.EnsureSignature</c>
/// / <c>CilMethodLowerer.Run</c> routing a struct <c>this</c> through <c>CilTypeMapper.MapParam</c>
/// instead of the plain <c>Map</c>) gets its first real-module exercise: every fixture below calls a
/// <c>Sprite</c> INSTANCE method (<c>Set</c>/<c>Move</c>/<c>SetTile</c>/<c>SetAttr</c>/<c>Hide</c>), the
/// exact shape that fix unblocked.
/// </summary>
public class CilSpritesTests
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
        "koh-cil-sprites-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilSpritesAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_sprites_{Guid.NewGuid():N}.dll");
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

    // ---- Emulator harness (mirrors CilGraphicsSlice2Tests) --------------------------------------

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

    /// <summary>Allows both ROM and the HRAM OAM-DMA trampoline, per <c>CilGraphicsSlice2Tests.Run</c>'s
    /// own remarks — every fixture here reaches <c>Video.EndFrame</c> -&gt; <c>Sprites.Flush</c> -&gt;
    /// <c>Hardware.RunOamDma</c>, which legitimately visits HRAM (0xFF80+), not just ROM.</summary>
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

    // ---- Fixture 1: mutate several slots, EndFrame, OAM matches the shadow byte-for-byte -----------
    //
    // Slot 0: Set + SetAttr (ordinary position/tile/attr). Slot 1: negative-coordinate Set (clipping).
    // Slot 2: Set then Hide (Y forced to 0, X/Tile/Attr from the earlier Set survive). Slot 39: left
    // completely untouched, to prove Video.Init's Sprites.HideAll() actually zeroed the WHOLE shadow
    // (not just a vacuous "happened to start at 0") before the DMA copies all 160 bytes.
    private const string MutateSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Video.Start();

                Sprite a;
                Sprites.Get(0, out a);
                a.Set(16, 32, 5);
                a.SetAttr(ObjAttr.FlipX);

                Sprite b;
                Sprites.Get(1, out b);
                b.Set(-4, -20, 9);

                Sprite c;
                Sprites.Get(2, out c);
                c.Set(50, 50, 3);
                c.Hide();

                Video.EndFrame();
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task EndFrame_FlushesShadowToOam_MatchingSetMoveHideAndClipping(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(MutateSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(MutateSource, level), out int start);
        Run(gb, start);

        // Slot 0: Set(16, 32, 5) -> Y=32+16=48, X=16+8=24, Tile=5; SetAttr(FlipX=0x20).
        await Assert.That(gb.DebugReadByte(0xFE00)).IsEqualTo((byte)48); // Y
        await Assert.That(gb.DebugReadByte(0xFE01)).IsEqualTo((byte)24); // X
        await Assert.That(gb.DebugReadByte(0xFE02)).IsEqualTo((byte)5); // Tile
        await Assert.That(gb.DebugReadByte(0xFE03)).IsEqualTo((byte)0x20); // Attr

        // Slot 1: Set(-4, -20, 9) -> Y=(-20+16) as byte = 252, X=(-4+8) as byte = 4, Tile=9,
        // Attr untouched (0, from Video.Init's shadow clear).
        await Assert.That(gb.DebugReadByte(0xFE04)).IsEqualTo((byte)252); // Y (clipped off the top)
        await Assert.That(gb.DebugReadByte(0xFE05)).IsEqualTo((byte)4); // X
        await Assert.That(gb.DebugReadByte(0xFE06)).IsEqualTo((byte)9); // Tile
        await Assert.That(gb.DebugReadByte(0xFE07)).IsEqualTo((byte)0); // Attr

        // Slot 2: Set(50, 50, 3) then Hide() -> Y forced to 0; X/Tile survive from Set.
        await Assert.That(gb.DebugReadByte(0xFE08)).IsEqualTo((byte)0); // Y (hidden)
        await Assert.That(gb.DebugReadByte(0xFE09)).IsEqualTo((byte)58); // X = 50+8
        await Assert.That(gb.DebugReadByte(0xFE0A)).IsEqualTo((byte)3); // Tile
        await Assert.That(gb.DebugReadByte(0xFE0B)).IsEqualTo((byte)0); // Attr

        // Slot 39 (last of the 40, bytes 0xFE9C-0xFE9F): never touched -> still zero from HideAll.
        await Assert.That(gb.DebugReadByte(0xFE9C)).IsEqualTo((byte)0);
        await Assert.That(gb.DebugReadByte(0xFE9D)).IsEqualTo((byte)0);
        await Assert.That(gb.DebugReadByte(0xFE9E)).IsEqualTo((byte)0);
        await Assert.That(gb.DebugReadByte(0xFE9F)).IsEqualTo((byte)0);
    }

    // ---- Fixture 2: Sprites.HideAll alone hides every slot (Y == 0), independent of EndFrame -------
    private const string HideAllSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Video.Start();

                Sprite a;
                Sprites.Get(5, out a);
                a.Set(40, 60, 7);

                Sprites.HideAll();
                Video.EndFrame();
            }
        }
        """;

    [Test]
    public async Task HideAll_ForcesEveryShadowSlotYToZero()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(HideAllSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(HideAllSource, OptimizationLevel.Debug), out int start);
        Run(gb, start);

        // Slot 5's Y (byte 20) must be back to 0 even though it was set to a visible position, and
        // every other slot's Y (checked via a couple of samples across the range) stays 0 too.
        await Assert.That(gb.DebugReadByte(0xFE00 + 5 * 4)).IsEqualTo((byte)0); // slot 5 Y
        await Assert.That(gb.DebugReadByte(0xFE00)).IsEqualTo((byte)0); // slot 0 Y
        await Assert.That(gb.DebugReadByte(0xFE9C)).IsEqualTo((byte)0); // slot 39 Y
    }

    // ---- Fixture 3: a Move() after Set() changes only Y/X, never Tile/Attr -------------------------
    private const string MoveSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Video.Start();

                Sprite a;
                Sprites.Get(3, out a);
                a.Set(10, 10, 42);
                a.SetAttr(ObjAttr.Priority);
                a.Move(20, 30);

                Video.EndFrame();
            }
        }
        """;

    [Test]
    public async Task Move_ChangesPositionOnly_TileAndAttrSurvive()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(MoveSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(MoveSource, OptimizationLevel.Debug), out int start);
        Run(gb, start);

        const int baseAddr = 0xFE00 + 3 * 4;
        await Assert.That(gb.DebugReadByte(baseAddr)).IsEqualTo((byte)46); // Y = 30 + 16
        await Assert.That(gb.DebugReadByte(baseAddr + 1)).IsEqualTo((byte)28); // X = 20 + 8
        await Assert.That(gb.DebugReadByte(baseAddr + 2)).IsEqualTo((byte)42); // Tile, from Set
        await Assert.That(gb.DebugReadByte(baseAddr + 3)).IsEqualTo((byte)0x80); // Attr, from SetAttr
    }

    // ---- Fixture 4: EndFrame with no mutation since Init is a true no-op (dirty flag) --------------
    //
    // No sprite is ever touched: HideAll() (via Video.Init) already put the shadow AND real OAM in
    // agreement (Init clears real OAM directly too), so EndFrame's Flush should still run once
    // harmlessly (dirty starts true from HideAll) without crashing or leaving OAM non-zero.
    private const string NoMutationSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Video.Init();
                Video.Start();
                Video.EndFrame();
            }
        }
        """;

    [Test]
    public async Task EndFrame_WithNoSpriteMutation_LeavesOamAllZero()
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(NoMutationSource, OptimizationLevel.Debug, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var gb = Load(Compile(NoMutationSource, OptimizationLevel.Debug), out int start);
        Run(gb, start);

        await Assert.That(gb.DebugReadByte(0xFE00)).IsEqualTo((byte)0);
        await Assert.That(gb.DebugReadByte(0xFE9F)).IsEqualTo((byte)0);
    }
}
