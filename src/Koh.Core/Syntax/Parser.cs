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

    // Condition-only keywords (z, nz, nc) — never registers.
    // CKeyword is NOT here: it's always parsed as RegisterOperand, binder disambiguates.
    private static bool IsConditionKeyword(SyntaxKind kind) =>
        kind >= SyntaxKind.ZKeyword && kind <= SyntaxKind.NcKeyword;

    // Register keywords occupy a contiguous range in SyntaxKind.
    private static bool IsRegisterKeyword(SyntaxKind kind) =>
        kind >= SyntaxKind.AKeyword && kind <= SyntaxKind.DeKeyword;

    private GreenNodeBase? ParseStatement()
    {
        if (IsInstructionKeyword(Current.Kind))
            return ParseInstruction();

        return null; // will be handled by the safety advance in ParseCompilationUnit
    }

    private GreenNode ParseInstruction()
    {
        var children = new List<GreenNodeBase>();
        children.Add(Advance()); // mnemonic

        // Parse first operand if present
        if (!AtEndOfStatement())
        {
            children.Add(ParseOperand());

            // Parse comma-separated additional operands
            while (!AtEndOfStatement() && Current.Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance()); // comma is a direct child
                children.Add(ParseOperand()); // ParseOperand handles EOF/missing gracefully
            }
        }

        return new GreenNode(SyntaxKind.InstructionStatement, children.ToArray());
    }

    private bool AtEndOfStatement()
    {
        if (Current.Kind == SyntaxKind.EndOfFileToken) return true;
        if (_position == 0) return false;
        return HasNewlineTrivia(_tokens[_position - 1]);
    }

    private GreenNodeBase ParseOperand()
    {
        var kind = Current.Kind;

        // Guard: if we're at EOF or on a token that can't start an operand,
        // produce a missing operand rather than absorbing the token.
        if (kind == SyntaxKind.EndOfFileToken)
        {
            ReportMissingToken(SyntaxKind.IdentifierToken);
            return new GreenNode(SyntaxKind.ImmediateOperand,
                [new GreenToken(SyntaxKind.MissingToken, "")]);
        }

        // Condition flags: z, nz, nc
        if (IsConditionKeyword(kind))
            return ParseConditionOperand();

        // Indirect: [...]
        if (kind == SyntaxKind.OpenBracketToken)
            return ParseIndirectOperand();

        // Register keywords
        if (IsRegisterKeyword(kind))
            return ParseRegisterOperand();

        // Local label reference
        if (kind == SyntaxKind.LocalLabelToken)
            return ParseLabelOperand();

        // Global label reference (identifier)
        if (kind == SyntaxKind.IdentifierToken)
            return ParseLabelOperand();

        // Everything else: immediate (number literal, current address, etc.)
        return ParseImmediateOperand();
    }

    private GreenNode ParseRegisterOperand() =>
        new(SyntaxKind.RegisterOperand, [Advance()]);

    private GreenNode ParseConditionOperand() =>
        new(SyntaxKind.ConditionOperand, [Advance()]);

    private GreenNode ParseLabelOperand() =>
        new(SyntaxKind.LabelOperand, [Advance()]);

    private GreenNode ParseImmediateOperand() =>
        new(SyntaxKind.ImmediateOperand, [Advance()]);

    private GreenNode ParseIndirectOperand()
    {
        var children = new List<GreenNodeBase>();
        children.Add(Advance()); // [

        // Consume inner tokens until ], comma, or end of statement
        while (Current.Kind != SyntaxKind.CloseBracketToken &&
               Current.Kind != SyntaxKind.CommaToken &&
               Current.Kind != SyntaxKind.EndOfFileToken &&
               !HasNewlineTrivia(_tokens[_position - 1]))
        {
            children.Add(Advance());
        }

        if (Current.Kind == SyntaxKind.CloseBracketToken)
        {
            children.Add(Advance()); // ]
        }
        else
        {
            // Missing closing bracket — insert a zero-width missing token
            var missing = new GreenToken(SyntaxKind.MissingToken, "");
            children.Add(missing);
            ReportMissingToken(SyntaxKind.CloseBracketToken);
        }

        return new GreenNode(SyntaxKind.IndirectOperand, children.ToArray());
    }

    private static bool HasNewlineTrivia(GreenToken token) =>
        token.TrailingTrivia.Any(t => t.Kind == SyntaxKind.NewlineTrivia);

    private void ReportMissingToken(SyntaxKind expected)
    {
        // Calculate current position for the diagnostic span
        int pos = 0;
        for (int i = 0; i < _position && i < _tokens.Count; i++)
            pos += _tokens[i].FullWidth;

        _diagnostics.Report(
            new TextSpan(pos, 0),
            $"Expected '{expected}'");
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
