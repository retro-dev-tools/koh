using Koh.Compiler.Frontends.CSharp;
using Koh.Compiler.Ir;
using Koh.Core.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Koh.Compiler.Tests;

/// <summary>Phase 2 of the semantic-model migration: the declaration passes (<c>CSharpFrontend.cs</c>
/// Pass 1/1.5, <c>CSharpFrontend.Declarations.cs</c>) register every declaration into
/// <see cref="CSharpSemantics"/>'s symbol-keyed indexes alongside the existing string-keyed tables.
/// Nothing consumes the indexes yet — these tests only assert their contents, via the real declaration
/// passes (<see cref="CSharpFrontend.LowerForTest"/>), not a hand-rolled reimplementation.</summary>
public class CSharpSemanticsIndexTests
{
    private static (IrModule Module, CSharpSemantics Semantics) Build(string source)
    {
        var diagnostics = new DiagnosticBag();
        var result = CSharpFrontend.LowerForTest(source, diagnostics);
        return !diagnostics.Any()
            ? result
            : throw new InvalidOperationException(
                "unexpected diagnostics: " + string.Join("; ", diagnostics.Select(d => d.Message))
            );
    }

    /// <summary>The main (non-stub) syntax tree the declaration passes ran over, so a test can find the
    /// declaration node whose registered symbol it wants to look up.</summary>
    private static CompilationUnitSyntax MainRoot(CSharpSemantics semantics) =>
        semantics
            .Compilation!.SyntaxTrees.First(t => t != IntrinsicsStub.Tree)
            .GetCompilationUnitRoot();

    // ---- One index per declaration kind --------------------------------------------------------------

    [Test]
    public async Task EachDeclarationKind_RegistersUnderItsOwnSymbol()
    {
        const string src = """
            enum Color : byte { Red, Green, Blue }

            struct Point
            {
                public byte X;
                public byte Y;
            }

            class Node
            {
                public byte Value;
            }

            static byte Score;
            const byte MaxScore = 99;

            static class Board
            {
                public static byte Get() => 5;
            }

            static byte TopLevelFn() => 1;
            """;
        var (module, semantics) = Build(src);
        var root = MainRoot(semantics);

        // Enum
        var enumDecl = root.DescendantNodes()
            .OfType<EnumDeclarationSyntax>()
            .First(e => e.Identifier.Text == "Color");
        var enumSymbol = semantics.DeclaredSym(enumDecl);
        await Assert.That(enumSymbol).IsTypeOf<INamedTypeSymbol>();
        await Assert.That(semantics.Enums.ContainsKey((INamedTypeSymbol)enumSymbol!)).IsTrue();
        var csEnum = semantics.Enums[(INamedTypeSymbol)enumSymbol!];
        await Assert.That(csEnum.Members["Red"]).IsEqualTo(0L);
        await Assert.That(csEnum.Members["Green"]).IsEqualTo(1L);
        await Assert.That(csEnum.Members["Blue"]).IsEqualTo(2L);

        // Struct
        var structDecl = root.DescendantNodes()
            .OfType<StructDeclarationSyntax>()
            .First(s => s.Identifier.Text == "Point");
        var structSymbol = semantics.DeclaredSym(structDecl);
        await Assert.That(structSymbol).IsTypeOf<INamedTypeSymbol>();
        await Assert.That(semantics.Structs.ContainsKey((INamedTypeSymbol)structSymbol!)).IsTrue();
        await Assert
            .That(semantics.Structs[(INamedTypeSymbol)structSymbol!].Fields.Count)
            .IsEqualTo(2);

        // Class
        var classDecl = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .First(c => c.Identifier.Text == "Node");
        var classSymbol = semantics.DeclaredSym(classDecl);
        await Assert.That(classSymbol).IsTypeOf<INamedTypeSymbol>();
        await Assert.That(semantics.Classes.ContainsKey((INamedTypeSymbol)classSymbol!)).IsTrue();
        await Assert.That(semantics.Classes[(INamedTypeSymbol)classSymbol!].Name).IsEqualTo("Node");

        // Static field (global)
        var scoreDecl = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .First(v => v.Identifier.Text == "Score");
        var scoreSymbol = semantics.DeclaredSym(scoreDecl);
        await Assert.That(scoreSymbol).IsTypeOf<IFieldSymbol>();
        await Assert.That(semantics.Globals.ContainsKey((IFieldSymbol)scoreSymbol!)).IsTrue();
        await Assert
            .That(semantics.Globals[(IFieldSymbol)scoreSymbol!].Global.Name)
            .IsEqualTo("Score");

        // Module const
        var maxScoreDecl = root.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .First(v => v.Identifier.Text == "MaxScore");
        var maxScoreSymbol = semantics.DeclaredSym(maxScoreDecl);
        await Assert.That(maxScoreSymbol).IsTypeOf<IFieldSymbol>();
        await Assert
            .That(semantics.ModuleConsts.ContainsKey((IFieldSymbol)maxScoreSymbol!))
            .IsTrue();
        await Assert
            .That(semantics.ModuleConsts[(IFieldSymbol)maxScoreSymbol!].Value)
            .IsEqualTo(99L);

        // Top-level method
        var topFnDecl = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "TopLevelFn");
        var topFnSymbol = semantics.DeclaredSym(topFnDecl);
        await Assert.That(topFnSymbol).IsTypeOf<IMethodSymbol>();
        await Assert.That(semantics.Methods.ContainsKey((IMethodSymbol)topFnSymbol!)).IsTrue();
        await Assert
            .That(semantics.Methods[(IMethodSymbol)topFnSymbol!].Fn.Name)
            .IsEqualTo("TopLevelFn");

        // Static-class method (program-scope name qualified by its class)
        var boardGetDecl = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "Get");
        var boardGetSymbol = semantics.DeclaredSym(boardGetDecl);
        await Assert.That(boardGetSymbol).IsTypeOf<IMethodSymbol>();
        await Assert.That(semantics.Methods.ContainsKey((IMethodSymbol)boardGetSymbol!)).IsTrue();
        await Assert
            .That(semantics.Methods[(IMethodSymbol)boardGetSymbol!].Fn.Name)
            .IsEqualTo("Board.Get");

        // Sanity: the module actually contains both functions (the index isn't the only place they live).
        await Assert.That(module.Functions.Any(f => f.Name == "TopLevelFn")).IsTrue();
        await Assert.That(module.Functions.Any(f => f.Name == "Board.Get")).IsTrue();
    }

    // ---- Instance methods (Pass 1.5) -----------------------------------------------------------------

    [Test]
    public async Task InstanceMethod_RegistersUnderItsOwnSymbol()
    {
        const string src = """
            class Counter
            {
                public byte Value;
                public byte Get() => Value;
            }

            static byte UseIt()
            {
                Counter c = new Counter();
                return c.Get();
            }
            """;
        var (_, semantics) = Build(src);
        var root = MainRoot(semantics);

        var getDecl = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "Get");
        var getSymbol = semantics.DeclaredSym(getDecl);
        await Assert.That(getSymbol).IsTypeOf<IMethodSymbol>();
        await Assert.That(semantics.Methods.ContainsKey((IMethodSymbol)getSymbol!)).IsTrue();
        await Assert
            .That(semantics.Methods[(IMethodSymbol)getSymbol!].Fn.Name)
            .IsEqualTo("Counter.Get");
        await Assert.That(semantics.Methods[(IMethodSymbol)getSymbol!].ThisClass).IsNotNull();
    }

    // ---- Monomorphized generic instances are skipped, not errored ------------------------------------

    [Test]
    public async Task GenericMethodInstance_IsSkippedWithoutError()
    {
        const string src = """
            static T Identity<T>(T x) => x;

            static byte UseIt() => Identity<byte>(5);
            """;
        var (module, semantics) = Build(src);

        // The specialization really was lowered (a normal IR function exists for it) ...
        await Assert.That(module.Functions.Any(f => f.Name.StartsWith("Identity__g"))).IsTrue();
        // ... but its declaration node is detached from the main tree, so DeclaredSym returns null for it
        // and Materialize silently drops it from the symbol-keyed index — no throw, no bogus entry.
        await Assert
            .That(semantics.Methods.Values.Any(m => m.Fn.Name.StartsWith("Identity")))
            .IsFalse();

        // An ordinary sibling method in the same program still indexes normally.
        var root = MainRoot(semantics);
        var useItDecl = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == "UseIt");
        var useItSymbol = semantics.DeclaredSym(useItDecl);
        await Assert.That(useItSymbol).IsTypeOf<IMethodSymbol>();
        await Assert.That(semantics.Methods.ContainsKey((IMethodSymbol)useItSymbol!)).IsTrue();
    }

    // ---- Disabled ignores registrations ---------------------------------------------------------------

    [Test]
    public async Task Disabled_RegistrationsAreNoOpsAndIndexesStayEmpty()
    {
        var disabled = CSharpSemantics.Disabled;
        var foreignTree = CSharpSyntaxTree.ParseText(
            """
            enum E { A }
            struct S { public byte F; }
            class C { public byte F; }
            class Program { static byte M() => 1; static byte G; }
            """
        );
        var foreignRoot = foreignTree.GetCompilationUnitRoot();
        var enumDecl = foreignRoot.DescendantNodes().OfType<EnumDeclarationSyntax>().First();
        var structDecl = foreignRoot.DescendantNodes().OfType<StructDeclarationSyntax>().First();
        var classDecl = foreignRoot
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .First(c => c.Identifier.Text == "C");
        var methodDecl = foreignRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var fieldDecl = foreignRoot.DescendantNodes().OfType<VariableDeclaratorSyntax>().First();

        var dummyFn = new IrFunction("Program.M", IrType.I8, []);
        var dummyMethod = new CsMethod(dummyFn, CsType.U8, [], [], []);
        var dummyGlobal = new IrGlobal("G", IrType.I8, Koh.Compiler.Targets.AddressSpace.Wram);

        disabled.RegisterMethod(methodDecl, dummyMethod);
        disabled.RegisterGlobal(fieldDecl, dummyGlobal, CsType.U8);
        disabled.RegisterConst(fieldDecl, CsType.U8, 42);
        disabled.RegisterEnum(
            enumDecl,
            new CsEnum(CsType.U8, new Dictionary<string, long> { ["A"] = 0 })
        );
        disabled.RegisterStruct(structDecl, new CsStruct([], 0));
        disabled.RegisterClass(
            classDecl,
            new CsClass("C", new CsStruct([], 1), new Dictionary<string, MethodDeclarationSyntax>())
        );

        await Assert.That(disabled.Methods).IsEmpty();
        await Assert.That(disabled.Globals).IsEmpty();
        await Assert.That(disabled.ModuleConsts).IsEmpty();
        await Assert.That(disabled.Enums).IsEmpty();
        await Assert.That(disabled.Structs).IsEmpty();
        await Assert.That(disabled.Classes).IsEmpty();
    }
}
