using Koh.Lsp;

namespace Koh.Lsp.Tests;

public class WorkspaceTests
{
    [Test]
    public async Task GetDocument_UnknownUri_ReturnsNull()
    {
        var ws = new Workspace();
        var doc = ws.GetDocument("file:///missing.asm");
        await Assert.That(doc).IsNull();
    }

    [Test]
    public async Task GetDocumentDiagnostics_UnknownUri_ReturnsNulls()
    {
        var ws = new Workspace();
        var (text, tree, diags) = ws.GetDocumentDiagnostics("file:///missing.asm");
        await Assert.That(text).IsNull();
        await Assert.That(tree).IsNull();
        await Assert.That(diags).IsNull();
    }

    [Test]
    public async Task OpenDocument_ThenGetDocument_ReturnsDocument()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm", "nop");
        var doc = ws.GetDocument("file:///test.asm");

        await Assert.That(doc).IsNotNull();
        await Assert.That(doc!.Value.Tree).IsNotNull();
    }

    [Test]
    public async Task ChangeDocument_UpdatesContent()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm", "nop");
        ws.ChangeDocument("file:///test.asm", "halt");

        var doc = ws.GetDocument("file:///test.asm");
        await Assert.That(doc).IsNotNull();
        // Verify the tree was actually rebuilt with the new content
        var token = doc!.Value.Tree.Root.FindToken(0);
        await Assert.That(token).IsNotNull();
        await Assert.That(token!.Kind).IsEqualTo(Koh.Core.Syntax.SyntaxKind.HaltKeyword);
    }

    [Test]
    public async Task CloseDocument_RemovesDocument()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm", "nop");
        ws.CloseDocument("file:///test.asm");

        var doc = ws.GetDocument("file:///test.asm");
        await Assert.That(doc).IsNull();
        await Assert.That(ws.GetModel()).IsNull();
        await Assert.That(ws.OpenDocumentUris.Count).IsEqualTo(0);
    }

    [Test]
    public async Task OpenDocumentUris_TracksOpenFiles()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///a.asm", "nop");
        ws.OpenDocument("file:///b.asm", "halt");

        await Assert.That(ws.OpenDocumentUris.Count).IsEqualTo(2);

        ws.CloseDocument("file:///a.asm");
        await Assert.That(ws.OpenDocumentUris.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CaseInsensitiveUri()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///Test.asm", "nop");
        var doc = ws.GetDocument("file:///test.asm");
        await Assert.That(doc).IsNotNull();
    }

    [Test]
    public async Task GetModel_ReturnsEmitModel()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm", "SECTION \"Main\", ROM0\nnop");
        var model = ws.GetModel();

        await Assert.That(model).IsNotNull();
        await Assert.That(model!.Success).IsTrue();
    }

    [Test]
    public async Task Diagnostics_ParseErrors_Reported()
    {
        var ws = new Workspace();
        ws.OpenDocument("file:///test.asm", "SECTION \"Main\", ROM0\nld a,");
        var (text, tree, diags) = ws.GetDocumentDiagnostics("file:///test.asm");

        await Assert.That(diags).IsNotNull();
        await Assert.That(diags!.Count).IsGreaterThan(0);
    }
}
