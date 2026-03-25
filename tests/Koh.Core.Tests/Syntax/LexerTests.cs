using Koh.Core.Syntax;
using Koh.Core.Text;

namespace Koh.Core.Tests.Syntax;

public class LexerTests
{
    private static List<SyntaxToken> Lex(string source)
    {
        var text = SourceText.From(source);
        var lexer = new Lexer(text);
        var tokens = new List<SyntaxToken>();
        while (true)
        {
            var token = lexer.NextToken();
            tokens.Add(token);
            if (token.Kind == SyntaxKind.EndOfFileToken) break;
        }
        return tokens;
    }

    [Test]
    public async Task Lexer_Nop()
    {
        var tokens = Lex("nop");
        await Assert.That(tokens).HasCount().EqualTo(2); // NOP + EOF
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.NopKeyword);
        await Assert.That(tokens[0].Text).IsEqualTo("nop");
    }

    [Test]
    public async Task Lexer_LdAB()
    {
        var tokens = Lex("ld a, b");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.LdKeyword);
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.AKeyword);
        await Assert.That(tokens[2].Kind).IsEqualTo(SyntaxKind.CommaToken);
        await Assert.That(tokens[3].Kind).IsEqualTo(SyntaxKind.BKeyword);
    }

    [Test]
    public async Task Lexer_WhitespaceTrivia()
    {
        var tokens = Lex("  nop");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.NopKeyword);
        var leading = tokens[0].LeadingTrivia.ToList();
        await Assert.That(leading).HasCount().EqualTo(1);
        await Assert.That(leading[0].Kind).IsEqualTo(SyntaxKind.WhitespaceTrivia);
        await Assert.That(leading[0].Text).IsEqualTo("  ");
    }

    [Test]
    public async Task Lexer_LineComment()
    {
        var tokens = Lex("nop ; comment");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.NopKeyword);
        var trailing = tokens[0].TrailingTrivia.ToList();
        await Assert.That(trailing.Any(t => t.Kind == SyntaxKind.LineCommentTrivia)).IsTrue();
    }

    [Test]
    public async Task Lexer_Number()
    {
        var tokens = Lex("$FF");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.NumberLiteral);
        await Assert.That(tokens[0].Text).IsEqualTo("$FF");
    }

    [Test]
    public async Task Lexer_CaseInsensitive()
    {
        var tokens = Lex("NOP");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.NopKeyword);
    }
}
