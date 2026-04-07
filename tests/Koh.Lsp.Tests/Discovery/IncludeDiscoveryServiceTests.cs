using Koh.Lsp.Discovery;

namespace Koh.Lsp.Tests.Discovery;

public class IncludeDiscoveryServiceTests
{
    private const string WorkspaceFolder = "C:/project";
    private const string MainFile = "C:/project/src/main.asm";

    private readonly IncludeDiscoveryService _service = new();

    [Test]
    public async Task SimpleInclude_IsExtracted()
    {
        var text = """
            INCLUDE "utils.asm"
            """;

        var result = _service.Discover(MainFile, text, WorkspaceFolder);

        await Assert.That(result.IncludedFiles.Count).IsEqualTo(1);
        await Assert.That(result.IncludedFiles[0]).IsEqualTo(
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(MainFile)!, "utils.asm")));
    }

    [Test]
    public async Task MultipleIncludes_AreAllExtracted()
    {
        var text = """
            INCLUDE "header.asm"
            nop
            INCLUDE "footer.asm"
            """;

        var result = _service.Discover(MainFile, text, WorkspaceFolder);

        await Assert.That(result.IncludedFiles.Count).IsEqualTo(2);
    }

    [Test]
    public async Task CaseInsensitive_Include()
    {
        var text = """
            include "lower.asm"
            Include "mixed.asm"
            INCLUDE "upper.asm"
            """;

        var result = _service.Discover(MainFile, text, WorkspaceFolder);

        await Assert.That(result.IncludedFiles.Count).IsEqualTo(3);
    }

    [Test]
    public async Task CommentedOutInclude_IsIgnored()
    {
        var text = """
            ; INCLUDE "commented.asm"
            INCLUDE "real.asm"
            """;

        var result = _service.Discover(MainFile, text, WorkspaceFolder);

        await Assert.That(result.IncludedFiles.Count).IsEqualTo(1);
        await Assert.That(result.IncludedFiles[0]).Contains("real.asm");
    }

    [Test]
    public async Task InlineComment_DoesNotAffectExtraction()
    {
        var text = """
            INCLUDE "utils.asm" ; load utilities
            """;

        var result = _service.Discover(MainFile, text, WorkspaceFolder);

        await Assert.That(result.IncludedFiles.Count).IsEqualTo(1);
        await Assert.That(result.IncludedFiles[0]).Contains("utils.asm");
    }

    [Test]
    public async Task IncludeAfterLabel_IsExtracted()
    {
        var text = """
            main: INCLUDE "utils.asm"
            """;

        var result = _service.Discover(MainFile, text, WorkspaceFolder);

        await Assert.That(result.IncludedFiles.Count).IsEqualTo(1);
        await Assert.That(result.IncludedFiles[0]).Contains("utils.asm");
    }

    [Test]
    public async Task MalformedInclude_NoClosingQuote_DoesNotCrash()
    {
        var text = """
            INCLUDE "broken
            INCLUDE "valid.asm"
            """;

        var result = _service.Discover(MainFile, text, WorkspaceFolder);

        await Assert.That(result.IncludedFiles.Count).IsEqualTo(1);
        await Assert.That(result.IncludedFiles[0]).Contains("valid.asm");
    }

    [Test]
    public async Task MalformedInclude_NoQuotes_DoesNotCrash()
    {
        var text = """
            INCLUDE broken.asm
            """;

        var result = _service.Discover(MainFile, text, WorkspaceFolder);

        await Assert.That(result.IncludedFiles.Count).IsEqualTo(0);
    }

    [Test]
    public async Task EmptyFile_DoesNotCrash()
    {
        var result = _service.Discover(MainFile, "", WorkspaceFolder);

        await Assert.That(result.IncludedFiles.Count).IsEqualTo(0);
    }

    [Test]
    public async Task FilePath_IsNormalized()
    {
        var result = _service.Discover("C:/project/./src/../src/main.asm", "", WorkspaceFolder);

        await Assert.That(result.FilePath).IsEqualTo(Path.GetFullPath("C:/project/src/main.asm"));
    }

    [Test]
    public async Task RelativePath_ResolvedToContainingFileDirectory()
    {
        // The included file does not exist on disk at workspace root,
        // so it should resolve relative to the containing file's directory.
        var text = """
            INCLUDE "helpers/math.asm"
            """;

        var result = _service.Discover(MainFile, text, WorkspaceFolder);

        var expected = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(MainFile)!, "helpers/math.asm"));
        await Assert.That(result.IncludedFiles[0]).IsEqualTo(expected);
    }

    [Test]
    public async Task BlockComment_IncludeInsideIsIgnored()
    {
        var text = """
            /* INCLUDE "blocked.asm" */
            INCLUDE "real.asm"
            """;

        var result = _service.Discover(MainFile, text, WorkspaceFolder);

        await Assert.That(result.IncludedFiles.Count).IsEqualTo(1);
        await Assert.That(result.IncludedFiles[0]).Contains("real.asm");
    }

    [Test]
    public async Task MultiLineBlockComment_IncludeInsideIsIgnored()
    {
        var text = """
            /*
            INCLUDE "blocked.asm"
            */
            INCLUDE "real.asm"
            """;

        var result = _service.Discover(MainFile, text, WorkspaceFolder);

        await Assert.That(result.IncludedFiles.Count).IsEqualTo(1);
        await Assert.That(result.IncludedFiles[0]).Contains("real.asm");
    }

    [Test]
    public async Task OverlayText_OverridesDiskContent()
    {
        // This test demonstrates that the caller controls the text source.
        // When overlay text is provided, it is used instead of disk content.
        var overlayText = """
            INCLUDE "overlay-only.asm"
            """;

        var result = _service.Discover(MainFile, overlayText, WorkspaceFolder);

        await Assert.That(result.IncludedFiles.Count).IsEqualTo(1);
        await Assert.That(result.IncludedFiles[0]).Contains("overlay-only.asm");
    }

    [Test]
    public async Task IncludeKeywordInsideIdentifier_IsNotExtracted()
    {
        var text = """
            xINCLUDE "not_real.asm"
            """;

        var result = _service.Discover(MainFile, text, WorkspaceFolder);

        await Assert.That(result.IncludedFiles.Count).IsEqualTo(0);
    }

    [Test]
    public async Task LeadingWhitespace_IsHandled()
    {
        var text = "    \t  INCLUDE \"indented.asm\"";

        var result = _service.Discover(MainFile, text, WorkspaceFolder);

        await Assert.That(result.IncludedFiles.Count).IsEqualTo(1);
        await Assert.That(result.IncludedFiles[0]).Contains("indented.asm");
    }

    [Test]
    public async Task EmptyQuotedPath_IsIgnored()
    {
        var text = """
            INCLUDE ""
            """;

        var result = _service.Discover(MainFile, text, WorkspaceFolder);

        await Assert.That(result.IncludedFiles.Count).IsEqualTo(0);
    }
}
