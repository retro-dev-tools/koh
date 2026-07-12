using Koh.Compiler.Backends.Sm83;
using Koh.Compiler.Frontends.CSharp;
using Koh.Compiler.Ir;
using Koh.Core.Binding;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Koh.Emulator.Core;
using Koh.Emulator.Core.Cartridge;
using Koh.Linker.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using LinkerType = Koh.Linker.Core.Linker;

namespace Koh.Compiler.Tests;

/// <summary>Stage-2 P3 of the symbol-only-resolution migration: <c>CSharpSemantics.SymOrCandidate</c>
/// (candidate-aware resolution) alongside the untouched <c>Sym</c>, wired into <c>MethodLowerer</c>'s
/// callee/field symbol-first sites (<c>LowerCall</c>'s <c>symCallee</c>, <c>LowerInstanceCall</c>'s
/// symbol parameter, <c>TryGlobal</c>, <c>TryModuleConst</c>, <c>ResolvedFieldName</c>). These sites
/// previously fell straight through to their pre-migration string-keyed fallback whenever Roslyn's own
/// C# rules (not Koh's) rejected an otherwise Koh-legal program: mixed-width arithmetic in a call
/// argument fails overload resolution (<c>Helper(a + b)</c> with <c>byte a, b</c> types the sum as C#'s
/// <c>int</c>, and there's no implicit <c>int</c>-to-<c>byte</c> conversion), and Koh's own disregard
/// for C# accessibility means a cross-class reference to a `private` member is <c>Inaccessible</c> to
/// Roslyn. <see cref="CSharpSemantics.SymOrCandidate"/> accepts the single resolvable candidate for
/// exactly those two <see cref="CandidateReason"/>s, so the real callee/field resolves symbol-first —
/// proven directly against the semantic model, not just by the program still lowering (which the
/// pre-existing string fallback would also achieve, proving nothing about which path actually fired).</summary>
public class CSharpCandidateResolutionTests
{
    // ---- End-to-end harness (mirrors CSharpSemanticsResolutionTests) --------------------------------

    private static IrModule Frontend(string src)
    {
        var diagnostics = new DiagnosticBag();
        var module = new CSharpFrontend().Lower(SourceText.From(src, "game.cs"), diagnostics);
        if (!diagnostics.Any(d => d.Severity == Koh.Core.Diagnostics.DiagnosticSeverity.Error))
        {
            var errors = IrVerifier.Verify(module);
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    "IR verification failed:\n  " + string.Join("\n  ", errors)
                );
        }
        return module;
    }

    private static EmitModel Compile(string src) =>
        new Sm83Backend().Compile(Frontend(src), new DiagnosticBag());

    private static GameBoySystem Load(EmitModel model, out int start, out int length)
    {
        var link = new LinkerType().Link([new LinkerInput("cs", model)]);
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
            if (pc < start || pc >= start + length)
                break;
            gb.StepInstruction();
        }
    }

    private static byte RunA(string src)
    {
        var gb = Load(Compile(src), out int s, out int l);
        Run(gb, s, l);
        return gb.Registers.A;
    }

    private static DiagnosticBag LowerDiagnostics(string src)
    {
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(SourceText.From(src, "game.cs"), diagnostics);
        return diagnostics;
    }

    private static bool HasError(string src) =>
        LowerDiagnostics(src).Any(d => d.Severity == Koh.Core.Diagnostics.DiagnosticSeverity.Error);

    private static Koh.Core.Diagnostics.Diagnostic OnlyError(string src)
    {
        var diagnostics = LowerDiagnostics(src).ToList();
        var errors = diagnostics
            .Where(d => d.Severity == Koh.Core.Diagnostics.DiagnosticSeverity.Error)
            .ToList();
        if (errors.Count != 1)
            throw new InvalidOperationException(
                $"expected exactly one error diagnostic, got {errors.Count}: "
                    + string.Join(" | ", diagnostics.Select(d => d.ToString()))
            );
        return errors[0];
    }

    // ---- Direct-assertion harness: locate a node in the real production main tree -------------------

    private static (IrModule Module, CSharpSemantics Semantics) Build(string source)
    {
        var diagnostics = new DiagnosticBag();
        var result = CSharpFrontend.LowerForTest(source, diagnostics);
        return result;
    }

    /// <summary>The main (non-stub, non-instances) syntax tree, so a test can find the node it wants to
    /// probe. Mirrors <c>CSharpSemanticsIndexTests.MainRoot</c>.</summary>
    private static CompilationUnitSyntax MainRoot(CSharpSemantics semantics) =>
        semantics
            .Compilation!.SyntaxTrees.First(t =>
                t != IntrinsicsStub.Tree && t.FilePath != "__KohGenericInstances.cs"
            )
            .GetCompilationUnitRoot();

    // ---- Mixed-width-argument call: OverloadResolutionFailure --------------------------------------

    [Test]
    public async Task ByteArithmeticArgument_CompilesClean_AndRunsCorrectly()
    {
        // `Helper(a + b)` types the sum as C#'s `int` (byte+byte promotes); Helper's only parameter is
        // `byte`, and there is no implicit int-to-byte conversion, so Roslyn's overload resolution
        // rejects the call outright (Symbol null). Koh's own usual-arithmetic-conversion rules accept it
        // (the sum truncates to byte at the call boundary), so the program must still compile and run
        // exactly as before candidate acceptance existed.
        const string src =
            "static byte Helper(byte x) => x;\n"
            + "static byte Main() { byte a = 3; byte b = 4; return Helper(a + b); }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)7);
    }

    [Test]
    public async Task ByteArithmeticArgument_ResolvesViaCandidate_NotPlainSym()
    {
        // The behavioral test above only proves *a* resolution path worked (the string fallback would
        // have produced the identical program too). This proves it's specifically the new candidate path:
        // plain Sym is null (Roslyn's own binder truly rejects the call), while SymOrCandidate recovers
        // the real Helper symbol from the invocation's lone OverloadResolutionFailure candidate.
        const string src =
            "static byte Helper(byte x) => x;\n"
            + "static byte Main() { byte a = 3; byte b = 4; return Helper(a + b); }";
        var (_, semantics) = Build(src);
        var call = MainRoot(semantics)
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First();

        await Assert.That(semantics.Sym(call)).IsNull();
        var candidate = semantics.SymOrCandidate(call) as IMethodSymbol;
        await Assert.That(candidate).IsNotNull();
        await Assert.That(candidate!.Name).IsEqualTo("Helper");
    }

    // ---- Cross-class private member access: Inaccessible -------------------------------------------

    [Test]
    public async Task PrivateInstanceField_CrossClassRead_CompilesClean_AndRunsCorrectly()
    {
        // `c.Value` reads Counter's `private` field from Reader, a different class. Koh ignores
        // accessibility entirely (ResolvedFieldName/StructFieldPointer never checked it, even
        // pre-migration), but Roslyn's own binder does: `Value` is Inaccessible from Reader's scope, so
        // Symbol is null. Must still compile/run identically.
        const string src =
            "class Counter { private byte Value; public void Set(byte v) { Value = v; } }\n"
            + "static class Reader { public static byte Read(Counter c) => c.Value; }\n"
            + "static byte Main() { Counter c = new Counter(); c.Set(5); return Reader.Read(c); }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)5);
    }

    [Test]
    public async Task PrivateInstanceField_CrossClassRead_ResolvesViaCandidate_NotPlainSym()
    {
        const string src =
            "class Counter { private byte Value; public void Set(byte v) { Value = v; } }\n"
            + "static class Reader { public static byte Read(Counter c) => c.Value; }\n"
            + "static byte Main() { Counter c = new Counter(); c.Set(5); return Reader.Read(c); }";
        var (_, semantics) = Build(src);
        var member = MainRoot(semantics)
            .DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .First(m => m.Name.Identifier.Text == "Value" && m.ToString() == "c.Value");

        await Assert.That(semantics.Sym(member)).IsNull();
        var candidate = semantics.SymOrCandidate(member) as IFieldSymbol;
        await Assert.That(candidate).IsNotNull();
        await Assert.That(candidate!.Name).IsEqualTo("Value");
    }

    [Test]
    public async Task PrivateStaticMethod_CrossClassCall_CompilesClean_AndRunsCorrectly()
    {
        // `A.Secret()` calls a `private` static method of A from sibling class B — Inaccessible to
        // Roslyn, ignored by Koh. This call already lowered fine pre-P3 via the qualified-name syntax
        // fallback in LowerCall; this pins that it keeps working once the symbol-first path also fires.
        const string src =
            "static class A { private static byte Secret() => 42; }\n"
            + "static class B { public static byte Read() => A.Secret(); }\n"
            + "static byte Main() { return B.Read(); }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)42);
    }

    [Test]
    public async Task PrivateStaticMethod_CrossClassCall_ResolvesViaCandidate_NotPlainSym()
    {
        const string src =
            "static class A { private static byte Secret() => 42; }\n"
            + "static class B { public static byte Read() => A.Secret(); }\n"
            + "static byte Main() { return B.Read(); }";
        var (_, semantics) = Build(src);
        var call = MainRoot(semantics)
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(c => c.ToString() == "A.Secret()");

        await Assert.That(semantics.Sym(call)).IsNull();
        var candidate = semantics.SymOrCandidate(call) as IMethodSymbol;
        await Assert.That(candidate).IsNotNull();
        await Assert.That(candidate!.Name).IsEqualTo("Secret");
    }

    // ---- Guard: accepting a candidate must never bypass Koh's own arity check ----------------------

    [Test]
    public async Task WrongArgumentCount_StillReportsKohsArityDiagnostic_NotUnsupportedOrSilent()
    {
        // Helper takes exactly one argument; the call supplies two. Roslyn's overload resolution rejects
        // the call (OverloadResolutionFailure) with the same single real Helper as its lone candidate —
        // the same reason/shape as the correct-arity case above — so SymOrCandidate resolves it exactly
        // as readily. The guard is downstream: LowerCall's arity check compares the call's own syntax
        // argument count against the resolved callee's *real* parameter count (not the candidate's
        // presence), so a wrong-arity call still fails loudly with Koh's own message instead of silently
        // calling Helper with a dropped argument, or falling through to "unsupported call target".
        const string src =
            "static byte Helper(byte x) => x;\n"
            + "static byte Main() { byte a = 3; byte b = 4; return Helper(a, b); }";
        var error = OnlyError(src);
        await Assert.That(error.Message).Contains("'Helper' takes 1 argument(s), but 2 were given");
        await Assert.That(error.Message).DoesNotContain("unsupported call target");
    }

    // ---- Genuinely ambiguous: neither Ambiguous nor a multi-candidate non-match is ever guessed at ---

    [Test]
    public async Task MultipleCandidates_NoneAKnownDeclaration_ResolvesToNull()
    {
        // Two overloads with swapped parameter types (`Helper(int, long)` / `Helper(long, int)`) called
        // as `Helper(1, 1)`: both are equally applicable from an int-literal argument pair, so Roslyn
        // reports OverloadResolutionFailure with BOTH as CandidateSymbols (this is how Roslyn surfaces
        // invocation ambiguity — CandidateReason.Ambiguous is for a non-invoked reference, not a call).
        //
        // A genuinely ambiguous multi-candidate case where BOTH candidates are simultaneously registered
        // as live Koh declarations isn't constructible via any supported Koh program: Koh has no method
        // overloading at all (CSharpFrontend keeps only the first same-named declaration and reports the
        // rest as a "duplicate function" diagnostic, so at most one candidate for any given name is ever
        // registered — see DuplicateFunctionName_IsStillADiagnostic in CSharpSemanticsResolutionTests).
        // This test isolates the underlying safety net directly instead, via BuildSemanticsForTest (which
        // builds the same production tree/compilation shape but does not run the declaration passes that
        // call RegisterMethod, so Methods stays empty regardless of what the source declares): with
        // nothing registered at all, neither raw candidate is a known Koh declaration, so SymOrCandidate
        // must decline to guess rather than silently pick one — proving the "otherwise null" branch is
        // real, not merely unreachable dead code.
        const string src =
            "static byte Main() { return Helper(1, 1); }\n"
            + "static byte Helper(int x, long y) => 1;\n"
            + "static byte Helper(long x, int y) => 2;";
        var (root, semantics) = CSharpFrontend.BuildSemanticsForTest(src);
        var call = root.DescendantNodes().OfType<InvocationExpressionSyntax>().First();

        await Assert.That(semantics.Sym(call)).IsNull();
        await Assert.That(semantics.SymOrCandidate(call)).IsNull();

        // Meanwhile the real, fully-lowered program (both overloads registered up to the first, per
        // Koh's own duplicate-name policy) still reports its pre-existing duplicate-function diagnostic
        // and still lowers/runs via the string-keyed fallback to the first declaration — candidate
        // acceptance changes nothing about that already-established behavior.
        await Assert.That(HasError(src)).IsTrue();
        await Assert.That(RunA(src)).IsEqualTo((byte)1);
    }

    // ---- End-to-end regression: byte-arithmetic args + private cross-class access together ----------

    [Test]
    public async Task MixedByteArithmeticAndPrivateCrossClassAccess_ComputesCorrectly()
    {
        const string src = """
            class Counter { private byte Value; public void Set(byte v) { Value = v; } }
            static class Reader { public static byte Read(Counter c) => c.Value; }
            static class A { private static byte Secret() => 42; }
            static class B { public static byte ReadA() => A.Secret(); }
            static byte Helper(byte x) => x;
            static byte Main() {
              Counter c = new Counter();
              c.Set(5);
              byte fromField = Reader.Read(c);
              byte fromMethod = B.ReadA();
              byte a1 = 3;
              byte b1 = 4;
              return (byte)(Helper(a1 + b1) + fromField + fromMethod);
            }
            """;
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)54); // Helper(3+4)=7 + Value(5) + Secret(42) = 54
    }
}
