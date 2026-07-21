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
/// Delegates/closures task (see <c>docs/superpowers/specs/2026-07-14-cil-frontend-design.md</c>):
/// <c>ldftn</c>/<c>ldvirtftn</c>, <c>newobj</c> of a delegate type, and a <c>Delegate.Invoke</c> call
/// site resolved to its one statically-known target — following the cached-<c>&lt;&gt;9__</c>
/// static-field pattern (no-capture) and the direct <c>newobj</c>+<c>ldftn</c> pattern (capturing) the
/// design spike documented — plus <c>callvirt</c>-on-a-known-concrete-type devirtualization. Mirrors
/// <see cref="CilEndToEndTests"/>'s harness shape: real C# compiled by Roslyn to a real assembly, in
/// both Debug and Release IL, lowered by <see cref="CilFrontend"/>, verified, run on
/// <see cref="GameBoySystem"/>.
/// </summary>
public class CilDelegateTests
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
        "koh-cil-delegate-tests"
    );

    private static string CompileToAssembly(string source, OptimizationLevel level)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "CilDelegateAsm_" + Guid.NewGuid().ToString("N"),
            [tree],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: level,
                nullableContextOptions: NullableContextOptions.Disable
            )
        );
        Directory.CreateDirectory(ScratchDir);
        var path = Path.Combine(ScratchDir, $"cil_deleg_{Guid.NewGuid():N}.dll");
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
        IrOptimizer.Optimize(module); // CompilerDriver's default path (Mem2RegPass does SSA construction).
        return new Sm83Backend().Compile(module, new DiagnosticBag());
    }

    // ---- Emulator harness (mirrors CilEndToEndTests/CSharpEndToEndTests) ----------------------

    private static GameBoySystem Load(EmitModel model, out int start, out int length)
    {
        var link = new LinkerType().Link([new LinkerInput("cil", model)]);
        var rom = link.RomData ?? throw new InvalidOperationException("no ROM");
        start = 0x100;
        length = Sm83Backend.CodeBase + model.Sections[0].Data.Length - 0x100;
        var gb = new GameBoySystem(HardwareMode.Dmg, CartridgeFactory.Load(rom));
        gb.Registers.Sp = 0xFFFE;
        gb.Registers.Pc = (ushort)start;
        return gb;
    }

    private static void Run(GameBoySystem gb, int start, int length)
    {
        for (int steps = 0; steps < 200_000; steps++)
        {
            int pc = gb.Registers.Pc;
            if (pc < start || pc >= 0x8000)
                break;
            gb.StepInstruction();
        }
    }

    private static byte RunAndReadBgp(string source, OptimizationLevel level)
    {
        var gb = Load(Compile(source, level), out int s, out int l);
        Run(gb, s, l);
        return gb.DebugReadByte(0xFF47);
    }

    // ---- Fixture: a no-capture lambda, cached via Roslyn's <>9__ singleton idiom --------------

    private const string NoCaptureSource = """
        using Koh.GameBoy;
        using System;

        public class Program
        {
            private static int NoCapture()
            {
                Func<int, int> f = x => x + 1;
                return f(5);
            }

            public static void Main()
            {
                Hardware.BGP = (byte)NoCapture();
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task NoCaptureLambda_VerifiesCleanAndInvokesResolvedTarget(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(NoCaptureSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        // 5 + 1 = 6, only if the cache-idiom-resolved `f(5)` genuinely called the lambda body.
        await Assert.That(RunAndReadBgp(NoCaptureSource, level)).IsEqualTo((byte)6);
    }

    [Test]
    public async Task NoCaptureLambda_DebugAndReleaseProduceIdenticalObservableState()
    {
        var debug = RunAndReadBgp(NoCaptureSource, OptimizationLevel.Debug);
        var release = RunAndReadBgp(NoCaptureSource, OptimizationLevel.Release);
        await Assert.That(release).IsEqualTo(debug);
    }

    // ---- Fixture: a capturing lambda, heap-allocated via Roslyn's display class ----------------

    private const string CaptureSource = """
        using Koh.GameBoy;
        using System;

        public class Program
        {
            private static int Capture(int local)
            {
                Func<int, int> f = x => x + local;
                return f(3);
            }

            public static void Main()
            {
                Hardware.BGP = (byte)Capture(10);
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task CapturingLambda_VerifiesCleanAndInvokesResolvedTarget(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var module = Frontend(CaptureSource, level, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse();
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        // The display-class allocation needs the shared heap global.
        await Assert.That(module.Globals.Any(g => g.Name == "__heap")).IsTrue();

        // 3 + 10 = 13, only if the captured `local` genuinely round-tripped through the heap-
        // allocated display class into the lambda body.
        await Assert.That(RunAndReadBgp(CaptureSource, level)).IsEqualTo((byte)13);
    }

    [Test]
    public async Task CapturingLambda_DebugAndReleaseProduceIdenticalObservableState()
    {
        var debug = RunAndReadBgp(CaptureSource, OptimizationLevel.Debug);
        var release = RunAndReadBgp(CaptureSource, OptimizationLevel.Release);
        await Assert.That(release).IsEqualTo(debug);
    }

    // ---- A delegate through a parameter — the former escape-hatch diagnostic, now enabler E3 ----

    // `Apply` receives an already-constructed delegate as a plain parameter — no `newobj`/cache-
    // idiom trail to a single target at the invoke. Before enabler E3 (the ideal-game-API
    // program's stored-delegates milestone, motivated by the JRPG north star's dialogue callback)
    // this was a diagnostic; it now lowers via boundary materialization + closed-world
    // CilDelegateRegistry dispatch (see CilStoredDelegateTests for the full matrix). This fixture
    // keeps the original shape and pins the E3 behavior: no diagnostic, and the right answer on
    // the emulator. A delegate TYPE with no creation site anywhere remains a diagnostic.
    private const string UnresolvableDelegateSource = """
        using Koh.GameBoy;
        using System;

        public class Program
        {
            private static int Apply(Func<int, int> f, int x) => f(x);

            public static void Main()
            {
                Hardware.BGP = (byte)Apply(v => v + 1, 5);
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task DelegateThroughParameter_LowersViaBlobDispatch(OptimizationLevel level)
    {
        var diagnostics = new DiagnosticBag();
        var assemblyPath = CompileToAssembly(UnresolvableDelegateSource, level);
        var input = CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        var module = new CilFrontend().Lower(input, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        await Assert.That(RunAndReadBgp(UnresolvableDelegateSource, level)).IsEqualTo((byte)6); // Apply(v => v + 1, 5)
    }

    // ---- callvirt devirtualization on a known concrete (sealed) type ---------------------------
    //
    // Neither lambda fixture above exercises LowerInstanceCall's isVirtualDispatch branch — a
    // delegate Invoke is intercepted before ever reaching it. A plain Roslyn-compiled polymorphic
    // call site doesn't reach it either: for `d.Get()` with `d` statically typed as the sealed
    // `Derived`, Roslyn's callvirt token names the ORIGINAL virtual declaration (`Base.Get`, still
    // IsFinal=false and DeclaringType=Base, not sealed) rather than the override — CLR virtual
    // dispatch only needs the slot, so Roslyn doesn't bother re-pointing the token. To exercise the
    // branch this task actually added (IsVirtual && !IsFinal && DeclaringType.IsSealed — the shape
    // task 3's iterator devirtualization needs, where a resolved concrete type's own override token
    // IS what gets called), this test patches one instruction's operand with Cecil after a normal
    // Roslyn compile: repointing `callvirt Base::Get()` to `callvirt Derived::Get()` is exactly what
    // the CLR would do anyway could Roslyn statically prove the receiver's exact type — this is not
    // a synthetic/invalid IL shape, just one Roslyn happens not to emit.
    private const string SealedOverrideSource = """
        using Koh.GameBoy;

        public class Base
        {
            public virtual int Get() => 1;
        }

        public sealed class Derived : Base
        {
            public override int Get() => 42;
        }

        public class Program
        {
            public static void Main()
            {
                Derived d = new Derived();
                Hardware.BGP = (byte)d.Get();
            }
        }
        """;

    private static string PatchCallvirtToSealedOverride(string assemblyPath)
    {
        var patchedPath = Path.Combine(ScratchDir, $"cil_deleg_patched_{Guid.NewGuid():N}.dll");
        using var module = Mono.Cecil.ModuleDefinition.ReadModule(assemblyPath);
        var baseGet = module.GetType("Base").Methods.Single(m => m.Name == "Get");
        var derivedGet = module.GetType("Derived").Methods.Single(m => m.Name == "Get");
        var main = module.GetType("Program").Methods.Single(m => m.Name == "Main");

        var patched = false;
        foreach (var instr in main.Body.Instructions)
        {
            if (
                instr.OpCode.Code == Mono.Cecil.Cil.Code.Callvirt
                && instr.Operand is Mono.Cecil.MethodReference mr
                && mr.Resolve() == baseGet
            )
            {
                instr.Operand = module.ImportReference(derivedGet);
                patched = true;
            }
        }
        if (!patched)
            throw new InvalidOperationException(
                "expected to find a 'callvirt Base::Get()' in Program.Main to patch."
            );

        module.Write(patchedPath);
        return patchedPath;
    }

    [Test]
    public async Task CallvirtOnSealedDeclaringType_DevirtualizesToDirectCall()
    {
        var assemblyPath = CompileToAssembly(SealedOverrideSource, OptimizationLevel.Release);
        var patchedPath = PatchCallvirtToSealedOverride(assemblyPath);

        var diagnostics = new DiagnosticBag();
        var input = CompilerInput.FromAssembly(
            patchedPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        var module = new CilFrontend().Lower(input, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        var main = module.FindFunction("Program.Main");
        await Assert.That(main).IsNotNull();
        var directCall = main!
            .Blocks.SelectMany(b => b.Instructions)
            .OfType<CallInstruction>()
            .Any(c => c.Callee.Name == "Derived.Get");
        await Assert.That(directCall).IsTrue();

        // The devirtualized call must actually run correctly too, not just verify.
        IrOptimizer.Optimize(module);
        var model = new Sm83Backend().Compile(module, new DiagnosticBag());
        var gb = Load(model, out int s, out int l);
        Run(gb, s, l);
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)42);
    }

    // ---- ldvirtftn: a method-group delegate off a virtual method --------------------------------

    // `d.Get` (a method-group conversion, not a call) off a *virtual* method forces Roslyn to emit
    // `ldvirtftn` instead of `ldftn` — the delegate-construction sequence is otherwise identical
    // (env pushed/`dup`'d, then the ftn, then `newobj`). `ldvirtftn`'s operand only names the vtable
    // SLOT it was compiled against (`Base::Get`) — resolving it directly to that token would silently
    // invoke the WRONG method whenever the receiver's runtime type overrides it (exactly this
    // fixture: `d`'s runtime type is `Derived`, so real .NET returns 42, not 1). With no concrete-
    // type tracking (task 3's job) this frontend cannot prove the receiver's type, so it must
    // diagnose rather than silently bind the base slot — same rule LowerInstanceCall already applies
    // to an equivalent unresolvable `callvirt`.
    private const string VirtualMethodGroupSource = """
        using Koh.GameBoy;
        using System;

        public class Base
        {
            public virtual int Get() => 1;
        }

        public sealed class Derived : Base
        {
            public override int Get() => 42;
        }

        public class Program
        {
            public static void Main()
            {
                Derived d = new Derived();
                Func<int> f = d.Get;
                Hardware.BGP = (byte)f();
            }
        }
        """;

    [Test]
    [Arguments(OptimizationLevel.Debug)]
    [Arguments(OptimizationLevel.Release)]
    public async Task VirtualMethodGroupDelegate_IsDiagnosedNotSilentlyMisresolved(
        OptimizationLevel level
    )
    {
        var diagnostics = new DiagnosticBag();
        var assemblyPath = CompileToAssembly(VirtualMethodGroupSource, level);
        var input = CompilerInput.FromAssembly(
            assemblyPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        // Must not throw, and must not silently bind Base.Get() (which would make the ROM return the
        // wrong value, 1, instead of the real .NET answer, 42) — an unresolvable virtual dispatch is
        // reported as a diagnostic.
        var module = new CilFrontend().Lower(input, diagnostics);
        await Assert.That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error)).IsTrue();
        await Assert.That(diagnostics.Any(d => d.Message.Contains("ldvirtftn"))).IsTrue();
    }

    // The positive case — resolvable when the vtable-slot token's OWN declaring type is sealed —
    // needs a token Roslyn doesn't emit for this source (see
    // CallvirtOnSealedDeclaringType_DevirtualizesToDirectCall's remarks: Roslyn always compiles
    // against the original virtual declaration). Patch the same instruction's operand the same way.
    private static string PatchLdvirtftnToSealedOverride(string assemblyPath)
    {
        var patchedPath = Path.Combine(ScratchDir, $"cil_deleg_patched_{Guid.NewGuid():N}.dll");
        using var module = Mono.Cecil.ModuleDefinition.ReadModule(assemblyPath);
        var baseGet = module.GetType("Base").Methods.Single(m => m.Name == "Get");
        var derivedGet = module.GetType("Derived").Methods.Single(m => m.Name == "Get");
        var main = module.GetType("Program").Methods.Single(m => m.Name == "Main");

        var patched = false;
        foreach (var instr in main.Body.Instructions)
        {
            if (
                instr.OpCode.Code == Mono.Cecil.Cil.Code.Ldvirtftn
                && instr.Operand is Mono.Cecil.MethodReference mr
                && mr.Resolve() == baseGet
            )
            {
                instr.Operand = module.ImportReference(derivedGet);
                patched = true;
            }
        }
        if (!patched)
            throw new InvalidOperationException(
                "expected to find a 'ldvirtftn Base::Get()' in Program.Main to patch."
            );

        module.Write(patchedPath);
        return patchedPath;
    }

    [Test]
    public async Task VirtualMethodGroupDelegate_ResolvesToSealedOverrideWhenTokenIsSealed()
    {
        var assemblyPath = CompileToAssembly(VirtualMethodGroupSource, OptimizationLevel.Release);
        var patchedPath = PatchLdvirtftnToSealedOverride(assemblyPath);

        var diagnostics = new DiagnosticBag();
        var input = CompilerInput.FromAssembly(
            patchedPath,
            [typeof(Koh.GameBoy.Hardware).Assembly.Location]
        );
        var module = new CilFrontend().Lower(input, diagnostics);
        await Assert
            .That(diagnostics.Any(d => d.Severity == KohDiagnosticSeverity.Error))
            .IsFalse()
            .Because(string.Join(" | ", diagnostics.Select(d => d.Message)));
        await Assert.That(IrVerifier.Verify(module)).IsEmpty();

        IrOptimizer.Optimize(module);
        var model = new Sm83Backend().Compile(module, new DiagnosticBag());
        var gb = Load(model, out int s, out int l);
        Run(gb, s, l);
        await Assert.That(gb.DebugReadByte(0xFF47)).IsEqualTo((byte)42);
    }
}
