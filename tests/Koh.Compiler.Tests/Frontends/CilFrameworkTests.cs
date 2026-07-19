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
using Koh.Linker.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using KohDiagnosticSeverity = Koh.Core.Diagnostics.DiagnosticSeverity;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Frontends;

/// <summary>
/// Milestone M1 of the ideal-game-API program
/// (<c>docs/superpowers/specs/2026-07-19-ideal-game-api-design.md</c>): the Framework Stage-0
/// modules — <c>Input</c> (frame-latched edges/repeat), <c>Rng</c> (xorshift16), <c>Clock</c>/
/// <c>Timer</c>, <c>TileAsset</c>/<c>MapAsset</c>, and <c>Game.Boot</c>/<c>EndFrame</c> — all
/// ordinary referenced-assembly code lowered on demand, each proven on the emulator.
///
/// Fixtures verify INSIDE the compiled program and cross a verdict out through registers (SCY
/// verdict, SCX completion/phase marker — never a literal WRAM scratch address, which would collide
/// with the backend's static frames). The Input fixture additionally uses SCX as a PHASE marker:
/// the harness steps until the program signals a phase, toggles the scripted joypad, and resumes —
/// stepping is wholly harness-controlled, so this is race-free and deterministic.
///
/// The Rng fixture asserts emulator/desktop PARITY the strong way: the expected bytes are computed
/// by calling the real <c>Koh.GameBoy.Framework.Rng</c> in-process (the desktop reference build)
/// and interpolated into the fixture source the emulator then runs.
/// </summary>
public class CilFrameworkTests
{
    // ---- Roslyn: compile real C# to a real assembly on disk, referencing Koh.GameBoy -----------

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
        "koh-cil-framework-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilFrameworkAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable,
                allowUnsafe: true // the TileAsset fixture reads VRAM back through Gb.TileData
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_framework_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

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

    // ---- Emulator harness (mirrors CilVideoJoypadTests: HRAM DMA trampoline allowed) -----------

    private static GameBoySystem Load(EmitModel model, out int start)
    {
        var link = new LinkerType().Link([new LinkerInput("cil", model)]);
        var rom =
            link.RomData
            ?? throw new InvalidOperationException(
                "no ROM; linker diagnostics:\n  "
                    + string.Join("\n  ", link.Diagnostics.Select(d => d.Message))
            );
        start = 0x100;
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start;
        return gb;
    }

    /// <summary>Run to completion. PC may legitimately visit the HRAM OAM-DMA trampoline
    /// (<c>Video.EndFrame</c> fires OAM DMA when the shadow is dirty — and <c>Video.Init</c> always
    /// dirties it), so only "outside ROM AND outside HRAM" means done — same allowance as
    /// <c>CilVideoJoypadTests.Run</c>.</summary>
    private static void Run(GameBoySystem gb, int start, int stepBudget = 4_000_000)
    {
        for (int steps = 0; steps < stepBudget; steps++)
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

    /// <summary>Step until the program writes <paramref name="phase"/> to SCX (the phase-marker
    /// protocol of the Input fixture) — the harness then toggles the scripted pad and resumes.</summary>
    private static void RunUntilPhase(GameBoySystem gb, int start, byte phase)
    {
        for (int steps = 0; steps < 4_000_000; steps++)
        {
            if (gb.DebugReadByte(0xFF43) == phase)
                return;
            int pc = gb.Registers.Pc;
            bool inRom = pc >= start && pc < 0x8000;
            bool inHram = pc >= Sm83Backend.OamDmaTrampoline && pc <= 0xFFFF;
            if (!inRom && !inHram)
                throw new InvalidOperationException($"program ended before reaching phase {phase}");
            gb.StepInstruction();
        }
        throw new InvalidOperationException($"phase {phase} not reached within the step budget");
    }

    // ============================================================================================
    // Fixture 1: Rng — emulator/desktop parity plus derived-API determinism.
    // ============================================================================================

    // Built ONCE, thread-safely: the desktop Rng is process-global static state, and TUnit runs the
    // Debug and Release invocations of the test in PARALLEL — two concurrent oracle computations
    // interleave their draws and bake garbage expectations into the fixture (observed as a
    // "Debug-only" failure that was really a scheduling coin-flip; the emulator's values were
    // correct the whole time). Lazy<T> serializes the oracle; after it resolves, no test-time code
    // touches the desktop Rng again.
    private static readonly Lazy<string> RngFixtureSource = new(BuildRngSource);

    private static string BuildRngSource()
    {
        // The desktop reference build IS the oracle: run the real Rng here, in-process, and bake
        // the expected sequence into the fixture the emulator runs.
        Koh.GameBoy.Framework.Rng.Seed(0x1234);
        var expected = new byte[8];
        for (int i = 0; i < expected.Length; i++)
            expected[i] = Koh.GameBoy.Framework.Rng.Next();

        Koh.GameBoy.Framework.Rng.Seed(0x1234);
        for (int i = 0; i < 3; i++)
            Koh.GameBoy.Framework.Rng.Next();
        byte expectedMod = Koh.GameBoy.Framework.Rng.Next(10);

        return $$"""
            using Koh.GameBoy;
            using Koh.GameBoy.Framework;

            public class Program
            {
                public static void Main()
                {
                    byte ok = 1;

                    Rng.Seed(0x1234);
                    if (Rng.Next() != {{expected[0]}}) ok = 0;
                    if (Rng.Next() != {{expected[1]}}) ok = 0;
                    if (Rng.Next() != {{expected[2]}}) ok = 0;
                    if (Rng.Next() != {{expected[3]}}) ok = 0;
                    if (Rng.Next() != {{expected[4]}}) ok = 0;
                    if (Rng.Next() != {{expected[5]}}) ok = 0;
                    if (Rng.Next() != {{expected[6]}}) ok = 0;
                    if (Rng.Next() != {{expected[7]}}) ok = 0;

                    // Reseeding restarts the identical sequence; Next(max) stays in range and
                    // matches the desktop-computed draw at the same position.
                    Rng.Seed(0x1234);
                    if (Rng.Next() != {{expected[0]}}) ok = 0;
                    Rng.Next();
                    Rng.Next();
                    byte mod = Rng.Next(10);
                    if (mod != {{expectedMod}}) ok = 0;
                    if (mod >= 10) ok = 0;

                    // Seed(0) is coerced, never a stuck-at-zero generator.
                    Rng.Seed(0);
                    if (Rng.Next16() == 0) ok = 0;

                    // Mix perturbs the stream: same seed, mixed vs unmixed diverge.
                    Rng.Seed(0x1234);
                    Rng.Mix(0x5A);
                    if (Rng.Next() == {{expected[0]}}) ok = 0;

                    Hardware.SCY = ok;
                    Hardware.SCX = 0xEE; // completion marker
                }
            }
            """;
    }

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task Rng_MatchesDesktopSequenceAndDerivedDraws(OptimizationLevel level)
    {
        var source = RngFixtureSource.Value;
        var gb = Load(Compile(source, level), out int start);
        Run(gb, start);
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)0xEE);
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)1);
    }

    // ============================================================================================
    // Fixture 2: Timer — countdown semantics, fire-once edge, restart, Stop.
    // ============================================================================================

    private const string TimerSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Framework;

        public class Program
        {
            private static Timer _timer; // a Framework struct as a static field, like real games use

            public static void Main()
            {
                byte ok = 1;

                if (_timer.Running) ok = 0;      // zero-init = idle
                if (_timer.Tick()) ok = 0;       // idle ticks never fire

                _timer.Start(3);
                if (!_timer.Running) ok = 0;
                if (_timer.Tick()) ok = 0;       // 3 -> 2
                if (_timer.Tick()) ok = 0;       // 2 -> 1
                if (!_timer.Tick()) ok = 0;      // 1 -> 0: THE firing tick
                if (_timer.Running) ok = 0;
                if (_timer.Tick()) ok = 0;       // expired stays quiet

                _timer.Start(1);
                if (!_timer.Tick()) ok = 0;      // restart works; 1-frame timer fires immediately

                _timer.Start(100);
                _timer.Stop();
                if (_timer.Running) ok = 0;
                if (_timer.Tick()) ok = 0;

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task Timer_CountsDownFiresOnceAndRestarts(OptimizationLevel level)
    {
        var gb = Load(Compile(TimerSource, level), out int start);
        Run(gb, start);
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)0xEE);
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)1);
    }

    // ============================================================================================
    // Fixture 3: TileAsset/MapAsset as STATIC STRUCT FIELDS (the design's verify-first item) —
    // Define/Load land the art in VRAM, Tile() does the base math, MapAsset draws through Bg.
    // ============================================================================================

    private const string AssetSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Framework;
        using Koh.GameBoy.Graphics;

        public static class Assets
        {
            // Two distinguishable 2bpp tiles (16 bytes each).
            public static readonly byte[] Art =
            {
                0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
                0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0x0F, 0xF0,
                0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
                0xA8, 0xA9, 0xAA, 0xAB, 0xAC, 0xAD, 0xAE, 0xAF,
            };

            public static readonly byte[] Map = { 1, 2, 2, 1 }; // 2x2 rect of tile indices

            public static TileAsset Tiles;   // static field of a Framework struct
            public static MapAsset Layout;
        }

        public class Program
        {
            public static unsafe void Main()
            {
                Game.Boot(); // LCD off -> TileSet.Load takes the direct-copy path

                Assets.Tiles.Define(Assets.Art, 2);
                Assets.Tiles.Load(4); // land at VRAM slot 4, clear of anything Init touched

                byte ok = 1;
                if (Assets.Tiles.TileCount != 2) ok = 0;
                if (Assets.Tiles.BaseTile != 4) ok = 0;
                if (Assets.Tiles.Tile(0) != 4) ok = 0;
                if (Assets.Tiles.Tile(1) != 5) ok = 0;

                // The art must be byte-identical in VRAM at slot 4 (0x8000 + 4*16).
                byte* vram = Gb.TileData + 4 * 16;
                for (byte i = 0; i < 32; i++)
                    if (vram[i] != Assets.Art[i]) ok = 0;

                // MapAsset: draw the 2x2 rect at (3,5) through Bg's shadow, flush via EndFrame.
                Assets.Layout.Define(Assets.Map, 2, 2);
                if (Assets.Layout.Width != 2) ok = 0;
                if (Assets.Layout.Height != 2) ok = 0;
                Assets.Layout.Draw(3, 5);

                Video.Start();
                Game.EndFrame(); // flushes the tilemap shadow during vblank

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task Assets_StaticStructHandles_LoadVramAndDrawMap(OptimizationLevel level)
    {
        var gb = Load(Compile(AssetSource, level), out int start);
        Run(gb, start);
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)0xEE);
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)1);

        // The map rect arrived on the hardware tilemap: (3,5) origin, row stride 32.
        await Assert.That(gb.DebugReadByte(0x9800 + 5 * 32 + 3)).IsEqualTo((byte)1);
        await Assert.That(gb.DebugReadByte(0x9800 + 5 * 32 + 4)).IsEqualTo((byte)2);
        await Assert.That(gb.DebugReadByte(0x9800 + 6 * 32 + 3)).IsEqualTo((byte)2);
        await Assert.That(gb.DebugReadByte(0x9800 + 6 * 32 + 4)).IsEqualTo((byte)1);
    }

    // ============================================================================================
    // Fixture 4: Input — latched edges, consume-once fixed, release edges, and the repeat cadence,
    // driven by the phase-marker protocol (program signals SCX; harness toggles the pad; stepping
    // is harness-controlled so each phase boundary is exact).
    // ============================================================================================

    private const string InputSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Framework;

        public class Program
        {
            public static void Main()
            {
                byte ok = 1;

                // Phase 0 (harness holds Right from the start).
                Input.Update();
                if (!Input.Pressed(Button.Right)) ok = 0;   // rising edge
                if (!Input.Held(Button.Right)) ok = 0;
                if (!Input.Pressed(Button.Right)) ok = 0;   // SECOND query, same frame: still true
                if (Input.Released(Button.Right)) ok = 0;
                if (Input.DpadX() != 1) ok = 0;

                Input.Update();                              // still held
                if (Input.Pressed(Button.Right)) ok = 0;     // no new edge
                if (!Input.Held(Button.Right)) ok = 0;

                Hardware.SCX = 1;                            // harness: release Right
                Input.Update();
                if (!Input.Released(Button.Right)) ok = 0;   // falling edge
                if (Input.Held(Button.Right)) ok = 0;
                if (Input.DpadX() != 0) ok = 0;

                Hardware.SCX = 2;                            // harness: press Left
                Input.SetRepeat(3, 2);
                Input.Update();                              // frame of the edge
                if (!Input.Repeated(Button.Left)) ok = 0;    // edge counts as a repeat
                if (Input.DpadX() != -1) ok = 0;

                Input.Update();                              // +1
                if (Input.Repeated(Button.Left)) ok = 0;
                Input.Update();                              // +2
                if (Input.Repeated(Button.Left)) ok = 0;
                Input.Update();                              // +3 = delay reached: fire
                if (!Input.Repeated(Button.Left)) ok = 0;
                Input.Update();                              // +1 of interval
                if (Input.Repeated(Button.Left)) ok = 0;
                Input.Update();                              // +2 = interval reached: fire
                if (!Input.Repeated(Button.Left)) ok = 0;

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task Input_LatchedEdgesAndRepeatCadence(OptimizationLevel level)
    {
        var gb = Load(Compile(InputSource, level), out int start);

        gb.JoypadPress(JoypadButton.Right);
        RunUntilPhase(gb, start, 1);
        gb.JoypadRelease(JoypadButton.Right);
        RunUntilPhase(gb, start, 2);
        gb.JoypadPress(JoypadButton.Left);
        Run(gb, start);

        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)0xEE);
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)1);
    }

    // ============================================================================================
    // Fixture 5: Game.Boot/EndFrame — the frame bracketing ticks Clock and latches Input in one
    // call, and Boot leaves the machine authorable (LCD off) with a seeded Rng.
    // ============================================================================================

    private const string GameLoopSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Framework;
        using Koh.GameBoy.Graphics;

        public class Program
        {
            public static void Main()
            {
                Game.Boot();

                byte ok = 1;
                if (Clock.Frames != 0) ok = 0;
                if (Rng.Next16() == 0) ok = 0;  // seeded (any nonzero state draws nonzero)

                Video.Start();
                Game.EndFrame();
                Game.EndFrame();
                Game.EndFrame();

                if (Clock.Frames != 3) ok = 0;
                if (!Input.Held(Button.A)) ok = 0; // pad scripted before run; EndFrame latched it

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task GameBootAndEndFrame_TickClockAndLatchInput(OptimizationLevel level)
    {
        var gb = Load(Compile(GameLoopSource, level), out int start);
        gb.JoypadPress(JoypadButton.A);
        Run(gb, start);
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)0xEE);
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)1);
    }
}
