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
/// Enabler E3 of the ideal-game-API program (M6): STORED delegates. A delegate that crosses a
/// provenance-erasing boundary (constructor/method argument, instance/static field, return) is
/// materialized into a 3-byte arena blob <c>[u8 targetId][u16 env]</c>
/// (<c>CilMethodLowerer.MaterializeDelegateIfNeeded</c>), and an untraceable <c>Invoke</c>
/// dispatches over the closed-world <c>CilDelegateRegistry</c> — a switch of DIRECT calls, no
/// indirect-call backend. The traceable fast path (LINQ lambdas, same-method invokes) is untouched.
/// The JRPG north star's <c>DialogueScene(lines, onClosed)</c> close-callback is the motivating
/// shape. Fixtures verify on the emulator, Debug and Release, via the register-verdict pattern.
/// </summary>
public class CilStoredDelegateTests
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
        "koh-cil-stored-delegate-tests"
    );

    private static GameBoySystem Compile(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilStoredDelegateAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_sdel_{Guid.NewGuid():N}.dll");
        var emitResult = compilation.Emit(path);
        if (!emitResult.Success)
            throw new InvalidOperationException(
                "Roslyn compile failed:\n"
                    + string.Join("\n", emitResult.Diagnostics.Select(d => d.ToString()))
            );

        var diagnostics = new DiagnosticBag();
        var input = CompilerInput.FromAssembly(
            path,
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

    private static void Run(GameBoySystem gb, int stepBudget = 2_000_000)
    {
        for (int steps = 0; steps < stepBudget; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < 0x100 || pc >= 0x8000)
                return;
            gb.StepInstruction();
        }
        throw new InvalidOperationException("program did not finish within the step budget");
    }

    private static async Task AssertPasses(string source, OptimizationLevel level)
    {
        var gb = Compile(source, level);
        Run(gb);
        await Assert.That(gb.DebugReadByte(0xFF43)).IsEqualTo((byte)0xEE);
        await Assert.That(gb.DebugReadByte(0xFF42)).IsEqualTo((byte)1);
    }

    // ============================================================================================
    // Fixture 1: the JRPG shape — an Action through a ctor into a readonly field, invoked later
    // from another method; both a CAPTURING lambda (display-class env) and a non-capturing one,
    // and ONE field holding two different targets across two instances (the switch must pick the
    // right arm per blob, not per call site).
    // ============================================================================================

    private const string ActionFieldSource = """
        using System;
        using Koh.GameBoy;

        class Button
        {
            private readonly Action _onPress;

            public Button(Action onPress)
            {
                _onPress = onPress;
            }

            public void Press() => _onPress();
        }

        public class Program
        {
            static byte _a;
            static byte _b;

            public static void Main()
            {
                byte ok = 1;

                byte captured = 40;
                Button first = new Button(() => _a = (byte)(captured + 2)); // capturing lambda
                Button second = new Button(() => _b = 7);                    // non-capturing lambda

                first.Press();
                second.Press();
                first.Press(); // reinvocable — the blob survives multiple invokes

                if (_a != 42) ok = 0;
                if (_b != 7) ok = 0;

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task ActionThroughCtorField_TwoTargetsOneField_DispatchPerBlob(
        OptimizationLevel level
    ) => await AssertPasses(ActionFieldSource, level);

    // ============================================================================================
    // Fixture 2: a delegate WITH a return value through a boundary — the dispatch merges results
    // through the alloca slot; also a delegate passed as a plain method ARGUMENT (not a ctor) and
    // invoked inside the callee.
    // ============================================================================================

    private const string FuncArgSource = """
        using System;
        using Koh.GameBoy;

        public class Program
        {
            static int Apply(Func<int, int> f, int x) => f(x); // param boundary + invoke in callee

            public static void Main()
            {
                byte ok = 1;

                int bias = 5;
                if (Apply(v => v * 2, 21) != 42) ok = 0;        // non-capturing
                if (Apply(v => v + bias, 10) != 15) ok = 0;     // capturing
                if (Apply(v => v * 2, 3) != 6) ok = 0;          // same target again, fresh blob

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task FuncThroughParameter_ReturnValuesMergeThroughSlot(OptimizationLevel level) =>
        await AssertPasses(FuncArgSource, level);

    // ============================================================================================
    // Fixture 3: a STATIC delegate field reassigned between invokes — the stsfld boundary, and
    // proof the blob read happens per invoke (no stale target).
    // ============================================================================================

    private const string StaticFieldSource = """
        using System;
        using Koh.GameBoy;

        public class Program
        {
            static Action _handler;
            static byte _hits;

            public static void Main()
            {
                byte ok = 1;

                _handler = () => _hits += 1;
                _handler();
                _handler();
                if (_hits != 2) ok = 0;

                _handler = () => _hits += 10;   // reassign: later invokes take the new arm
                _handler();
                if (_hits != 12) ok = 0;

                Hardware.SCY = ok;
                Hardware.SCX = 0xEE;
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task StaticDelegateField_ReassignmentSwitchesTarget(OptimizationLevel level) =>
        await AssertPasses(StaticFieldSource, level);
}
