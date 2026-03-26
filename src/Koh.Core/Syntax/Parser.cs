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
        var (tokens, lexerDiagnostics) = LexAll(text);
        _tokens = tokens;
        foreach (var d in lexerDiagnostics)
            _diagnostics.Report(d.Span, d.Message, d.Severity);
    }

    private static (List<GreenToken> tokens, IReadOnlyList<Diagnostic> lexerDiagnostics) LexAll(
        SourceText text
    )
    {
        var lexer = new Lexer(text);
        var tokens = new List<GreenToken>();
        while (true)
        {
            var greenToken = lexer.NextGreenToken();
            tokens.Add(greenToken);
            if (greenToken.Kind == SyntaxKind.EndOfFileToken)
                break;
        }
        return (tokens, lexer.Diagnostics);
    }

    private GreenToken Current => _position < _tokens.Count ? _tokens[_position] : _tokens[^1]; // EOF

    private GreenToken Peek(int offset = 1)
    {
        int index = _position + offset;
        return index < _tokens.Count ? _tokens[index] : _tokens[^1];
    }

    private GreenToken Advance()
    {
        var token = Current;
        if (_position < _tokens.Count - 1)
            _position++;
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
        // Labels: identifier/localLabel followed by : or ::
        if (IsLabelStart())
            return ParseLabelDeclaration();

        if (IsInstructionKeyword(Current.Kind))
            return ParseInstruction();

        return null; // will be handled by the safety advance in ParseCompilationUnit
    }

    private bool IsLabelStart()
    {
        if (Current.Kind is SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken)
            return Peek().Kind is SyntaxKind.ColonToken or SyntaxKind.DoubleColonToken;
        return false;
    }

    private GreenNode ParseLabelDeclaration()
    {
        var name = Advance(); // identifier or local label token
        var colon = Advance(); // : or ::
        return new GreenNode(SyntaxKind.LabelDeclaration, [name, colon]);
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
        if (Current.Kind == SyntaxKind.EndOfFileToken)
            return true;
        if (_position == 0)
            return false;
        return HasNewlineTrivia(_tokens[_position - 1]);
    }

    private GreenNodeBase ParseOperand()
    {
        var kind = Current.Kind;

        // Guard: if we're at EOF, produce a missing operand.
        if (kind == SyntaxKind.EndOfFileToken)
        {
            ReportMissingToken(SyntaxKind.IdentifierToken);
            return new GreenNode(
                SyntaxKind.ImmediateOperand,
                [new GreenToken(SyntaxKind.MissingToken, "")]
            );
        }

        // Condition flags: z, nz, nc
        if (IsConditionKeyword(kind))
            return ParseConditionOperand();

        // Indirect: [...]
        if (kind == SyntaxKind.OpenBracketToken)
            return ParseIndirectOperand();

        // Register keywords (only if not followed by an operator — e.g. `sp` alone is
        // a register, but `sp + $05` would be caught here too since sp is a register
        // and the binder handles the expression form)
        if (IsRegisterKeyword(kind) && !IsBinaryOperator(Peek().Kind))
            return ParseRegisterOperand();

        // Label reference (bare, not in expression)
        if (
            kind is SyntaxKind.LocalLabelToken or SyntaxKind.IdentifierToken
            && !IsBinaryOperator(Peek().Kind)
        )
            return ParseLabelOperand();

        // Everything else: immediate wrapping an expression
        return ParseImmediateOperand();
    }

    private GreenNode ParseRegisterOperand() => new(SyntaxKind.RegisterOperand, [Advance()]);

    private GreenNode ParseConditionOperand() => new(SyntaxKind.ConditionOperand, [Advance()]);

    private GreenNode ParseLabelOperand() => new(SyntaxKind.LabelOperand, [Advance()]);

    private GreenNode ParseImmediateOperand() =>
        new(SyntaxKind.ImmediateOperand, [ParseExpression()]);

    private GreenNode ParseIndirectOperand()
    {
        var children = new List<GreenNodeBase>();
        children.Add(Advance()); // [

        // Special case: [hl+] or [hl-] — post-increment/decrement syntax, not an expression
        if (
            Current.Kind == SyntaxKind.HlKeyword
            && Peek().Kind is SyntaxKind.PlusToken or SyntaxKind.MinusToken
            && Peek(2).Kind == SyntaxKind.CloseBracketToken
        )
        {
            children.Add(Advance()); // hl
            children.Add(Advance()); // + or -
        }
        else if (
            Current.Kind != SyntaxKind.CloseBracketToken
            && Current.Kind != SyntaxKind.EndOfFileToken
        )
        {
            children.Add(ParseExpression());
        }

        if (Current.Kind == SyntaxKind.CloseBracketToken)
        {
            children.Add(Advance()); // ]
        }
        else
        {
            var missing = new GreenToken(SyntaxKind.MissingToken, "");
            children.Add(missing);
            ReportMissingToken(SyntaxKind.CloseBracketToken);
        }

        return new GreenNode(SyntaxKind.IndirectOperand, children.ToArray());
    }

    // --- Pratt expression parser ---

    private GreenNodeBase ParseExpression(int parentPrecedence = 0)
    {
        var left = ParsePrefixExpression();

        while (true)
        {
            var precedence = GetBinaryPrecedence(Current.Kind);
            if (precedence == 0 || precedence <= parentPrecedence)
                break;

            var op = Advance();
            var right = ParseExpression(precedence);
            left = new GreenNode(SyntaxKind.BinaryExpression, [left, op, right]);
        }

        return left;
    }

    private GreenNodeBase ParsePrefixExpression()
    {
        // Unary operators
        if (
            Current.Kind
            is SyntaxKind.MinusToken
                or SyntaxKind.TildeToken
                or SyntaxKind.BangToken
                or SyntaxKind.PlusToken
        )
        {
            var op = Advance();
            var operand = ParsePrefixExpression();
            return new GreenNode(SyntaxKind.UnaryExpression, [op, operand]);
        }

        return ParsePrimaryExpression();
    }

    private GreenNodeBase ParsePrimaryExpression()
    {
        switch (Current.Kind)
        {
            case SyntaxKind.NumberLiteral:
            case SyntaxKind.CurrentAddressToken:
            case SyntaxKind.StringLiteral:
                return new GreenNode(SyntaxKind.LiteralExpression, [Advance()]);

            case SyntaxKind.IdentifierToken:
            case SyntaxKind.LocalLabelToken:
                return new GreenNode(SyntaxKind.NameExpression, [Advance()]);

            case SyntaxKind.OpenParenToken:
            {
                var open = Advance();
                var expr = ParseExpression();
                GreenNodeBase close;
                if (Current.Kind == SyntaxKind.CloseParenToken)
                {
                    close = Advance();
                }
                else
                {
                    close = new GreenToken(SyntaxKind.MissingToken, "");
                    ReportMissingToken(SyntaxKind.CloseParenToken);
                }
                return new GreenNode(SyntaxKind.ParenthesizedExpression, [open, expr, close]);
            }

            default:
                // Register keywords can appear in expressions (e.g. sp in `sp + $05`)
                if (IsRegisterKeyword(Current.Kind))
                    return new GreenNode(SyntaxKind.NameExpression, [Advance()]);

                // Unknown token — produce a missing expression, don't consume
                ReportMissingToken(SyntaxKind.NumberLiteral);
                return new GreenNode(
                    SyntaxKind.LiteralExpression,
                    [new GreenToken(SyntaxKind.MissingToken, "")]
                );
        }
    }

    // RGBDS precedence (higher number = tighter binding), matching C conventions:
    // 1: ||
    // 2: &&
    // 3: == !=
    // 4: < > <= >=
    // 5: |          (bitwise OR — lowest of the three bitwise ops, same as C)
    // 6: ^          (bitwise XOR)
    // 7: &          (bitwise AND — highest of the three)
    // 8: << >>
    // 9: + -
    // 10: * / %
    private static int GetBinaryPrecedence(SyntaxKind kind) =>
        kind switch
        {
            SyntaxKind.PipePipeToken => 1,
            SyntaxKind.AmpersandAmpersandToken => 2,
            SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken => 3,
            SyntaxKind.LessThanToken
            or SyntaxKind.GreaterThanToken
            or SyntaxKind.LessThanEqualsToken
            or SyntaxKind.GreaterThanEqualsToken => 4,
            SyntaxKind.PipeToken => 5,
            SyntaxKind.CaretToken => 6,
            SyntaxKind.AmpersandToken => 7,
            SyntaxKind.LessThanLessThanToken or SyntaxKind.GreaterThanGreaterThanToken => 8,
            SyntaxKind.PlusToken or SyntaxKind.MinusToken => 9,
            SyntaxKind.StarToken or SyntaxKind.SlashToken or SyntaxKind.PercentToken => 10,
            _ => 0,
        };

    private static bool IsBinaryOperator(SyntaxKind kind) => GetBinaryPrecedence(kind) > 0;

    private static bool HasNewlineTrivia(GreenToken token)
    {
        var trivia = token.TrailingTrivia;
        for (int i = 0; i < trivia.Count; i++)
        {
            var t = trivia[i];
            if (t.Kind == SyntaxKind.NewlineTrivia)
                return true;
            if (
                t.Kind == SyntaxKind.BlockCommentTrivia
                && (t.Text.Contains('\n') || t.Text.Contains('\r'))
            )
                return true;
        }
        return false;
    }

    private void ReportMissingToken(SyntaxKind expected)
    {
        // Calculate current position for the diagnostic span
        int pos = 0;
        for (int i = 0; i < _position && i < _tokens.Count; i++)
            pos += _tokens[i].FullWidth;

        _diagnostics.Report(new TextSpan(pos, 0), $"Expected '{expected}'");
    }

    private void ReportBadToken(GreenToken token)
    {
        // Calculate position of the bad token by summing all prior token widths
        int pos = 0;
        for (int i = 0; i < _tokens.Count; i++)
        {
            if (ReferenceEquals(_tokens[i], token))
                break;
            pos += _tokens[i].FullWidth;
        }

        _diagnostics.Report(
            new TextSpan(pos + token.LeadingTriviaWidth, token.Width),
            $"Unexpected token '{token.Kind}'"
        );
    }
}
