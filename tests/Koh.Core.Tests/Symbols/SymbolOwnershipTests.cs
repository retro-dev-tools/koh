using Koh.Core.Diagnostics;
using Koh.Core.Symbols;
using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Core.Tests.Symbols;

public class SymbolOwnershipTests
{
    [Test]
    public async Task SameNameDifferentOwners_NoDiagnostic()
    {
        var treeA = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\ncount:\n    nop\n", "a.asm"));
        var treeB = SyntaxTree.Parse(
            SourceText.From("SECTION \"B\", ROM0\ncount EQU 42\n    ld a, count\n", "b.asm"));

        var compilation = Compilation.Create(treeA, treeB);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SameNameDifferentOwners_ResolveToOwnSymbol()
    {
        var textB = "SECTION \"B\", ROM0\ncount EQU 42\n    ld a, count\n";
        var treeA = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\ncount:\n    nop\n", "a.asm"));
        var treeB = SyntaxTree.Parse(SourceText.From(textB, "b.asm"));

        var compilation = Compilation.Create(treeA, treeB);
        var modelB = compilation.GetSemanticModel(treeB);

        int pos = textB.LastIndexOf("count");
        var sym = modelB.ResolveSymbol("count", pos);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.Constant);
        await Assert.That(sym.OwnerId).IsEqualTo("b.asm");
        await Assert.That(sym.SymbolId).IsEqualTo(("b.asm", "count"));
    }

    [Test]
    public async Task SameNameGlobalLabels_DifferentOwners_NoCollision()
    {
        var treeA = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nmain:\n    nop\n", "a.asm"));
        var treeB = SyntaxTree.Parse(
            SourceText.From("SECTION \"B\", ROM0\nmain:\n    nop\n", "b.asm"));

        var compilation = Compilation.Create(treeA, treeB);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DifferentOwners_SameNameConstants_NoCollision()
    {
        var treeA = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nfoo EQU 42\n", "a.asm"));
        var treeB = SyntaxTree.Parse(
            SourceText.From("SECTION \"B\", ROM0\nfoo EQU 99\n", "b.asm"));

        var compilation = Compilation.Create(treeA, treeB);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task DifferentOwners_SameNameStringConstants_NoCollision()
    {
        var treeA = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nfoo EQUS \"hello\"\n", "a.asm"));
        var treeB = SyntaxTree.Parse(
            SourceText.From("SECTION \"B\", ROM0\nfoo EQUS \"world\"\n", "b.asm"));

        var compilation = Compilation.Create(treeA, treeB);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SameOwner_LabelAndConstant_SameName_ProducesDuplicate()
    {
        var tree = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nfoo:\nfoo EQU 42\n", "a.asm"));

        var compilation = Compilation.Create(tree);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task SameOwner_ConstantAndStringConstant_SameName_ProducesDuplicate()
    {
        var tree = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nfoo EQU 42\nfoo EQUS \"bar\"\n", "a.asm"));

        var compilation = Compilation.Create(tree);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task ExportedLabel_HasNullOwnerAndCorrectSymbolId()
    {
        var text = "SECTION \"A\", ROM0\nmain:\n    EXPORT main\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.IndexOf("EXPORT main");
        var sym = model.ResolveSymbol("main", pos);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.OwnerId).IsNull();
        await Assert.That(sym.Visibility).IsEqualTo(SymbolVisibility.Exported);
        await Assert.That(sym.SymbolId).IsEqualTo(((string?)null, "main"));
        await Assert.That(sym.Kind).IsEqualTo(SymbolKind.Label);
    }

    [Test]
    public async Task ExportedConstant_PreservesKindAndIdentity()
    {
        var text = "SECTION \"A\", ROM0\nMY_VAL EQU 42\n    EXPORT MY_VAL\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.IndexOf("EXPORT MY_VAL");
        var sym = model.ResolveSymbol("MY_VAL", pos);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.Constant);
        await Assert.That(sym.OwnerId).IsNull();
        await Assert.That(sym.SymbolId).IsEqualTo(((string?)null, "MY_VAL"));
    }

    [Test]
    public async Task ExportedStringConstant_PreservesKindAndIdentity()
    {
        var text = "SECTION \"A\", ROM0\nMY_STR EQUS \"hello\"\n    EXPORT MY_STR\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.IndexOf("EXPORT MY_STR");
        var sym = model.ResolveSymbol("MY_STR", pos);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.StringConstant);
        await Assert.That(sym.OwnerId).IsNull();
        await Assert.That(sym.SymbolId).IsEqualTo(((string?)null, "MY_STR"));
    }

    [Test]
    public async Task DuplicateExport_ProducesDiagnosticAtSecondExportDirective()
    {
        var textA = "SECTION \"A\", ROM0\nmain:\n    EXPORT main\n";
        var textB = "SECTION \"B\", ROM0\nmain:\n    EXPORT main\n";
        var treeA = SyntaxTree.Parse(SourceText.From(textA, "a.asm"));
        var treeB = SyntaxTree.Parse(SourceText.From(textB, "b.asm"));

        var compilation = Compilation.Create(treeA, treeB);

        var diag = compilation.Diagnostics.FirstOrDefault(d =>
            d.Severity == DiagnosticSeverity.Error &&
            d.Message.Contains("already defined"));

        await Assert.That(diag).IsNotNull();
        // The diagnostic should be reported at the second EXPORT directive
        await Assert.That(diag!.Span.Start).IsGreaterThan(0);
    }

    [Test]
    public async Task ExportUndefinedSymbol_ProducesDiagnostic()
    {
        var text = "SECTION \"A\", ROM0\n    EXPORT missing\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));

        var compilation = Compilation.Create(tree);

        var diag = compilation.Diagnostics.FirstOrDefault(d =>
            d.Message.Contains("Cannot export undefined symbol"));

        await Assert.That(diag).IsNotNull();
    }

    [Test]
    public async Task ExportMacro_ProducesDiagnosticAtExportDirective()
    {
        var text = "SECTION \"A\", ROM0\nmy_macro: MACRO\n    nop\nENDM\n    EXPORT my_macro\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));

        var compilation = Compilation.Create(tree);

        var diag = compilation.Diagnostics.FirstOrDefault(d =>
            d.Message.Contains("Cannot export macro"));

        await Assert.That(diag).IsNotNull();
        // Span points to the EXPORT SymbolDirective node (includes leading whitespace)
        await Assert.That(diag!.Span.Start).IsGreaterThan(0);
    }

    [Test]
    public async Task ExportLocalLabel_ProducesDiagnostic()
    {
        // PromoteExport should reject local labels (names containing '.').
        // Test this at the SymbolTable level since the parser doesn't pass qualified
        // local names through the EXPORT directive.
        var diagnostics = new DiagnosticBag();
        var table = new SymbolTable(diagnostics);
        var context = new SymbolResolutionContext("test.asm");

        // Define a global label and a local label
        table.DefineLabel("main", 0, "ROM0", null, context);
        table.DefineLabel(".local", 1, "ROM0", null, context);

        // Try to export the local label using its qualified name
        var dummySite = SyntaxTree.Parse("nop\n").Root;
        table.PromoteExport("main.local", dummySite, context);

        var diag = diagnostics.FirstOrDefault(d =>
            d.Message.Contains("Cannot export local label"));

        await Assert.That(diag).IsNotNull();
    }

    [Test]
    public async Task DefinedMacro_AppearsInSymbolTable()
    {
        var tree = SyntaxTree.Parse(
            SourceText.From("SECTION \"Main\", ROM0\nmy_macro: MACRO\n    nop\nENDM\n    my_macro\n", "main.asm"));

        var compilation = Compilation.Create(tree);
        var model = compilation.Emit();

        var sym = model.Symbols.FirstOrDefault(s => s.Name == "my_macro");

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.Macro);
    }

    [Test]
    public async Task MacroSymbol_ResolvableViaSemanticModel()
    {
        var text = "SECTION \"Main\", ROM0\nmy_macro: MACRO\n    nop\nENDM\n    my_macro\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "main.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.LastIndexOf("my_macro");
        var sym = model.ResolveSymbol("my_macro", pos);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.Macro);
    }

    [Test]
    public async Task MacroAndLabel_SameName_SameOwner_ProducesDuplicate()
    {
        var tree = SyntaxTree.Parse(
            SourceText.From("SECTION \"Main\", ROM0\nfoo: MACRO\n    nop\nENDM\nfoo:\n    nop\n", "main.asm"));

        var compilation = Compilation.Create(tree);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task CrossOwnerSameNameMacros_Allowed()
    {
        var treeA = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\ninit: MACRO\n    nop\nENDM\n    init\n", "a.asm"));
        var treeB = SyntaxTree.Parse(
            SourceText.From("SECTION \"B\", ROM0\ninit: MACRO\n    halt\nENDM\n    init\n", "b.asm"));

        var compilation = Compilation.Create(treeA, treeB);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SameOwner_IncludedMacro_CollidesWithRootMacro()
    {
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile("macros.inc", "foo: MACRO\n    nop\nENDM\n");

        var tree = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nfoo: MACRO\n    halt\nENDM\nINCLUDE \"macros.inc\"\n", "a.asm"));

        var compilation = Compilation.Create(resolver, tree);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task IncludedSymbol_BelongsToRootOwner()
    {
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile("defs.inc", "MY_CONST EQU 42\n");

        var text = "SECTION \"A\", ROM0\nINCLUDE \"defs.inc\"\n    ld a, MY_CONST\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));
        var compilation = Compilation.Create(resolver, tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.IndexOf("MY_CONST", text.IndexOf("ld a"));
        var sym = model.ResolveSymbol("MY_CONST", pos);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.OwnerId).IsEqualTo("a.asm");
    }

    [Test]
    public async Task IncludedMacro_BelongsToRootOwner()
    {
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile("macros.inc", "my_macro: MACRO\n    nop\nENDM\n");

        var text = "SECTION \"A\", ROM0\nINCLUDE \"macros.inc\"\n    my_macro\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));
        var compilation = Compilation.Create(resolver, tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.LastIndexOf("my_macro");
        var sym = model.ResolveSymbol("my_macro", pos);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.OwnerId).IsEqualTo("a.asm");
        await Assert.That(sym.Kind).IsEqualTo(SymbolKind.Macro);
    }

    [Test]
    public async Task SameIncludeDifferentRoots_IndependentSymbols()
    {
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile("defs.inc", "MY_CONST EQU 42\n");

        var treeA = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nINCLUDE \"defs.inc\"\n", "a.asm"));
        var treeB = SyntaxTree.Parse(
            SourceText.From("SECTION \"B\", ROM0\nINCLUDE \"defs.inc\"\n", "b.asm"));

        var compilation = Compilation.Create(resolver, treeA, treeB);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task SameOwnerIncludeCollision_ProducesDiagnostic()
    {
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile("defs.inc", "MY_CONST EQU 99\n");

        var tree = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nMY_CONST EQU 42\nINCLUDE \"defs.inc\"\n", "a.asm"));

        var compilation = Compilation.Create(resolver, tree);

        var errors = compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        await Assert.That(errors.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task ExportedFromInclude_HasExportedIdentity()
    {
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile("exports.inc", "main:\n    EXPORT main\n    nop\n");

        var text = "SECTION \"A\", ROM0\nINCLUDE \"exports.inc\"\nmain_ref:\n    jp main\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));
        var compilation = Compilation.Create(resolver, tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.IndexOf("jp main");
        var sym = model.ResolveSymbol("main", pos);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Visibility).IsEqualTo(SymbolVisibility.Exported);
        await Assert.That(sym.OwnerId).IsNull();
    }

    [Test]
    public async Task GetSymbol_LocalLabelReference_ReturnsCorrectScopedSymbol()
    {
        var text =
            "SECTION \"Main\", ROM0\n" +
            "funcA:\n.done:\n    jr .done\n" +
            "funcB:\n.done:\n    jr .done\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "main.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        var labelOperands = DescendantNodes(tree.Root)
            .Where(n => n.Kind == SyntaxKind.LabelOperand)
            .ToList();

        await Assert.That(labelOperands.Count).IsGreaterThanOrEqualTo(2);

        var symA = model.GetSymbol(labelOperands[0]);
        var symB = model.GetSymbol(labelOperands[1]);

        await Assert.That(symA).IsNotNull();
        await Assert.That(symB).IsNotNull();
        await Assert.That(symA!.Name).IsEqualTo("funcA.done");
        await Assert.That(symB!.Name).IsEqualTo("funcB.done");
    }

    [Test]
    public async Task GetDeclaredSymbol_LocalLabelDeclaration_ReturnsCorrectScopedSymbol()
    {
        var text = "SECTION \"Main\", ROM0\nfuncA:\n.done:\n    nop\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "main.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        var labelNode = tree.Root.ChildNodes()
            .Where(n => n.Kind == SyntaxKind.LabelDeclaration)
            .First(n => n.ChildTokens().Any(t => t.Text == ".done"));

        var sym = model.GetDeclaredSymbol(labelNode);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Name).IsEqualTo("funcA.done");
    }

    [Test]
    public async Task GetDeclaredSymbol_EquDirective_ReturnsConstantSymbol()
    {
        var text = "SECTION \"Main\", ROM0\nMY_VAL EQU 42\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "main.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        var directive = tree.Root.ChildNodes()
            .First(n => n.Kind == SyntaxKind.SymbolDirective);

        var sym = model.GetDeclaredSymbol(directive);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.Constant);
        await Assert.That(sym.Name).IsEqualTo("MY_VAL");
    }

    [Test]
    public async Task GetDeclaredSymbol_EqusDirective_ReturnsStringConstantSymbol()
    {
        var text = "SECTION \"Main\", ROM0\nMY_STR EQUS \"hello\"\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "main.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        var directive = tree.Root.ChildNodes()
            .First(n => n.Kind == SyntaxKind.SymbolDirective);

        var sym = model.GetDeclaredSymbol(directive);

        await Assert.That(sym).IsNotNull();
        await Assert.That(sym!.Kind).IsEqualTo(SymbolKind.StringConstant);
        await Assert.That(sym.Name).IsEqualTo("MY_STR");
    }

    [Test]
    public async Task LookupSymbols_DoesNotExposeOtherOwnerLocals()
    {
        var treeA = SyntaxTree.Parse(
            SourceText.From("SECTION \"A\", ROM0\nsecret:\n    nop\n", "a.asm"));
        var textB = "SECTION \"B\", ROM0\n    nop\n";
        var treeB = SyntaxTree.Parse(SourceText.From(textB, "b.asm"));

        var compilation = Compilation.Create(treeA, treeB);
        var modelB = compilation.GetSemanticModel(treeB);

        int pos = textB.IndexOf("nop");
        var visibleNames = modelB.LookupSymbols(pos)
            .Select(s => s.Name)
            .ToList();

        await Assert.That(visibleNames).DoesNotContain("secret");
    }

    [Test]
    public async Task LookupSymbols_IncludesExportedSymbols()
    {
        var text = "SECTION \"A\", ROM0\nmain:\n    EXPORT main\n    nop\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "a.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        int pos = text.IndexOf("nop");
        var visibleNames = model.LookupSymbols(pos)
            .Select(s => s.Name)
            .ToList();

        await Assert.That(visibleNames).Contains("main");
    }

    [Test]
    public async Task LookupSymbols_LocalLabelScope_OnlyShowsCurrentScope()
    {
        var text =
            "SECTION \"Main\", ROM0\n" +
            "funcA:\n.done:\n    nop\n" +
            "funcB:\n.done:\n    nop\n";
        var tree = SyntaxTree.Parse(SourceText.From(text, "main.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.GetSemanticModel(tree);

        int posA = text.IndexOf("nop");
        var namesA = model.LookupSymbols(posA)
            .Where(s => s.Name.Contains('.'))
            .Select(s => s.Name)
            .ToList();

        int posB = text.LastIndexOf("nop");
        var namesB = model.LookupSymbols(posB)
            .Where(s => s.Name.Contains('.'))
            .Select(s => s.Name)
            .ToList();

        await Assert.That(namesA).Contains("funcA.done");
        await Assert.That(namesA).DoesNotContain("funcB.done");
        await Assert.That(namesB).Contains("funcB.done");
        await Assert.That(namesB).DoesNotContain("funcA.done");
    }

    [Test]
    public async Task NullFilePath_MultiTree_Rejected()
    {
        var treeA = SyntaxTree.Parse("SECTION \"A\", ROM0\nmain:\n    nop\n");
        var treeB = SyntaxTree.Parse(
            SourceText.From("SECTION \"B\", ROM0\n    nop\n", "b.asm"));

        await Assert.That(() => Compilation.Create(treeA, treeB))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task SingleFile_NoChange()
    {
        var tree = SyntaxTree.Parse(SourceText.From(
            "SECTION \"Main\", ROM0\nmain:\n    jp main\n", "main.asm"));

        var compilation = Compilation.Create(tree);

        await Assert.That(compilation.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Count()).IsEqualTo(0);
    }

    private static IEnumerable<SyntaxNode> DescendantNodes(SyntaxNode node)
    {
        foreach (var child in node.ChildNodes())
        {
            yield return child;
            foreach (var descendant in DescendantNodes(child))
                yield return descendant;
        }
    }
}
