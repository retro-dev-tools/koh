using Koh.Core.Diagnostics;
using Koh.Core.Syntax.InternalSyntax;
using Koh.Core.Text;

namespace Koh.Core.Syntax;

internal sealed class Parser
{
    private readonly SourceText _text;
    private readonly List<GreenToken> _tokens;
    private readonly DiagnosticBag _diagnostics = new();
    private int _position;

    public Parser(SourceText text)
    {
        _text = text;
        _tokens = LexAll(text);
    }

    private static List<GreenToken> LexAll(SourceText text)
    {
        var lexer = new Lexer(text);
        var tokens = new List<GreenToken>();

        // We need to get the green tokens. The Lexer returns SyntaxToken (red),
        // so we re-lex and capture green tokens by creating a helper.
        // Actually, the Lexer returns SyntaxToken which wraps GreenToken.
        // We need access to the GreenToken. Let's use the lexer and extract.
        // Since SyntaxToken's green is private, we'll store the tokens as SyntaxTokens
        // and work with them. But we need GreenTokens to build GreenNodes.
        //
        // Let's change approach: store SyntaxTokens and extract green tokens
        // through a helper method on the lexer. Actually, let's just lex
        // and keep track of both.
        //
        // Simplest approach: use InternalLexer that returns GreenTokens.
        // But the Lexer is already built. Let's add an internal method.
        //
        // Actually, let's just build the parser around SyntaxTokens and
        // create GreenNodes from the green tokens we extract.

        // We'll use a different approach: lex into SyntaxTokens, then build
        // green tree from the underlying green tokens.
        return LexGreenTokens(text);
    }

    private static List<GreenToken> LexGreenTokens(SourceText text)
    {
        // We need to lex and get GreenTokens. Since the Lexer wraps them
        // in SyntaxToken with an internal constructor, and the green field
        // is private, we need a way to access them.
        // Let's add an internal method to the Lexer for this purpose.
        var lexer = new Lexer(text);
        var tokens = new List<GreenToken>();
        while (true)
        {
            var greenToken = lexer.NextGreenToken();
            tokens.Add(greenToken);
            if (greenToken.Kind == SyntaxKind.EndOfFileToken) break;
        }
        return tokens;
    }

    private GreenToken Current => _position < _tokens.Count
        ? _tokens[_position]
        : _tokens[^1]; // EOF

    private GreenToken Advance()
    {
        var token = Current;
        if (_position < _tokens.Count - 1) _position++;
        return token;
    }

    public SyntaxTree Parse()
    {
        var root = ParseCompilationUnit();
        var redRoot = new SyntaxNode(root, null, 0);
        return SyntaxTree.Create(_text, redRoot, _diagnostics.ToList());
    }

    private GreenNode ParseCompilationUnit()
    {
        var children = new List<GreenNodeBase>();

        while (Current.Kind != SyntaxKind.EndOfFileToken)
        {
            var startPos = _position;
            var statement = ParseStatement();
            if (statement != null)
                children.Add(statement);

            // Safety: if we didn't advance, force advance to avoid infinite loop
            if (_position == startPos)
            {
                var bad = Advance();
                ReportBadToken(bad);
            }
        }

        children.Add(Advance()); // EOF token
        return new GreenNode(SyntaxKind.CompilationUnit, children.ToArray());
    }

    private GreenNodeBase? ParseStatement()
    {
        return Current.Kind switch
        {
            SyntaxKind.NopKeyword or
            SyntaxKind.LdKeyword or
            SyntaxKind.AddKeyword => ParseInstruction(),
            _ => null, // will be handled by the safety advance in ParseCompilationUnit
        };
    }

    private GreenNode ParseInstruction()
    {
        var children = new List<GreenNodeBase>();
        var mnemonic = Advance();
        children.Add(mnemonic);

        // Consume remaining tokens on this line (operands, commas, etc.)
        // A line ends when: we hit EOF, or the previous token had newline trailing trivia
        while (Current.Kind != SyntaxKind.EndOfFileToken)
        {
            // Check if the mnemonic or last consumed token had a newline in trailing trivia
            // That means we've moved to a new line
            if (HasNewlineTrivia(children[^1]))
                break;

            children.Add(Advance());
        }

        return new GreenNode(SyntaxKind.InstructionStatement, children.ToArray());
    }

    private static bool HasNewlineTrivia(GreenNodeBase node)
    {
        if (node is GreenToken token)
        {
            return token.TrailingTrivia.Any(t => t.Kind == SyntaxKind.NewlineTrivia);
        }
        return false;
    }

    private void ReportBadToken(GreenToken token)
    {
        // Calculate position of the bad token by summing all prior token widths
        int pos = 0;
        for (int i = 0; i < _tokens.Count; i++)
        {
            if (ReferenceEquals(_tokens[i], token)) break;
            pos += _tokens[i].FullWidth;
        }

        _diagnostics.Report(
            new TextSpan(pos + token.LeadingTriviaWidth, token.Width),
            $"Unexpected token '{token.Kind}'");
    }
}
