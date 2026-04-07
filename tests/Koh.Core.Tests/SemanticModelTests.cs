using Koh.Core.Symbols;
using Koh.Core.Syntax;

namespace Koh.Core.Tests;

public class SemanticModelTests
{
    // --- ResolveSymbol tests ---

    [Test]
    public async Task ResolveSymbol_LocalLabel_UsesPositionScope()
    {
        var text =
            "SECTION \"Main\", ROM0\n" +
            "funcA:\n.done:\n    jr .done\n" +
            "funcB:\n.done:\n    jr .done\n";
        var tree = SyntaxTree.Parse(text);
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        int posA = text.IndexOf("jr .done");
        var symA = model.ResolveSymbol(".done", posA);
        await Assert.That(symA).IsNotNull();
        await Assert.That(symA!.Name).IsEqualTo("funcA.done");

        int posB = text.LastIndexOf("jr .done");
        var symB = model.ResolveSymbol(".done", posB);
        await Assert.That(symB).IsNotNull();
        await Assert.That(symB!.Name).IsEqualTo("funcB.done");
    }

    [Test]
    public async Task ResolveSymbol_GlobalLabel_WorksAnywhere()
    {
        var text = "SECTION \"Main\", ROM0\nmain:\n    jp main\n";
        var tree = SyntaxTree.Parse(text);
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.IndexOf("jp main");
        var sym = model.ResolveSymbol("main", pos);
        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Name).IsEqualTo("main");
    }

    [Test]
    public async Task ResolveSymbol_LocalLabelBeforeFirstGlobal_ReturnsNull()
    {
        var text = "SECTION \"Main\", ROM0\n.orphan:\n    nop\nmain:\n";
        var tree = SyntaxTree.Parse(text);
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.IndexOf("nop");
        var sym = model.ResolveSymbol(".orphan", pos);
        await Assert.That(sym).IsNull();
    }

    [Test]
    public async Task ResolveSymbol_LocalLabelBeforeDefiningGlobal_ReturnsNull()
    {
        var text = "SECTION \"Main\", ROM0\n    nop\nmain:\n.local:\n    jr .local\n";
        var tree = SyntaxTree.Parse(text);
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.IndexOf("nop");
        var sym = model.ResolveSymbol(".local", pos);
        await Assert.That(sym).IsNull();
    }

    [Test]
    public async Task LabelDeclarations_AreTopLevelChildren()
    {
        // If parser structure changes, ResolveSymbol must be updated.
        var tree = SyntaxTree.Parse("SECTION \"Main\", ROM0\nmain:\n.local:\n    nop\n");
        var labels = tree.Root.ChildNodes()
            .Where(n => n.Kind == SyntaxKind.LabelDeclaration)
            .ToList();
        await Assert.That(labels.Count).IsEqualTo(2);
    }

    // --- Existing tests ---

    [Test]
    public async Task Compilation_Create_ParsesAndBinds()
    {
        var tree = SyntaxTree.Parse("MY_CONST EQU $10\nSECTION \"Main\", ROM0\nmain:\nnop");
        var compilation = Compilation.Create(tree);
        await Assert.That(compilation.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task SemanticModel_GetDeclaredSymbol_Label()
    {
        var tree = SyntaxTree.Parse("SECTION \"Main\", ROM0\nmain:\nnop");
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);
        var label = tree.Root.ChildNodes().First(n => n.Kind == SyntaxKind.LabelDeclaration);
        var symbol = model.GetDeclaredSymbol(label);
        await Assert.That(symbol).IsNotNull();
        await Assert.That(symbol!.Name).IsEqualTo("main");
        await Assert.That(symbol.Kind).IsEqualTo(SymbolKind.Label);
    }

    [Test]
    public async Task SemanticModel_GetDeclaredSymbol_Equ()
    {
        var tree = SyntaxTree.Parse("MY_CONST EQU $42");
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);
        var directive = tree.Root.ChildNodes().First(n => n.Kind == SyntaxKind.SymbolDirective);
        var symbol = model.GetDeclaredSymbol(directive);
        await Assert.That(symbol).IsNotNull();
        await Assert.That(symbol!.Name).IsEqualTo("MY_CONST");
        await Assert.That(symbol.Value).IsEqualTo(0x42);
    }

    [Test]
    public async Task SemanticModel_LookupSymbols()
    {
        var source = "SECTION \"Main\", ROM0\nmain:\nnop\n.loop:\nnop";
        var tree = SyntaxTree.Parse(source);
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);
        // Position inside "main" scope (after "main:" declaration)
        int posInMainScope = source.IndexOf("nop");
        var symbols = model.LookupSymbols(posInMainScope).ToList();
        await Assert.That(symbols.Any(s => s.Name == "main")).IsTrue();
        await Assert.That(symbols.Any(s => s.Name == "main.loop")).IsTrue();
    }

    [Test]
    public async Task SemanticModel_GetDiagnostics()
    {
        var tree = SyntaxTree.Parse("SECTION \"Main\", ROM0\ndw NONEXISTENT");
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);
        var diags = model.GetDiagnostics();
        await Assert.That(diags).IsNotEmpty();
    }

    [Test]
    public async Task Compilation_AddSyntaxTrees()
    {
        var tree1 = SyntaxTree.Parse("SECTION \"Main\", ROM0\nmain:\nnop");
        var compilation = Compilation.Create(tree1);
        var tree2 = SyntaxTree.Parse("SECTION \"Other\", ROM0\nother:\nnop");
        var newCompilation = compilation.AddSyntaxTrees(tree2);
        await Assert.That(newCompilation.SyntaxTrees.Count).IsEqualTo(2);
        // Original unchanged
        await Assert.That(compilation.SyntaxTrees.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Compilation_ReplaceSyntaxTree()
    {
        var tree1 = SyntaxTree.Parse("SECTION \"Main\", ROM0\nmain:\nnop");
        var compilation = Compilation.Create(tree1);
        var tree2 = SyntaxTree.Parse("SECTION \"Main\", ROM0\nmain:\nhalt");
        var newCompilation = compilation.ReplaceSyntaxTree(tree1, tree2);
        // New compilation has the replacement
        await Assert.That(newCompilation.SyntaxTrees.Count).IsEqualTo(1);
        // Original unchanged
        await Assert.That(compilation.SyntaxTrees[0]).IsEqualTo(tree1);
    }

    [Test]
    public async Task Compilation_Emit()
    {
        var tree = SyntaxTree.Parse("SECTION \"Main\", ROM0\nnop\nhalt");
        var compilation = Compilation.Create(tree);
        var emit = compilation.Emit();
        await Assert.That(emit.Success).IsTrue();
        await Assert.That(emit.Sections.Count).IsEqualTo(1);
        await Assert.That(emit.Sections[0].Data[0]).IsEqualTo((byte)0x00);
        await Assert.That(emit.Sections[0].Data[1]).IsEqualTo((byte)0x76);
    }

    [Test]
    public async Task Compilation_NoDiagnostics_CleanProgram()
    {
        var tree = SyntaxTree.Parse(
            "MY_CONST EQU $10\nSECTION \"Main\", ROM0\nmain:\nld a, MY_CONST\nhalt");
        var compilation = Compilation.Create(tree);
        await Assert.That(compilation.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task SemanticModel_GetDeclaredSymbol_NonDeclaration_ReturnsNull()
    {
        var tree = SyntaxTree.Parse("SECTION \"Main\", ROM0\nnop");
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);
        var instr = tree.Root.ChildNodes().First(n => n.Kind == SyntaxKind.InstructionStatement);
        var symbol = model.GetDeclaredSymbol(instr);
        await Assert.That(symbol).IsNull();
    }

    [Test]
    public void GetSemanticModel_UnrelatedTree_Throws()
    {
        var tree1 = SyntaxTree.Parse("SECTION \"A\", ROM0\nnop");
        var tree2 = SyntaxTree.Parse("SECTION \"B\", ROM0\nnop");
        var compilation = Compilation.Create(tree1);
        Assert.Throws<ArgumentException>(() => compilation.GetSemanticModel(tree2));
    }

    [Test]
    public async Task SemanticModel_GetSymbol_LabelOperand()
    {
        var tree = SyntaxTree.Parse("MY_CONST EQU $42\nSECTION \"Main\", ROM0\nld a, MY_CONST");
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);
        // MY_CONST is parsed as LabelOperand (bare identifier, no following operator)
        var instr = tree.Root.ChildNodes().First(n => n.Kind == SyntaxKind.InstructionStatement);
        var labelOp = instr.ChildNodes().First(n => n.Kind == SyntaxKind.LabelOperand);
        var symbol = model.GetSymbol(labelOp);
        await Assert.That(symbol).IsNotNull();
        await Assert.That(symbol!.Name).IsEqualTo("MY_CONST");
        await Assert.That(symbol.Value).IsEqualTo(0x42);
    }

    [Test]
    public async Task SemanticModel_GetSymbol_NonReference_ReturnsNull()
    {
        var tree = SyntaxTree.Parse("SECTION \"Main\", ROM0\nnop");
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);
        var instr = tree.Root.ChildNodes().First(n => n.Kind == SyntaxKind.InstructionStatement);
        var symbol = model.GetSymbol(instr);
        await Assert.That(symbol).IsNull();
    }

    [Test]
    public async Task Compilation_Diagnostics_Cached()
    {
        var tree = SyntaxTree.Parse("SECTION \"Main\", ROM0\nnop");
        var compilation = Compilation.Create(tree);
        var diags1 = compilation.Diagnostics;
        var diags2 = compilation.Diagnostics;
        await Assert.That(diags1.Count).IsEqualTo(diags2.Count);
    }

    [Test]
    public async Task Compilation_Emit_Cached()
    {
        var tree = SyntaxTree.Parse("SECTION \"Main\", ROM0\nnop");
        var compilation = Compilation.Create(tree);
        var emit1 = compilation.Emit();
        var emit2 = compilation.Emit();
        await Assert.That(ReferenceEquals(emit1, emit2)).IsTrue();
    }

    [Test]
    public async Task Compilation_ReplaceSyntaxTree_NewTreeUsed()
    {
        var tree1 = SyntaxTree.Parse("SECTION \"Main\", ROM0\nmain:\nnop");
        var compilation = Compilation.Create(tree1);
        var tree2 = SyntaxTree.Parse("SECTION \"Main\", ROM0\nmain:\nhalt");
        var newCompilation = compilation.ReplaceSyntaxTree(tree1, tree2);
        await Assert.That(ReferenceEquals(newCompilation.SyntaxTrees[0], tree2)).IsTrue();
    }

    [Test]
    public async Task Compilation_Create_NoTrees()
    {
        var compilation = Compilation.Create();
        await Assert.That(compilation.SyntaxTrees.Count).IsEqualTo(0);
        await Assert.That(compilation.Diagnostics).IsEmpty();
    }
}
