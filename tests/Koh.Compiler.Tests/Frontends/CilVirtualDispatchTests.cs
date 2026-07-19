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
using Koh.Linker.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using KohDiagnosticSeverity = Koh.Core.Diagnostics.DiagnosticSeverity;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests.Frontends;

/// <summary>
/// Milestone M3 of the ideal-game-API program
/// (<c>docs/superpowers/specs/2026-07-19-ideal-game-api-design.md</c>): class inheritance and
/// closed-world virtual dispatch. Two halves, both proven on the emulator (Debug and Release):
///
/// 1. PREFIX FIELD LAYOUT — before this milestone, <c>CilClassLayout</c> laid a derived class's
///    fields from offset 0, silently OVERLAPPING its base's fields (a latent correctness bug for
///    any inheritance at all). Derived fields now start after the base layout, so base-typed and
///    derived-typed access agree.
///
/// 2. TAG DISPATCH — a genuinely virtual call (receiver not traceable to one concrete type) lowers
///    to a runtime-type-tag load (offset 0, stored at <c>newobj</c>) plus a jump-table
///    <c>switch</c> of DIRECT calls (<c>CilVirtualDispatch</c> / <c>TryEmitTagDispatch</c>) —
///    which is what makes the framework's <c>Scene</c>/<c>Game.Run</c> shape compile.
///
/// The scene fixture uses the phase-marker protocol (program signals SCX, harness observes;
/// stepping is harness-controlled) because <c>Game.Run</c> never returns by design.
/// </summary>
public class CilVirtualDispatchTests
{
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
        "koh-cil-virtual-dispatch-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilVDispatchAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_vdisp_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );
        return path;
    }

    private static GameBoySystem Compile(string source, OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var assemblyPath = CompileToAssembly(source, level);
        var input = CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        var module = new CilFrontend().Lower(input, diagnostics);
        if (diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            throw new InvalidOperationException(
                "frontend reported errors:\n  "
                    + string.Join("\n  ", diagnostics.Select(d => d.Message))
            );
        var errors = IrVerifier.Verify(module);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                "IR verification failed:\n  " + string.Join("\n  ", errors)
            );
        IrOptimizer.Optimize(module);
        var model = new Sm83Backend().Compile(module, new DiagnosticBag());
        var link = new LinkerType().Link([new LinkerInput("cil", model)]);
        var rom =
            link.RomData
            ?? throw new InvalidOperationException(
                "no ROM; linker diagnostics:\n  "
                    + string.Join("\n  ", link.Diagnostics.Select(d => d.Message))
            );
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = 0x100;
        return gb;
    }

    private static void Run(GameBoySystem gb, int stepBudget = 4_000_000)
    {
        for (int steps = 0; steps < stepBudget; steps++)
        {
            int pc = gb.Registers.Pc;
            bool inRom = pc >= 0x100 && pc < 0x8000;
            bool inHram = pc >= Sm83Backend.OamDmaTrampoline && pc <= 0xFFFF;
            if (!inRom && !inHram)
                return;
            gb.StepInstruction();
        }
        throw new InvalidOperationException("program did not finish within the step budget");
    }

    /// <summary>Step until SCX equals <paramref name="phase"/> — for fixtures whose main loop
    /// (e.g. <c>Game.Run</c>) never exits.</summary>
    private static void RunUntilPhase(GameBoySystem gb, byte phase, int stepBudget = 8_000_000)
    {
        for (int steps = 0; steps < stepBudget; steps++)
        {
            if (gb.DebugReadByte(0xFF43) == phase)
                return;
            int pc = gb.Registers.Pc;
            bool inRom = pc >= 0x100 && pc < 0x8000;
            bool inHram = pc >= Sm83Backend.OamDmaTrampoline && pc <= 0xFFFF;
            if (!inRom && !inHram)
                throw new InvalidOperationException($"program ended before phase {phase}");
            gb.StepInstruction();
        }
        throw new InvalidOperationException($"phase {phase} not reached within the step budget");
    }

    private static async Task AssertPasses(string source, OptimizationLevel level)
    {
        var gb = Compile(source, level);
        Run(gb);
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)0xEE);
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)1);
    }

    // ============================================================================================
    // Fixture 1: prefix layout — base and derived fields coexist; base-typed access to a derived
    // instance reads/writes the same bytes derived-typed access does.
    // ============================================================================================

    private const string LayoutSource = """
        using Koh.GameBoy;

        class Enemy
        {
            public byte Hp;
            public ushort Score;
        }

        class Boss : Enemy
        {
            public byte Phase;
            public ushort Armor;
        }

        public class Program
        {
            public static void Main()
            {
                byte ok = 1;

                Boss boss = new Boss();
                boss.Hp = 42;          // base field through derived receiver
                boss.Score = 5000;
                boss.Phase = 3;        // derived fields must NOT overlap the base's
                boss.Armor = 700;

                if (boss.Hp != 42) ok = 0;
                if (boss.Score != 5000) ok = 0;
                if (boss.Phase != 3) ok = 0;
                if (boss.Armor != 700) ok = 0;

                Enemy asBase = boss;   // base-typed view of the same instance
                if (asBase.Hp != 42) ok = 0;
                if (asBase.Score != 5000) ok = 0;
                asBase.Hp = 77;        // write through the base view, read back through derived
                if (boss.Hp != 77) ok = 0;
                if (boss.Phase != 3) ok = 0;   // and the derived fields survived the base write

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task InheritedFields_UsePrefixLayout_NoOverlap(OptimizationLevel level) =>
        await AssertPasses(LayoutSource, level);

    // ============================================================================================
    // Fixture 2: tag dispatch through a base-typed STATIC field (reloading from a static loses
    // concrete-type tracking, forcing the genuine dispatch path): three subclasses, an override
    // with parameter + return value, an inherited base implementation, and dispatch inside a loop
    // over reassigned receivers.
    // ============================================================================================

    private const string DispatchSource = """
        using Koh.GameBoy;

        abstract class Shape
        {
            public byte Id;

            public abstract int Area(int scale);

            public virtual byte Kind() => 0;   // base implementation some subclasses inherit
        }

        class Square : Shape
        {
            public int Side;

            public override int Area(int scale) => Side * Side * scale;

            public override byte Kind() => 1;
        }

        class Rect : Shape
        {
            public int W, H;

            public override int Area(int scale) => W * H * scale;
            // Kind() inherited from Shape
        }

        class Tri : Shape
        {
            public int B, H;

            public override int Area(int scale) => B * H * scale / 2;

            public override byte Kind() => 3;
        }

        public class Program
        {
            static Shape _current;   // static field: reloads carry NO concrete-type provenance

            public static void Main()
            {
                byte ok = 1;

                Square sq = new Square();
                sq.Side = 5;
                Rect re = new Rect();
                re.W = 3;
                re.H = 4;
                Tri tr = new Tri();
                tr.B = 6;
                tr.H = 7;

                _current = sq;
                if (_current.Area(2) != 50) ok = 0;    // 5*5*2 — dispatched to Square.Area
                if (_current.Kind() != 1) ok = 0;

                _current = re;
                if (_current.Area(1) != 12) ok = 0;    // 3*4 — dispatched to Rect.Area
                if (_current.Kind() != 0) ok = 0;      // inherited Shape.Kind

                _current = tr;
                if (_current.Area(2) != 42) ok = 0;    // 6*7*2/2 — dispatched to Tri.Area
                if (_current.Kind() != 3) ok = 0;

                // Dispatch in a loop over reassigned receivers accumulates across all three.
                int total = 0;
                for (byte i = 0; i < 3; i++)
                {
                    if (i == 0) _current = sq;
                    else if (i == 1) _current = re;
                    else _current = tr;
                    total += _current.Area(1);
                }
                if (total != 25 + 12 + 21) ok = 0;

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task VirtualCalls_TagDispatchThroughBaseTypedStatic(OptimizationLevel level) =>
        await AssertPasses(DispatchSource, level);

    // ============================================================================================
    // Fixture 3: the payoff — the framework's Scene/Game.Run shape, driven to a third scene with
    // ctor-carried state, asserting the DEFERRED commit (Exit/Enter run inside EndFrame, not at
    // the ChangeScene call) via in-program ordering checks. Game.Run never returns; the harness
    // steps to the final phase marker and stops.
    // ============================================================================================

    private const string SceneSource = """
        using Koh.GameBoy;
        using Koh.GameBoy.Framework;

        class TitleScene : Scene
        {
            public override void Enter() => Hardware.SCY = 10;

            public override void Update()
            {
                if (Clock.Frames >= 2)
                {
                    Game.ChangeScene(new PlayScene(0x4D2));
                    // Deferred commit: our own Exit must NOT have run yet on this line.
                    if (Hardware.WY == 0)
                        Hardware.OBP1 = 1;
                }
            }

            public override void Exit() => Hardware.WY = 20;
        }

        class PlayScene : Scene
        {
            private readonly ushort _code;

            public PlayScene(ushort code)
            {
                _code = code;
            }

            public override void Enter()
            {
                Hardware.OBP0 = (byte)(_code >> 8);   // 0x04
                Hardware.LYC = (byte)_code;           // 0xD2
                Hardware.SCX = 0xEE;                  // final phase marker for the harness
            }

            public override void Update() { }
        }

        public class Program
        {
            public static void Main() => Game.Run(new TitleScene());
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task SceneMachine_GameRun_DeferredChangeWithCtorState(OptimizationLevel level)
    {
        var gb = Compile(SceneSource, level);
        RunUntilPhase(gb, 0xEE);
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)10); // Title entered (SCY)
        await Assert.That(gb.DebugReadByte(0xFF49)).IsEqualTo((byte)1); // commit was deferred (OBP1)
        await Assert.That(gb.DebugReadByte(0xFF4A)).IsEqualTo((byte)20); // Title.Exit ran (WY)
        await Assert.That(gb.DebugReadByte(0xFF48)).IsEqualTo((byte)0x04); // ctor state hi (OBP0)
        await Assert.That(gb.DebugReadByte(0xFF45)).IsEqualTo((byte)0xD2); // ctor state lo (LYC)
    }

    // ============================================================================================
    // Fixture 4: coexistence — an [Interrupt("VBlank")] handler (an unconditional call-graph root)
    // compiled alongside tag dispatch; the handler counts frames while dispatch runs in the
    // mainline, and both observable results must hold.
    // ============================================================================================

    private const string InterruptSource = """
        using Koh.GameBoy;

        abstract class Counter
        {
            public abstract byte Step(byte x);
        }

        class Doubler : Counter
        {
            public override byte Step(byte x) => (byte)(x * 2);
        }

        class Tripler : Counter
        {
            public override byte Step(byte x) => (byte)(x * 3);
        }

        public class Program
        {
            static Counter _c;
            static byte _vblanks;

            [Interrupt("VBlank")]
            static void OnVBlank()
            {
                _vblanks++;
            }

            public static void Main()
            {
                byte ok = 1;

                _c = new Doubler();
                if (_c.Step(7) != 14) ok = 0;
                _c = new Tripler();
                if (_c.Step(7) != 21) ok = 0;

                Hardware.IE = 0x01;            // VBlank
                Hardware.EnableInterrupts();
                while (_vblanks < 2) { }       // the real PPU fires it

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task TagDispatch_CoexistsWithInterruptHandlerRoots(OptimizationLevel level)
    {
        // Frame-driven (not instruction-stepped): the handler executes at the 0x40 vector, below
        // the step-harness's ROM window, and RunFrame is how the sibling CilInterruptTests drive
        // the real PPU's VBlank raise.
        var gb = Compile(InterruptSource, level);
        for (int frame = 0; frame < 600 && gb.DebugReadByte(0xFF43) != 0xEE; frame++)
            gb.RunFrame();
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)0xEE);
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)1);
    }
}
