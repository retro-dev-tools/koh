using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Lsp.Tests;

public class SemanticTokenTests
{
    /// <summary>
    /// Parse source and encode tokens, then extract (line, char, length, typeIndex) tuples.
    /// </summary>
    private static List<(int Line, int Char, int Length, int Type)> GetTokens(string source)
    {
        var sourceText = SourceText.From(source, "file:///test.asm");
        var tree = SyntaxTree.Parse(sourceText);
        var data = SemanticTokenEncoder.Encode(tree);

        var tokens = new List<(int Line, int Char, int Length, int Type)>();
        int line = 0, col = 0;
        for (int i = 0; i + 4 < data.Length; i += 5)
        {
            line += data[i];
            col = data[i] == 0 ? col + data[i + 1] : data[i + 1];
            tokens.Add((line, col, data[i + 2], data[i + 3]));
        }
        return tokens;
    }

    private static bool HasTokenOfType(List<(int Line, int Char, int Length, int Type)> tokens, int typeIndex)
        => tokens.Any(t => t.Type == typeIndex);

    [Test]
    public async Task InstructionKeyword_ClassifiedAsKeyword()
    {
        var tokens = GetTokens("nop");
        await Assert.That(HasTokenOfType(tokens, SemanticTokenEncoder.TypeKeyword)).IsTrue();
    }

    [Test]
    public async Task Register_ClassifiedAsRegister()
    {
        var tokens = GetTokens("ld a, b");
        await Assert.That(tokens.Any(t => t.Type == SemanticTokenEncoder.TypeRegexp)).IsTrue();
    }

    [Test]
    public async Task Directive_ClassifiedAsDirective()
    {
        var tokens = GetTokens("SECTION \"Main\", ROM0");
        await Assert.That(tokens.Any(t => t.Type == SemanticTokenEncoder.TypeEnum)).IsTrue();
    }

    [Test]
    public async Task Number_ClassifiedAsNumber()
    {
        var tokens = GetTokens("ld a, $42");
        await Assert.That(HasTokenOfType(tokens, SemanticTokenEncoder.TypeNumber)).IsTrue();
    }

    [Test]
    public async Task String_ClassifiedAsString()
    {
        var tokens = GetTokens("db \"hello\"");
        await Assert.That(HasTokenOfType(tokens, SemanticTokenEncoder.TypeString)).IsTrue();
    }

    [Test]
    public async Task Comment_ClassifiedAsComment()
    {
        var tokens = GetTokens("; this is a comment");
        await Assert.That(HasTokenOfType(tokens, SemanticTokenEncoder.TypeComment)).IsTrue();
    }

    [Test]
    public async Task LocalLabel_ClassifiedAsLabel()
    {
        var tokens = GetTokens("Global:\n.local:");
        await Assert.That(HasTokenOfType(tokens, SemanticTokenEncoder.TypeLabel)).IsTrue();
    }

    [Test]
    public async Task Operator_ClassifiedAsOperator()
    {
        var tokens = GetTokens("X EQU 1 + 2");
        await Assert.That(HasTokenOfType(tokens, SemanticTokenEncoder.TypeOperator)).IsTrue();
    }

    [Test]
    public async Task ConditionFlag_ClassifiedAsConditionFlag()
    {
        var tokens = GetTokens("jp nz, $1234");
        await Assert.That(HasTokenOfType(tokens, SemanticTokenEncoder.TypeFunction)).IsTrue();
    }

    [Test]
    public async Task Identifier_ClassifiedAsVariable()
    {
        var tokens = GetTokens("SECTION \"Main\", ROM0\nMyLabel:\n  jp MyLabel");
        await Assert.That(HasTokenOfType(tokens, SemanticTokenEncoder.TypeVariable)).IsTrue();
    }

    [Test]
    public async Task SectionType_ClassifiedAsSectionType()
    {
        var tokens = GetTokens("SECTION \"Main\", ROM0");
        await Assert.That(HasTokenOfType(tokens, SemanticTokenEncoder.TypeType)).IsTrue();
    }

    [Test]
    public async Task MacroParam_ClassifiedAsParameter()
    {
        var tokens = GetTokens("MyMacro: MACRO\n  ld a, \\1\nENDM");
        await Assert.That(HasTokenOfType(tokens, SemanticTokenEncoder.TypeParameter)).IsTrue();
    }

    [Test]
    public async Task MultipleTokens_DeltaEncodingIsCorrect()
    {
        var source = "nop\nhalt";
        var sourceText = SourceText.From(source, "file:///test.asm");
        var tree = SyntaxTree.Parse(sourceText);
        var data = SemanticTokenEncoder.Encode(tree);

        // First token: nop at (0, 0)
        // Second token: halt at (1, 0)
        // data[0..4] = deltaLine=0, deltaChar=0, length=3, type=keyword, modifiers=0
        // data[5..9] = deltaLine=1, deltaChar=0, length=4, type=keyword, modifiers=0
        await Assert.That(data.Length).IsGreaterThanOrEqualTo(10);
        // First token
        await Assert.That(data[0]).IsEqualTo(0); // deltaLine
        await Assert.That(data[1]).IsEqualTo(0); // deltaChar
        await Assert.That(data[2]).IsEqualTo(3); // length of "nop"
        // Second token
        await Assert.That(data[5]).IsEqualTo(1); // deltaLine (next line)
        await Assert.That(data[6]).IsEqualTo(0); // deltaChar (start of line)
        await Assert.That(data[7]).IsEqualTo(4); // length of "halt"
    }

    [Test]
    public async Task EmptySource_ReturnsEmptyData()
    {
        var sourceText = SourceText.From("", "file:///test.asm");
        var tree = SyntaxTree.Parse(sourceText);
        var data = SemanticTokenEncoder.Encode(tree);

        await Assert.That(data.Length).IsEqualTo(0);
    }
}
