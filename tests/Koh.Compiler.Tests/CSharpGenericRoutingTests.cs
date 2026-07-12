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

/// <summary>Stage-2 P4 of the symbol-only-resolution migration: <c>MethodLowerer.LowerCall</c> now routes
/// a generic call site to its monomorphized instance through <see cref="CSharpSemantics.GenericInstances"/>/
/// <see cref="CSharpSemantics.TryGetGenericInstance"/> — the call's own resolved method symbol
/// (<c>OriginalDefinition</c>, the template) plus its own explicit <c>&lt;...&gt;</c> type-argument syntax
/// (mangled via <see cref="CSharpFrontend.MangleSuffix"/>) selects the exact same <see cref="CsMethod"/>
/// the pre-P4 syntax-based <c>MangleGeneric</c> switch would also have found — a strict shortcut to the
/// same answer, not a second source of truth. That syntax switch remains as the fallback for a call whose
/// type arguments were inferred rather than written out (P5 removes it). These tests mirror
/// <see cref="CSharpSemanticsIndexTests"/>'s and <see cref="CSharpCandidateResolutionTests"/>'s harnesses:
/// real end-to-end lowering/running plus direct assertions against the production trees, so a test proves
/// the symbol-first path actually fired rather than merely that some path did.</summary>
public class CSharpGenericRoutingTests
{
    // ---- End-to-end harness (mirrors CSharpSemanticsResolutionTests / CSharpCandidateResolutionTests) -

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

    // ---- Direct-assertion harness: locate a node in the real production trees -----------------------

    private static (IrModule Module, CSharpSemantics Semantics) Build(string source)
    {
        var diagnostics = new DiagnosticBag();
        return CSharpFrontend.LowerForTest(source, diagnostics);
    }

    /// <summary>The main (non-stub, non-instances) syntax tree. Mirrors <c>CSharpSemanticsIndexTests.MainRoot</c>.</summary>
    private static CompilationUnitSyntax MainRoot(CSharpSemantics semantics) =>
        semantics
            .Compilation!.SyntaxTrees.First(t =>
                t != IntrinsicsStub.Tree && t.FilePath != "__KohGenericInstances.cs"
            )
            .GetCompilationUnitRoot();

    /// <summary>The second, constructed tree housing monomorphized generic instances. Mirrors
    /// <c>CSharpSemanticsIndexTests.InstancesRoot</c>.</summary>
    private static CompilationUnitSyntax InstancesRoot(CSharpSemantics semantics) =>
        semantics
            .Compilation!.SyntaxTrees.First(t => t.FilePath == "__KohGenericInstances.cs")
            .GetCompilationUnitRoot();

    /// <summary>Resolves <paramref name="template"/>'s <see cref="IMethodSymbol"/> and looks up the
    /// instance specialized at <paramref name="typeArgs"/>'s mangled suffix, exactly as
    /// <c>MethodLowerer.LowerCall</c>'s new routing branch does.</summary>
    private static bool TryRouteBySymbol(
        CSharpSemantics semantics,
        MethodDeclarationSyntax template,
        TypeArgumentListSyntax typeArgs,
        out CsMethod method
    )
    {
        var templateSymbol = (IMethodSymbol)semantics.DeclaredSym(template)!;
        return semantics.TryGetGenericInstance(
            templateSymbol,
            CSharpFrontend.MangleSuffix(typeArgs.Arguments),
            out method
        );
    }

    // ---- Bare generic call ----------------------------------------------------------------------------

    [Test]
    public async Task BareGenericCall_RoutesThroughSymbol_ToTheSameInstanceTheModuleContains()
    {
        const string src =
            "static byte Main() { return (byte)Max<byte>(3, 7); }\n"
            + "static T Max<T>(T a, T b) { if (a > b) return a; return b; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)7);

        var (module, semantics) = Build(src);
        var templateDecl = MainRoot(semantics)
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "Max");
        var call = MainRoot(semantics)
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(i => i.Expression is GenericNameSyntax { Identifier.Text: "Max" });
        var typeArgs = ((GenericNameSyntax)call.Expression).TypeArgumentList;

        var found = TryRouteBySymbol(semantics, templateDecl, typeArgs, out var instance);
        await Assert.That(found).IsTrue();
        await Assert.That(module.Functions.Any(f => f.Name == instance.Fn.Name)).IsTrue();
        await Assert.That(instance.Fn.Name).StartsWith("Max__g");
    }

    // ---- Class-qualified generic call ------------------------------------------------------------------

    [Test]
    public async Task QualifiedGenericCall_ClassOwner_RoutesThroughSymbol()
    {
        const string src =
            "static class Owner { public static T M<T>(T x) => x; }\n"
            + "static byte Main() { return (byte)Owner.M<byte>(9); }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)9);

        var (module, semantics) = Build(src);
        var templateDecl = MainRoot(semantics)
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "M");
        var call = MainRoot(semantics)
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(i =>
                i.Expression
                    is MemberAccessExpressionSyntax
                    {
                        Name: GenericNameSyntax { Identifier.Text: "M" }
                    }
            );
        var typeArgs = (
            (GenericNameSyntax)((MemberAccessExpressionSyntax)call.Expression).Name
        ).TypeArgumentList;

        var found = TryRouteBySymbol(semantics, templateDecl, typeArgs, out var instance);
        await Assert.That(found).IsTrue();
        await Assert.That(instance.Fn.Name).IsEqualTo("Owner.M__g1_4_byte");
        await Assert.That(module.Functions.Any(f => f.Name == "Owner.M__g1_4_byte")).IsTrue();
    }

    // ---- Transitive chain: a call inside one instance's own body routes another instance --------------

    [Test]
    public async Task TransitiveChain_ThreeLevels_EachCallRoutesThroughSymbol()
    {
        // Main -> A<byte> -> B<byte>: the call to B<T> lives inside A's own monomorphized body (a real
        // member of the instances tree, not detached syntax, since Stage-2 P2), and its type argument was
        // substituted from A's own T to the concrete `byte` by TypeParamRewriter before this call is ever
        // lowered — so the suffix computed from that call's own (now-concrete) syntax still matches the
        // instance B__g... was registered under.
        const string src =
            "static byte Main() { return (byte)A<byte>(5); }\n"
            + "static T A<T>(T x) { return (T)(B<T>(x) + 1); }\n"
            + "static T B<T>(T x) { return x; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)6);

        var (module, semantics) = Build(src);
        await Assert.That(module.Functions.Any(f => f.Name.StartsWith("A__g"))).IsTrue();
        await Assert.That(module.Functions.Any(f => f.Name.StartsWith("B__g"))).IsTrue();

        // The call to B<T> (now B<byte> post-substitution) inside A's specialized body resolves via Sym
        // (it's a real member of the instances tree) and routes through the same index.
        var bTemplateDecl = MainRoot(semantics)
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "B");
        var aInstanceDecl = InstancesRoot(semantics)
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text.StartsWith("A__g"));
        var innerCall = aInstanceDecl
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(i => i.Expression is GenericNameSyntax { Identifier.Text: "B" });
        var innerSymbol = semantics.Sym(innerCall) as IMethodSymbol;
        await Assert.That(innerSymbol).IsNotNull();

        var typeArgs = ((GenericNameSyntax)innerCall.Expression).TypeArgumentList;
        var found = TryRouteBySymbol(semantics, bTemplateDecl, typeArgs, out var bInstance);
        await Assert.That(found).IsTrue();
        await Assert
            .That(
                SymbolEqualityComparer.Default.Equals(
                    innerSymbol!.OriginalDefinition,
                    semantics.DeclaredSym(bTemplateDecl)
                )
            )
            .IsTrue();
        await Assert.That(module.Functions.Any(f => f.Name == bInstance.Fn.Name)).IsTrue();
    }

    // ---- Inferred type arguments: no explicit syntax to mangle, still unsupported ---------------------

    [Test]
    public async Task InferredTypeArgument_HasNoExplicitSyntaxToRouteBy_StaysUnsupported()
    {
        // `Id(5)` never spells out `<byte>`, so there is no GenericNameSyntax type-argument list for the
        // new routing branch to mangle a suffix from — GenericCallTypeArguments returns null and the call
        // falls through exactly as it did before Stage-2 P4 (this behavior is unchanged, not newly broken
        // by the routing addition).
        const string src = "static T Id<T>(T x) => x;\nstatic byte Main() { return (byte)Id(5); }";
        await Assert.That(HasError(src)).IsTrue();
    }

    // ---- Same-name, different arity: distinct templates route to distinct instances -------------------

    [Test]
    public async Task SameNameDifferentArity_RouteToDistinctInstances()
    {
        const string src =
            @"
static byte Main() { return (byte)(Pick<byte>(5) + Pick<byte, ushort>(7, 9)); }
static T Pick<T>(T a) { return a; }
static T Pick<T, U>(T a, U b) { return a; }";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)12); // 5 + 7

        var (module, semantics) = Build(src);
        var root = MainRoot(semantics);
        var oneArgTemplate = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "Pick" && m.TypeParameterList!.Parameters.Count == 1);
        var twoArgTemplate = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "Pick" && m.TypeParameterList!.Parameters.Count == 2);

        var oneArgCall = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(i =>
                i.Expression
                    is GenericNameSyntax
                    {
                        Identifier.Text: "Pick",
                        TypeArgumentList.Arguments.Count: 1
                    }
            );
        var twoArgCall = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(i =>
                i.Expression
                    is GenericNameSyntax
                    {
                        Identifier.Text: "Pick",
                        TypeArgumentList.Arguments.Count: 2
                    }
            );

        var foundOne = TryRouteBySymbol(
            semantics,
            oneArgTemplate,
            ((GenericNameSyntax)oneArgCall.Expression).TypeArgumentList,
            out var oneArgInstance
        );
        var foundTwo = TryRouteBySymbol(
            semantics,
            twoArgTemplate,
            ((GenericNameSyntax)twoArgCall.Expression).TypeArgumentList,
            out var twoArgInstance
        );

        await Assert.That(foundOne).IsTrue();
        await Assert.That(foundTwo).IsTrue();
        await Assert.That(oneArgInstance.Fn.Name).IsNotEqualTo(twoArgInstance.Fn.Name);
        await Assert.That(module.Functions.Any(f => f.Name == oneArgInstance.Fn.Name)).IsTrue();
        await Assert.That(module.Functions.Any(f => f.Name == twoArgInstance.Fn.Name)).IsTrue();
    }

    // ---- module.Functions order for monomorphized instances follows SYNTHESIS order, not the -----------
    // ---- instances tree's own document order (InstanceIndexAnnotation protocol) -------------------------

    [Test]
    public async Task SynthesisOrder_DiffersFromInstancesTreeDocumentOrder_ModuleFunctionsFollowsSynthesis()
    {
        // CSharpFrontend.Generics.cs's BuildInstancesTree nests every instance whose template has no
        // owning class ("bare", e.g. a legacy top-level generic function) directly under the wrapper,
        // ahead of every per-owner-class bucket, regardless of when each instance was synthesized (see its
        // own remarks: "bareMembers" is built first, then one nested partial class per owner in
        // `ownerOrder`). CSharpFrontend.cs's recovery of "the i'th instance" from that tree explicitly does
        // NOT walk it in that document order — it reads InstanceIndexAnnotation (the synthesis/work-list
        // order) back off each node and re-sorts by it, precisely so module.Functions order tracks
        // synthesis order instead of which nesting bucket an instance happened to land in (see
        // InstanceIndexAnnotation's and the recovery site's own remarks).
        //
        // This program is built so the two orders genuinely disagree: Main's first generic call is the
        // per-owner-class one (`Owner.M<byte>`), synthesized first (InstanceIndexAnnotation 0); its second
        // call is the bare one (`Bare<byte>`), synthesized second (annotation 1). But in the instances
        // tree's own document order, the bare instance is nested ahead of the owner-class bucket regardless
        // — so a walk of the tree in document order would see Bare before Owner.M, the OPPOSITE of
        // synthesis order. If the OrderBy-by-annotation recovery in CSharpFrontend.cs were ever dropped in
        // favor of walking the instances tree directly, Owner.M__g... and Bare__g... would swap places in
        // module.Functions and this assertion would flip.
        const string src =
            "static byte Main() { return (byte)(Owner.M<byte>(1) + Bare<byte>(2)); }\n"
            + "static class Owner { public static T M<T>(T x) => x; }\n"
            + "static T Bare<T>(T x) => x;";
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)3);

        var (module, _) = Build(src);
        int ownerIndex = module.Functions.FindIndex(f => f.Name.StartsWith("Owner.M__g"));
        int bareIndex = module.Functions.FindIndex(f => f.Name.StartsWith("Bare__g"));
        await Assert.That(ownerIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(bareIndex).IsGreaterThanOrEqualTo(0);
        // Synthesis order (Owner.M first, Bare second) — the opposite of the instances tree's own document
        // order (bare bucket first, owner-class bucket second).
        await Assert.That(ownerIndex).IsLessThan(bareIndex);
    }
}
