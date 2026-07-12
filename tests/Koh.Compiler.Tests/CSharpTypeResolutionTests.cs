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

/// <summary>Stage-2 P6 of the symbol-only-resolution migration: type-NAME resolution
/// (<c>CSharpFrontend.ResolveType</c>/<c>ResolveTypeAllowingClass</c>/<c>ConstEval</c>'s <c>Enum.Member</c>
/// arm, in <c>CSharpFrontend.Types.cs</c>) is now symbol-first too, the last string-keyed holdout after
/// Stage-2 P5 deleted every other resolution fallback. A user enum/struct/class type name resolves via its
/// own Roslyn symbol into <see cref="CSharpSemantics.Enums"/>/<see cref="CSharpSemantics.Structs"/>/
/// <see cref="CSharpSemantics.Classes"/> — the very same <c>CsEnum</c>/<c>CsStruct</c>/<c>CsClass</c>
/// instances the (now declaration-plumbing-only) string tables held. The one genuine hazard: those indexes
/// are lazily materialized from a registration list that fills in as <c>CollectEnums</c>/<c>CollectClasses</c>
/// run, so reading one before every relevant declaration is registered would freeze it incomplete forever —
/// <c>ConstEval</c>'s <c>Enum.Member</c> arm (consulted from <c>CollectEnums</c> itself, for a member
/// initializer referencing another enum) and <c>ResolveTypeAllowingClass</c> (consulted from
/// <c>CollectClasses</c> itself, for a self-/forward-referencing class field) both avoid it during their own
/// collection pass — see <c>SelfReferentialEnumWithCustomBase</c>/<c>CrossEnumReference</c> below, which pin
/// exactly that hazard.</summary>
public class CSharpTypeResolutionTests
{
    // ---- End-to-end harness (mirrors CSharpCandidateResolutionTests/CSharpGenericRoutingTests) ---------

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

    // ---- Direct-assertion harness: locate a node in the real production main/instances tree -----------

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

    private static CompilationUnitSyntax MainRoot(CSharpSemantics semantics) =>
        semantics
            .Compilation!.SyntaxTrees.First(t =>
                t != IntrinsicsStub.Tree && t.FilePath != "__KohGenericInstances.cs"
            )
            .GetCompilationUnitRoot();

    private static CompilationUnitSyntax InstancesRoot(CSharpSemantics semantics) =>
        semantics
            .Compilation!.SyntaxTrees.First(t => t.FilePath == "__KohGenericInstances.cs")
            .GetCompilationUnitRoot();

    // ---- Enum-typed local: resolves via symbol, through CSharpSemantics.Enums -----------------------

    [Test]
    public async Task EnumTypedLocal_ComputesCorrectly()
    {
        const string src = """
            enum Dir : byte { Up, Down, Left, Right }
            static byte Main() {
              Dir d = Dir.Left;
              return (byte)d;
            }
            """;
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)2);
    }

    [Test]
    public async Task EnumTypedLocal_TypeNode_ResolvesThroughEnumsIndex()
    {
        const string src = """
            enum Dir : byte { Up, Down, Left, Right }
            static byte Main() {
              Dir d = Dir.Left;
              return (byte)d;
            }
            """;
        var (_, semantics) = Build(src);
        var root = MainRoot(semantics);

        // The local's own type-name node (`Dir` in `Dir d = ...`) resolves to the enum's symbol, and that
        // symbol is a real key in CSharpSemantics.Enums — proving ResolveType went through the symbol-first
        // path, not merely that the program still lowered (a coincidental text match would too).
        var localDecl = root.DescendantNodes()
            .OfType<VariableDeclarationSyntax>()
            .First(d => d.Type is IdentifierNameSyntax { Identifier.Text: "Dir" });
        var typeSym = semantics.Sym(localDecl.Type) as INamedTypeSymbol;
        await Assert.That(typeSym).IsNotNull();
        await Assert.That(semantics.Enums.ContainsKey(typeSym!)).IsTrue();
        await Assert.That(semantics.Enums[typeSym!].Members["Left"]).IsEqualTo(2L);
    }

    // ---- Struct-typed local: resolves via symbol, through CSharpSemantics.Structs --------------------

    [Test]
    public async Task StructTypedLocal_ComputesCorrectly()
    {
        const string src = """
            struct Point { public byte X; public byte Y; }
            static byte Main() {
              Point p;
              p.X = 3;
              p.Y = 4;
              return (byte)(p.X + p.Y);
            }
            """;
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)7);
    }

    [Test]
    public async Task StructTypedLocal_TypeNode_ResolvesThroughStructsIndex()
    {
        const string src = """
            struct Point { public byte X; public byte Y; }
            static byte Main() {
              Point p;
              p.X = 3;
              p.Y = 4;
              return (byte)(p.X + p.Y);
            }
            """;
        var (_, semantics) = Build(src);
        var root = MainRoot(semantics);
        var localDecl = root.DescendantNodes()
            .OfType<VariableDeclarationSyntax>()
            .First(d => d.Type is IdentifierNameSyntax { Identifier.Text: "Point" });
        var typeSym = semantics.Sym(localDecl.Type) as INamedTypeSymbol;
        await Assert.That(typeSym).IsNotNull();
        await Assert.That(semantics.Structs.ContainsKey(typeSym!)).IsTrue();
        await Assert.That(semantics.Structs[typeSym!].Fields.Count).IsEqualTo(2);
    }

    // ---- Class-typed local and `new`: resolve via symbol, through CSharpSemantics.Classes -------------

    [Test]
    public async Task ClassTypedLocalAndNew_ComputeCorrectly()
    {
        const string src = """
            class Counter { public byte Value; }
            static byte Main() {
              Counter c = new Counter();
              c.Value = 9;
              return c.Value;
            }
            """;
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)9);
    }

    [Test]
    public async Task ClassTypedLocalAndNew_TypeNodes_ResolveThroughClassesIndex()
    {
        const string src = """
            class Counter { public byte Value; }
            static byte Main() {
              Counter c = new Counter();
              c.Value = 9;
              return c.Value;
            }
            """;
        var (_, semantics) = Build(src);
        var root = MainRoot(semantics);

        var localDecl = root.DescendantNodes()
            .OfType<VariableDeclarationSyntax>()
            .First(d => d.Type is IdentifierNameSyntax { Identifier.Text: "Counter" });
        var localSym = semantics.Sym(localDecl.Type) as INamedTypeSymbol;
        await Assert.That(localSym).IsNotNull();
        await Assert.That(semantics.Classes.ContainsKey(localSym!)).IsTrue();

        var objCreation = root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().First();
        var newSym = semantics.Sym(objCreation.Type) as INamedTypeSymbol;
        await Assert.That(newSym).IsNotNull();
        await Assert.That(semantics.Classes.ContainsKey(newSym!)).IsTrue();
        await Assert.That(semantics.Classes[newSym!].Name).IsEqualTo("Counter");
    }

    // ---- Int128/UInt128: still resolve (symbol-first via metadata name, per CSharpFrontend.IsBigIntName) -

    [Test]
    public async Task Int128Local_StillResolvesAndComputes()
    {
        const string src = """
            static byte Main() {
              Int128 a = 5;
              Int128 b = 7;
              Int128 c = a + b;
              return (byte)c;
            }
            """;
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)12);
    }

    [Test]
    public async Task UInt128Local_StillResolvesAndComputes()
    {
        const string src = """
            static byte Main() {
              UInt128 a = 200;
              UInt128 b = 55;
              UInt128 c = a + b;
              return (byte)c;
            }
            """;
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)255);
    }

    // ---- The ConstEval ordering hazard: self-referential enum members with a custom base ---------------

    [Test]
    public async Task SelfReferentialEnumWithCustomBase_StillCollectsCorrectly()
    {
        // `B = A + 1` is a bare-name self-reference within the SAME enum still being collected — this
        // must keep working via the `lookup` delegate (unaffected by Stage-2 P6), and the enum's custom
        // `ushort` base must still resolve (ResolveType's predefined-keyword fast path, no symbol needed).
        const string src = """
            enum E : ushort { A = 1, B = A + 1 }
            static ushort Main() {
              return (ushort)E.B;
            }
            """;
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        var gb = Load(Compile(src), out int s, out int l);
        Run(gb, s, l);
        await Assert.That(gb.Registers.HL).IsEqualTo((ushort)2);
    }

    [Test]
    public async Task CrossEnumReference_DuringCollection_StillResolvesCorrectly()
    {
        // B's member initializer references A.X — a DIFFERENT, earlier-declared enum — while CollectEnums
        // is still iterating (A is fully collected and registered into CSharpSemantics.Enums by the time
        // B is processed, but B itself is not yet, and no later enum in the file is either). ConstEval's
        // Enum.Member arm must resolve this via the safe (semantics: null) string-dict path, not by
        // forcing CSharpSemantics.Enums to materialize mid-collection (which would freeze it incomplete —
        // see the class remarks and CSharpFrontend.Types.cs's TryEnumMember).
        const string src = """
            enum A : byte { X = 5 }
            enum B : byte { Y = A.X + 1 }
            static byte Main() {
              return (byte)B.Y;
            }
            """;
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)6);
    }

    [Test]
    public async Task CrossEnumReference_DuringCollection_DoesNotFreezeEnumsIndexIncomplete()
    {
        // Direct proof the hazard is actually avoided, not just that the program happens to still lower:
        // after a full Build (which necessarily runs CollectEnums, including B's cross-enum reference to
        // A), CSharpSemantics.Enums must still contain BOTH enums — if ConstEval's Enum.Member arm had
        // forced the index to materialize mid-CollectEnums, whichever enum(s) were registered after that
        // point would be silently missing here.
        const string src = """
            enum A : byte { X = 5 }
            enum B : byte { Y = A.X + 1 }
            static byte Main() {
              return (byte)B.Y;
            }
            """;
        var (_, semantics) = Build(src);
        var root = MainRoot(semantics);

        var aDecl = root.DescendantNodes()
            .OfType<EnumDeclarationSyntax>()
            .First(e => e.Identifier.Text == "A");
        var bDecl = root.DescendantNodes()
            .OfType<EnumDeclarationSyntax>()
            .First(e => e.Identifier.Text == "B");
        var aSym = semantics.DeclaredSym(aDecl) as INamedTypeSymbol;
        var bSym = semantics.DeclaredSym(bDecl) as INamedTypeSymbol;
        await Assert.That(aSym).IsNotNull();
        await Assert.That(bSym).IsNotNull();
        await Assert.That(semantics.Enums.ContainsKey(aSym!)).IsTrue();
        await Assert.That(semantics.Enums.ContainsKey(bSym!)).IsTrue();
        await Assert.That(semantics.Enums[bSym!].Members["Y"]).IsEqualTo(6L);
    }

    // ---- The analogous class hazard: a self-/forward-referencing class field -------------------------

    [Test]
    public async Task SelfReferencingClassField_StillCollectsCorrectly()
    {
        // `Node Next;` inside `Node` itself references the class currently being laid out by CollectClasses
        // — ResolveTypeAllowingClass must use the safe `classNames` text check there (classIndexSafe:
        // false), not CSharpSemantics.Classes (not yet registered for Node at that point).
        const string src = """
            class Node { public byte Value; public Node Next; }
            static byte Main() {
              Node a = new Node();
              Node b = new Node();
              a.Value = 1;
              b.Value = 2;
              a.Next = b;
              Node n = a.Next;
              return n.Value;
            }
            """;
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)2);
    }

    // ---- Generic instance bodies: type nodes live in the instances tree, not the main tree -------------

    [Test]
    public async Task GenericInstanceBody_EnumTypedLocal_ComputesCorrectly()
    {
        const string src = """
            enum Dir : byte { Up, Down, Left, Right }
            static byte Pick<T>(byte extra) {
              Dir d = Dir.Right;
              return (byte)(extra + (byte)d);
            }
            static byte Main() {
              return Pick<byte>(1);
            }
            """;
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)4);
    }

    [Test]
    public async Task GenericInstanceBody_EnumTypedLocal_ResolvesInInstancesTree()
    {
        const string src = """
            enum Dir : byte { Up, Down, Left, Right }
            static byte Pick<T>(byte extra) {
              Dir d = Dir.Right;
              return (byte)(extra + (byte)d);
            }
            static byte Main() {
              return Pick<byte>(1);
            }
            """;
        var (_, semantics) = Build(src);
        var instancesRoot = InstancesRoot(semantics);
        var instanceDecl = instancesRoot
            .DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text.StartsWith("Pick__g"));

        var localDecl = instanceDecl
            .DescendantNodes()
            .OfType<VariableDeclarationSyntax>()
            .First(d => d.Type is IdentifierNameSyntax { Identifier.Text: "Dir" });
        // The type node lives in the instances tree, not the main tree — Sym must still resolve it there
        // (CSharpSemantics.InTree/ModelFor cover both trees), proving symbol-first type resolution works
        // for a monomorphized instance's own body, not just ordinary top-level code.
        var typeSym = semantics.Sym(localDecl.Type) as INamedTypeSymbol;
        await Assert.That(typeSym).IsNotNull();
        await Assert.That(semantics.Enums.ContainsKey(typeSym!)).IsTrue();
    }

    [Test]
    public async Task GenericInstanceBody_Cast_ComputesCorrectly()
    {
        // A cast inside a generic instance body (ResolveType consulted from MethodLowerer's cast site) —
        // the cast's own type node also lives in the instances tree.
        const string src = """
            static byte Widen<T>(byte x) {
              ushort w = (ushort)x;
              return (byte)(w + 1);
            }
            static byte Main() {
              return Widen<byte>(41);
            }
            """;
        await Assert.That(LowerDiagnostics(src)).IsEmpty();
        await Assert.That(RunA(src)).IsEqualTo((byte)42);
    }
}
