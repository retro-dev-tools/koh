using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Tests;

/// <summary>Stage 2 Phase 2 spike: pins the load-bearing Roslyn behavior the whole "instances tree" design
/// depends on (see the Stage-2 plan, design decision 2) — BEFORE any production code changes. A second,
/// constructed <c>SyntaxTree</c> can declare <c>static partial class W { static partial class C { ... } }</c>
/// alongside a main tree whose own <c>W</c>/<c>C</c> are NOT written as matching partials (only <c>W</c> is
/// partial there; <c>C</c> is a plain <c>static class</c>), and Roslyn still merges the two trees'
/// declarations of <c>C</c> for binding purposes — a call/field reference in the second tree's member
/// resolves to the first tree's sibling declarations — even though the mismatched partial-ness produces a
/// CS0260 diagnostic. If this is false, the whole instances-tree design (nesting synthesized generic
/// instances in a second tree so they bind via ordinary C# scoping) doesn't work and Phase 2 must halt.</summary>
public class CSharpInstancesTreeTests
{
    private static IReadOnlyList<MetadataReference> BclReferences()
    {
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string tpa || tpa.Length == 0)
            throw new InvalidOperationException("no TRUSTED_PLATFORM_ASSEMBLIES in this host.");
        var refs = new List<MetadataReference>();
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
            catch (IOException) { }
            catch (BadImageFormatException) { }
        }
        return refs;
    }

    [Test]
    public async Task PartialMerge_NonPartialMainDecl_StillBindsMembersAcrossTrees()
    {
        // Tree A: the "main tree" stand-in. `W` is partial (as the production wrapper will become); `C` —
        // standing in for a user top-level `static class` — is deliberately NOT partial.
        const string treeASource = """
            static partial class W
            {
                static class C
                {
                    static int F(int x) => x;
                    static int G = 1;
                }
            }
            """;
        var treeA = CSharpSyntaxTree.ParseText(treeASource, path: "A.cs");

        // Tree B: the "instances tree" stand-in, built the same way production will build it — constructed
        // nodes (SyntaxFactory), not a text round-trip — nesting a synthesized member inside a `static
        // partial class C` inside a `static partial class W`, mirroring only the nesting shape.
        var hMethod = (MethodDeclarationSyntax)
            SyntaxFactory.ParseMemberDeclaration("static int H(int x) { return F(x) + G; }")!;
        var staticPartial = SyntaxFactory.TokenList(
            SyntaxFactory.Token(SyntaxKind.StaticKeyword),
            SyntaxFactory.Token(SyntaxKind.PartialKeyword)
        );
        var classC = SyntaxFactory
            .ClassDeclaration("C")
            .WithModifiers(staticPartial)
            .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(hMethod));
        var classW = SyntaxFactory
            .ClassDeclaration("W")
            .WithModifiers(staticPartial)
            .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(classC));
        var compilationUnitB = SyntaxFactory
            .CompilationUnit()
            .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(classW));
        var treeB = CSharpSyntaxTree.Create(compilationUnitB, path: "B.cs");

        var compilation = CSharpCompilation.Create(
            "Spike",
            [treeA, treeB],
            BclReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true)
        );

        // CS0260 IS present (mismatched partial-ness across the two trees' declarations of C) — this is
        // expected, and must never gate; Koh's own diagnostics are the only ones that gate compilation.
        var diagnostics = compilation.GetDiagnostics();
        await Assert.That(diagnostics.Any(d => d.Id == "CS0260")).IsTrue();

        var modelB = compilation.GetSemanticModel(treeB);
        var hDecl = treeB
            .GetRoot()
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "H");

        // The call `F(x)` inside H (tree B) resolves to `C.F` declared in tree A.
        var callF = hDecl.DescendantNodes().OfType<InvocationExpressionSyntax>().First();
        var fSymbol = modelB.GetSymbolInfo(callF).Symbol as IMethodSymbol;
        await Assert.That(fSymbol).IsNotNull();
        await Assert.That(fSymbol!.Name).IsEqualTo("F");
        await Assert.That(fSymbol.ContainingType.Name).IsEqualTo("C");

        // The reference to `G` inside H (tree B) resolves to `C.G` declared in tree A.
        var gRef = hDecl
            .DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(i => i.Identifier.Text == "G");
        var gSymbol = modelB.GetSymbolInfo(gRef).Symbol as IFieldSymbol;
        await Assert.That(gSymbol).IsNotNull();
        await Assert.That(gSymbol!.Name).IsEqualTo("G");
        await Assert.That(gSymbol.ContainingType.Name).IsEqualTo("C");
    }
}
