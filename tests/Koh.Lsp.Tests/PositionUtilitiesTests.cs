using Koh.Core.Diagnostics;
using Koh.Core.Syntax;
using Koh.Core.Text;
using Koh.Lsp;
using LspPosition = Microsoft.VisualStudio.LanguageServer.Protocol.Position;

namespace Koh.Lsp.Tests;

public class PositionUtilitiesTests
{
    [Test]
    public async Task ToOffset_FirstLine_FirstChar()
    {
        var source = SourceText.From("nop");
        var offset = PositionUtilities.ToOffset(source, new LspPosition(0, 0));
        await Assert.That(offset).IsEqualTo(0);
    }

    [Test]
    public async Task ToOffset_FirstLine_MidChar()
    {
        var source = SourceText.From("halt");
        var offset = PositionUtilities.ToOffset(source, new LspPosition(0, 2));
        await Assert.That(offset).IsEqualTo(2);
    }

    [Test]
    public async Task ToOffset_SecondLine()
    {
        var source = SourceText.From("nop\nld a, b\nhalt");
        var offset = PositionUtilities.ToOffset(source, new LspPosition(1, 0));
        await Assert.That(offset).IsEqualTo(4);
    }

    [Test]
    public async Task ToOffset_CharacterClampedToLineLength()
    {
        var source = SourceText.From("nop\nhalt");
        var offset = PositionUtilities.ToOffset(source, new LspPosition(0, 999));
        await Assert.That(offset).IsEqualTo(3); // clamped to line length
    }

    [Test]
    public async Task ToOffset_OutOfRangeLine_ReturnsEndOfText()
    {
        var source = SourceText.From("nop");
        var offset = PositionUtilities.ToOffset(source, new LspPosition(99, 0));
        await Assert.That(offset).IsEqualTo(source.Length);
    }

    [Test]
    public async Task ToLspPosition_RoundTrip()
    {
        var source = SourceText.From("nop\nld a, b\nhalt");
        var original = new LspPosition(1, 3);
        var offset = PositionUtilities.ToOffset(source, original);
        var result = PositionUtilities.ToLspPosition(source, offset);

        await Assert.That(result.Line).IsEqualTo(original.Line);
        await Assert.That(result.Character).IsEqualTo(original.Character);
    }

    [Test]
    public async Task ToLspDiagnostic_ErrorSeverity()
    {
        var source = SourceText.From("nop");
        var diag = new Diagnostic(new TextSpan(0, 3), "test error", DiagnosticSeverity.Error);
        var lspDiag = PositionUtilities.ToLspDiagnostic(diag, source);

        await Assert.That(lspDiag.Severity).IsEqualTo(
            Microsoft.VisualStudio.LanguageServer.Protocol.DiagnosticSeverity.Error);
        await Assert.That(lspDiag.Message).IsEqualTo("test error");
        await Assert.That(lspDiag.Source).IsEqualTo("koh");
    }

    [Test]
    public async Task ToLspDiagnostic_WarningSeverity()
    {
        var source = SourceText.From("nop");
        var diag = new Diagnostic(new TextSpan(0, 3), "test warn", DiagnosticSeverity.Warning);
        var lspDiag = PositionUtilities.ToLspDiagnostic(diag, source);

        await Assert.That(lspDiag.Severity).IsEqualTo(
            Microsoft.VisualStudio.LanguageServer.Protocol.DiagnosticSeverity.Warning);
    }

    [Test]
    public async Task ToLspDiagnostic_DefaultSpan_MapsToZeroZero()
    {
        var source = SourceText.From("nop");
        var diag = new Diagnostic(default, "global error");
        var lspDiag = PositionUtilities.ToLspDiagnostic(diag, source);

        await Assert.That(lspDiag.Range.Start.Line).IsEqualTo(0);
        await Assert.That(lspDiag.Range.Start.Character).IsEqualTo(0);
        await Assert.That(lspDiag.Range.End.Line).IsEqualTo(0);
        await Assert.That(lspDiag.Range.End.Character).IsEqualTo(0);
    }

    [Test]
    public async Task ToLspDiagnostic_NonDefaultSpan_MapsToCorrectRange()
    {
        // "nop\nhalt" — span covering "halt" at offset 4, length 4
        var source = SourceText.From("nop\nhalt");
        var diag = new Diagnostic(new TextSpan(4, 4), "error in halt");
        var lspDiag = PositionUtilities.ToLspDiagnostic(diag, source);

        await Assert.That(lspDiag.Range.Start.Line).IsEqualTo(1);
        await Assert.That(lspDiag.Range.Start.Character).IsEqualTo(0);
        await Assert.That(lspDiag.Range.End.Line).IsEqualTo(1);
        await Assert.That(lspDiag.Range.End.Character).IsEqualTo(4);
    }
}
