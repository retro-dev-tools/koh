using Koh.Common;
using Koh.Native;
using Koh.Native.Syntax;

namespace Koh.Lsp.Tests;

public class NativeBridgeTests
{
    [Test]
    public async Task NativeWarningsReachSemanticAndEmissionDiagnostics()
    {
        const string source =
            "; 😀\nWARN \"custom\"\nASSERT WARN,0,\"detail\"\nSECTION \"main\",ROM0\nnop";
        var tree = SyntaxTree.Parse(SourceText.From(source, "warnings.asm"));
        var compilation = Compilation.Create(tree);
        var model = compilation.Emit();
        await Assert.That(model.Success).IsTrue();
        foreach (
            var diagnostics in new[]
            {
                compilation.GetSemanticModel(tree).GetDiagnostics(),
                model.Diagnostics,
            }
        )
        {
            var warnings = diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning)
                .ToArray();
            await Assert.That(warnings.Length).IsEqualTo(2);
            await Assert.That(warnings[0].Message).IsEqualTo("custom");
            await Assert.That(warnings[0].FilePath).IsEqualTo("warnings.asm");
            await Assert
                .That(warnings[0].Span.Start)
                .IsEqualTo(source.IndexOf("WARN", StringComparison.Ordinal));
            await Assert.That(warnings[1].Message).IsEqualTo("Assertion failed: detail");
            await Assert
                .That(warnings[1].Span.Start)
                .IsEqualTo(source.IndexOf("ASSERT", StringComparison.Ordinal));
        }
    }

    [Test]
    public async Task NativeAssemblyProducesInstructionBytes()
    {
        var tree = SyntaxTree.Parse(
            SourceText.From("SECTION \"main\", ROM0[$100]\nStart: ld a,$12\nret", "bridge.asm")
        );
        var model = Compilation.Create(tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert
            .That(model.Sections.Single().Data)
            .IsEquivalentTo(new byte[] { 0x3e, 0x12, 0xc9 });
    }

    [Test]
    public async Task NativeAnalysisOffsetsUseUtf16()
    {
        const string source = "db \"🌍\"\nLabel: nop";
        var tree = SyntaxTree.Parse(SourceText.From(source, "unicode.asm"));
        var offset = source.IndexOf("Label", StringComparison.Ordinal);
        var token = tree.Root.FindToken(offset);
        await Assert.That(token!.Text).IsEqualTo("Label");
        await Assert.That(token.Span.Start).IsEqualTo(offset);
    }

    [Test]
    public async Task VirtualIncludeIsPassedToNativeAssembly()
    {
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile("defs.inc", "DEF VALUE EQU 42");
        var tree = SyntaxTree.Parse(
            SourceText.From("INCLUDE \"defs.inc\"\nSECTION \"main\", ROM0\ndb VALUE", "overlay.asm")
        );
        var model = Compilation.Create(resolver, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections.Single().Data).IsEquivalentTo(new byte[] { 42 });
    }

    [Test]
    public async Task DynamicIncludeUsesVirtualOverlay()
    {
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile("dynamic.inc", "DEF VALUE EQU 17");
        var tree = SyntaxTree.Parse(
            SourceText.From(
                "DEF FILE EQUS \"\\\"dynamic.inc\\\"\"\nINCLUDE FILE\nSECTION \"main\",ROM0\ndb VALUE",
                "dynamic.asm"
            )
        );
        var model = Compilation.Create(resolver, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections.Single().Data).IsEquivalentTo(new byte[] { 17 });
    }

    [Test]
    public async Task BinaryOverlayIsPassedToNativeAssembly()
    {
        var resolver = new VirtualFileResolver();
        resolver.AddBinaryFile("bytes.bin", [0, 128, 255]);
        resolver.AddTextFile("text.txt", "ok");
        var tree = SyntaxTree.Parse(
            SourceText.From(
                "SECTION \"main\",ROM0\nINCBIN \"bytes.bin\"\nINCBIN \"text.txt\"",
                "binary.asm"
            )
        );
        var model = Compilation.Create(resolver, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert
            .That(model.Sections.Single().Data)
            .IsEquivalentTo(new byte[] { 0, 128, 255, 111, 107 });
    }

    [Test]
    public async Task TextAndBinaryReadsUseSeparateOverlaySnapshots()
    {
        var resolver = new VirtualFileResolver();
        resolver.AddTextFile("shared.inc", "db 7");
        resolver.AddBinaryFile("shared.inc", [8, 9]);
        var tree = SyntaxTree.Parse(
            SourceText.From(
                "SECTION \"main\",ROM0\nINCLUDE \"shared.inc\"\nINCBIN \"shared.inc\"",
                "separate.asm"
            )
        );
        var model = Compilation.Create(resolver, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections.Single().Data).IsEquivalentTo(new byte[] { 7, 8, 9 });
    }

    [Test]
    public async Task UnsavedTextDoesNotReplacePhysicalIncbinData()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"koh-native-overlay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var asset = Path.Combine(directory, "asset.bin");
            File.WriteAllBytes(asset, [8, 9]);
            var resolver = new Koh.Lsp.Source.WorkspaceOverlayResolver(new FileSystemResolver());
            resolver.SetOverlayText(asset, "unsaved text");
            var tree = SyntaxTree.Parse(
                SourceText.From(
                    "SECTION \"main\",ROM0\nINCBIN \"asset.bin\"",
                    Path.Combine(directory, "main.asm")
                )
            );
            var model = Compilation.Create(resolver, tree).Emit();
            await Assert.That(model.Success).IsTrue();
            await Assert.That(model.Sections.Single().Data).IsEquivalentTo(new byte[] { 8, 9 });
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
