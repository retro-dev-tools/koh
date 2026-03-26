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

    // Instruction keywords occupy a contiguous range in SyntaxKind — new instructions
    // added within that block are automatically included.
    private static bool IsInstructionKeyword(SyntaxKind kind) =>
        kind >= SyntaxKind.NopKeyword && kind <= SyntaxKind.LdhKeyword;

    private GreenNodeBase? ParseStatement()
    {
        if (IsInstructionKeyword(Current.Kind))
            return ParseInstruction();

        return null; // will be handled by the safety advance in ParseCompilationUnit
    }

    private GreenNode ParseInstruction()
    {
        var children = new List<GreenNodeBase>();
        var mnemonic = Advance();
        children.Add(mnemonic);

        // Consume remaining tokens on this line (operands, commas, etc.)
        // A line ends when: we hit EOF, or the previous token had newline trailing trivia.
        // We check _tokens directly so this stays correct even when children contains GreenNodes.
        while (Current.Kind != SyntaxKind.EndOfFileToken)
        {
            if (HasNewlineTrivia(_tokens[_position - 1]))
                break;

            children.Add(Advance());
        }

        return new GreenNode(SyntaxKind.InstructionStatement, children.ToArray());
    }

    private static bool HasNewlineTrivia(GreenToken token) =>
        token.TrailingTrivia.Any(t => t.Kind == SyntaxKind.NewlineTrivia);

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
