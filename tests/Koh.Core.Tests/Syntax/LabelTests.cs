using Koh.Core.Syntax;

namespace Koh.Core.Tests.Syntax;

public class LabelTests
{
    [Test]
    public async Task GlobalLabel()
    {
        var tree = SyntaxTree.Parse("main:");
        var stmts = tree.Root.ChildNodes().ToList();

        await Assert.That(stmts).Count().IsEqualTo(1);
        await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.LabelDeclaration);

        var tokens = stmts[0].ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.IdentifierToken);
        await Assert.That(tokens[0].Text).IsEqualTo("main");
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.ColonToken);
    }

    [Test]
    public async Task LocalLabel()
    {
        var tree = SyntaxTree.Parse(".loop:");
        var stmts = tree.Root.ChildNodes().ToList();

        await Assert.That(stmts).Count().IsEqualTo(1);
        await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.LabelDeclaration);

        var tokens = stmts[0].ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.LocalLabelToken);
        await Assert.That(tokens[0].Text).IsEqualTo(".loop");
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.ColonToken);
    }

    [Test]
    public async Task ExportedLabel()
    {
        var tree = SyntaxTree.Parse("main::");
        var stmts = tree.Root.ChildNodes().ToList();

        await Assert.That(stmts).Count().IsEqualTo(1);
        await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.LabelDeclaration);

        var tokens = stmts[0].ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.IdentifierToken);
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.DoubleColonToken);
    }

    [Test]
    public async Task LabelBeforeInstruction()
    {
        var tree = SyntaxTree.Parse("main:\n    nop");
        var stmts = tree.Root.ChildNodes().ToList();

        await Assert.That(stmts).Count().IsEqualTo(2);
        await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.LabelDeclaration);
        await Assert.That(stmts[1].Kind).IsEqualTo(SyntaxKind.InstructionStatement);
    }

    [Test]
    public async Task LabelAndInstructionSameLine()
    {
        // RGBDS allows label and instruction on same line
        var tree = SyntaxTree.Parse("main: nop");
        var stmts = tree.Root.ChildNodes().ToList();

        await Assert.That(stmts).Count().IsEqualTo(2);
        await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.LabelDeclaration);
        await Assert.That(stmts[1].Kind).IsEqualTo(SyntaxKind.InstructionStatement);
    }

    [Test]
    public async Task MultipleLabels()
    {
        var tree = SyntaxTree.Parse("start:\n.loop:\n    nop");
        var stmts = tree.Root.ChildNodes().ToList();

        await Assert.That(stmts).Count().IsEqualTo(3);
        await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.LabelDeclaration);
        await Assert.That(stmts[1].Kind).IsEqualTo(SyntaxKind.LabelDeclaration);
        await Assert.That(stmts[2].Kind).IsEqualTo(SyntaxKind.InstructionStatement);
    }

    [Test]
    public async Task LabelNoDiagnostics()
    {
        var tree = SyntaxTree.Parse("main:\n    nop");
        await Assert.That(tree.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task IdentifierWithoutColon_IsNotLabel()
    {
        // A bare identifier is not a label — should not produce LabelDeclaration
        var tree = SyntaxTree.Parse("someIdentifier");
        var stmts = tree.Root.ChildNodes().ToList();
        // It won't match instruction or label, so it'll be a bad token
        await Assert.That(stmts.All(s => s.Kind != SyntaxKind.LabelDeclaration)).IsTrue();
    }

    // =========================================================================
    // Local labels without trailing colon (RGBDS compatibility)
    // =========================================================================

    [Test]
    public async Task LocalLabel_WithoutColon_ParsedAsLabel()
    {
        var tree = SyntaxTree.Parse(".loadLoop\n    nop");
        var stmts = tree.Root.ChildNodes().ToList();

        await Assert.That(stmts).Count().IsEqualTo(2);
        await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.LabelDeclaration);
        await Assert.That(stmts[1].Kind).IsEqualTo(SyntaxKind.InstructionStatement);

        var tokens = stmts[0].ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.LocalLabelToken);
        await Assert.That(tokens[0].Text).IsEqualTo(".loadLoop");
        // No colon token — only 1 child
        await Assert.That(tokens).Count().IsEqualTo(1);
    }

    [Test]
    public async Task LocalLabel_WithoutColon_NoDiagnostics()
    {
        var tree = SyntaxTree.Parse(".done\n    ret");
        await Assert.That(tree.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task LocalLabel_WithoutColon_AtEndOfFile()
    {
        var tree = SyntaxTree.Parse(".end");
        var stmts = tree.Root.ChildNodes().ToList();

        await Assert.That(stmts).Count().IsEqualTo(1);
        await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.LabelDeclaration);
    }

    [Test]
    public async Task LocalLabel_WithoutColon_MultipleOnSeparateLines()
    {
        var tree = SyntaxTree.Parse(".first\n.second\n    nop");
        var stmts = tree.Root.ChildNodes().ToList();

        await Assert.That(stmts).Count().IsEqualTo(3);
        await Assert.That(stmts[0].Kind).IsEqualTo(SyntaxKind.LabelDeclaration);
        await Assert.That(stmts[1].Kind).IsEqualTo(SyntaxKind.LabelDeclaration);
        await Assert.That(stmts[2].Kind).IsEqualTo(SyntaxKind.InstructionStatement);
    }
}
