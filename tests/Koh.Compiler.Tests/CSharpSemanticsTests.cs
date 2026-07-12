using Koh.Compiler.Frontends.CSharp;
using Koh.Core.Diagnostics;
using Koh.Core.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Tests;

/// <summary>Phase 1 of the semantic-model migration: the Roslyn compilation/semantic-model plumbing
/// (<see cref="CSharpSemantics"/>, <see cref="IntrinsicsStub"/>) is wired in but not yet consulted by
/// lowering. These tests only assert the plumbing itself: the stub binds, symbols still resolve on
/// Koh-legal-but-C#-illegal code, detached nodes are guarded, and the exotic BCL surface binds.</summary>
public class CSharpSemanticsTests
{
    private static (CompilationUnitSyntax Root, CSharpSemantics Semantics) Build(string source) =>
        CSharpFrontend.BuildSemanticsForTest(source);

    private static MemberAccessExpressionSyntax MemberAccess(
        CompilationUnitSyntax root,
        string subject,
        string name
    ) =>
        root.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .First(m =>
                m.Expression is IdentifierNameSyntax { Identifier.Text: var s }
                && s == subject
                && m.Name.Identifier.Text == name
            );

    // ---- Stub binding -------------------------------------------------------------------------

    // One compilation covering the whole intrinsic surface (Hardware/Gb/Mem/Interrupt), rather than one
    // per member: each Build() call constructs a fresh CSharpCompilation over the full BCL reference set,
    // so combining assertions keeps the suite's wall-clock cost down without losing coverage.
    [Test]
    public async Task IntrinsicSurface_ResolvesToStubSymbols()
    {
        const string src = """
            [Interrupt("VBlank")]
            static unsafe void F() {
                byte r = Hardware.LCDC;
                byte* v = Gb.Vram;
                byte* a = Mem.Alloc(4);
            }
            """;
        var (root, semantics) = Build(src);

        var hardware = semantics.Sym(MemberAccess(root, "Hardware", "LCDC"));
        await Assert.That(hardware).IsTypeOf<IPropertySymbol>();
        await Assert
            .That(
                SymbolEqualityComparer.Default.Equals(
                    ((IPropertySymbol)hardware!).ContainingType,
                    semantics.HardwareType
                )
            )
            .IsTrue();

        var gb = semantics.Sym(MemberAccess(root, "Gb", "Vram"));
        await Assert.That(gb).IsTypeOf<IPropertySymbol>();
        await Assert
            .That(
                SymbolEqualityComparer.Default.Equals(
                    ((IPropertySymbol)gb!).ContainingType,
                    semantics.GbType
                )
            )
            .IsTrue();

        var allocCall = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(i =>
                i.Expression
                    is MemberAccessExpressionSyntax
                    {
                        Expression: IdentifierNameSyntax { Identifier.Text: "Mem" },
                        Name.Identifier.Text: "Alloc",
                    }
            );
        var mem = semantics.Sym(allocCall);
        await Assert.That(mem).IsTypeOf<IMethodSymbol>();
        await Assert
            .That(
                SymbolEqualityComparer.Default.Equals(
                    ((IMethodSymbol)mem!).ContainingType,
                    semantics.MemType
                )
            )
            .IsTrue();

        var attribute = root.DescendantNodes().OfType<AttributeSyntax>().First();
        var interruptCtor = semantics.Sym(attribute);
        await Assert.That(interruptCtor).IsTypeOf<IMethodSymbol>();
        await Assert
            .That(((IMethodSymbol)interruptCtor!).MethodKind)
            .IsEqualTo(MethodKind.Constructor);
        await Assert
            .That(
                SymbolEqualityComparer.Default.Equals(
                    ((IMethodSymbol)interruptCtor!).ContainingType,
                    semantics.InterruptAttributeType
                )
            )
            .IsTrue();
    }

    // ---- Roslyn diagnostics never gate resolution ----------------------------------------------

    [Test]
    public async Task SymbolsResolve_DespiteKohLegalCodeBeingCSharpIllegal()
    {
        // `a + b` promotes to `int` in real C#, so assigning it to `byte c` is CS0266 — Koh's own rules
        // keep byte + byte as byte. Pointer types outside `unsafe` are CS0214 — Koh C# allows `Gb.*`
        // pointer arithmetic anywhere. Roslyn's binder still resolves every identifier despite both.
        const string src = """
            static byte Add(byte a, byte b) {
                byte c = a + b;
                byte* p = Gb.Vram + c;
                return *p;
            }
            """;
        var (root, semantics) = Build(src);

        var idA = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(i => i.Identifier.Text == "a" && i.Parent is BinaryExpressionSyntax);
        var idB = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(i => i.Identifier.Text == "b" && i.Parent is BinaryExpressionSyntax);
        var gbVram = MemberAccess(root, "Gb", "Vram");

        await Assert.That(semantics.Sym(idA)).IsNotNull();
        await Assert.That(semantics.Sym(idB)).IsNotNull();
        var gbSymbol = semantics.Sym(gbVram);
        await Assert.That(gbSymbol).IsNotNull();
        await Assert
            .That(
                SymbolEqualityComparer.Default.Equals(
                    ((IPropertySymbol)gbSymbol!).ContainingType,
                    semantics.GbType
                )
            )
            .IsTrue();

        var diagnostics = semantics.Compilation!.GetDiagnostics();
        await Assert.That(diagnostics.Any(d => d.Id == "CS0266")).IsTrue();
        await Assert.That(diagnostics.Any(d => d.Id == "CS0214")).IsTrue();
    }

    // ---- Detached-node guard --------------------------------------------------------------------

    [Test]
    public async Task DetachedNode_InTreeIsFalse_SymReturnsNullWithoutThrowing()
    {
        var (_, semantics) = Build("static byte F() { return 1; }");

        // A node from a wholly separate tree, standing in for a monomorphized generic instance's body
        // (CSharpFrontend.Generics.cs's TypeParamRewriter), which is likewise detached from the main tree.
        var foreignTree = CSharpSyntaxTree.ParseText(
            "class Detached { void M() { int x = 0; x = x; } }"
        );
        var foreignNode = foreignTree
            .GetRoot()
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(i => i.Identifier.Text == "x");

        await Assert.That(semantics.InTree(foreignNode)).IsFalse();
        await Assert.That(semantics.Sym(foreignNode)).IsNull();
    }

    // ---- Exotic BCL surface ----------------------------------------------------------------------

    [Test]
    public async Task Int128AndStackalloc_BindInTheCompilation()
    {
        const string src = """
            static byte F() {
                Int128 big = 5;
                byte* p = stackalloc byte[4];
                return *p;
            }
            """;
        var (root, semantics) = Build(src);

        var int128 = root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(i => i.Identifier.Text == "Int128");
        var int128Symbol = semantics.Sym(int128);
        await Assert.That(int128Symbol).IsNotNull();
        await Assert.That(int128Symbol!.Kind).IsEqualTo(SymbolKind.NamedType);
        await Assert.That(((INamedTypeSymbol)int128Symbol).Name).IsEqualTo("Int128");

        var stackallocExpr = root.DescendantNodes()
            .OfType<StackAllocArrayCreationExpressionSyntax>()
            .First();
        var model = semantics.Compilation!.GetSemanticModel(root.SyntaxTree);
        var stackallocType = model.GetTypeInfo(stackallocExpr).Type;
        await Assert.That(stackallocType).IsNotNull();
        await Assert.That(stackallocType).IsTypeOf<IPointerTypeSymbol>();
    }

    // ---- End-to-end sanity -----------------------------------------------------------------------

    [Test]
    public async Task TinyProgram_StillLowersWithEmptyDiagnostics()
    {
        var diagnostics = new DiagnosticBag();
        new CSharpFrontend().Lower(
            SourceText.From("static byte Add(byte a, byte b) { return a + b; }", "game.cs"),
            diagnostics
        );
        await Assert.That(diagnostics).IsEmpty();
    }

    // ---- Stub/table parity -----------------------------------------------------------------------

    [Test]
    public async Task GeneratedStub_NamesEveryHardwareRegisterAndRegion()
    {
        var stub = IntrinsicsStub.Generate();
        foreach (var name in HardwareRegisters.RegisterNames)
            await Assert.That(stub.Contains($" {name} ")).IsTrue();
        foreach (var name in HardwareRegisters.RegionNames)
            await Assert.That(stub.Contains($" {name} ")).IsTrue();
    }
}
