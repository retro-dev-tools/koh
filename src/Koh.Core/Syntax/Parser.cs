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

    // Section type keywords occupy a contiguous range — Rom0Keyword..OamKeyword.
    // SyntaxKind.cs documents this dependency.
    private static bool IsSectionTypeKeyword(SyntaxKind kind) =>
        kind >= SyntaxKind.Rom0Keyword && kind <= SyntaxKind.OamKeyword;

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

        // Symbol definition: identifier EQU/EQUS expr, identifier RB/RW/RL N, identifier = expr
        if (Current.Kind == SyntaxKind.IdentifierToken &&
            Peek().Kind is SyntaxKind.EquKeyword or SyntaxKind.EqusKeyword
                or SyntaxKind.RbKeyword or SyntaxKind.RwKeyword or SyntaxKind.RlKeyword
                or SyntaxKind.EqualsToken)
            return ParseSymbolDirective();

        // Directives
        if (Current.Kind == SyntaxKind.SectionKeyword)
            return ParseSectionDirective();

        if (Current.Kind is SyntaxKind.DbKeyword or SyntaxKind.DwKeyword or SyntaxKind.DsKeyword)
            return ParseDataDirective();

        // REDEF identifier EQU/EQUS expr
        if (Current.Kind == SyntaxKind.RedefKeyword)
            return ParseSymbolDirective();

        // DEF identifier = expr (not DEF( which is a function call)
        if (Current.Kind == SyntaxKind.DefKeyword && Peek().Kind != SyntaxKind.OpenParenToken)
            return ParseSymbolDirective();

        // EXPORT identifier, ...
        if (Current.Kind == SyntaxKind.ExportKeyword)
            return ParseSymbolDirective();

        // PURGE identifier, ...
        if (Current.Kind == SyntaxKind.PurgeKeyword)
            return ParseSymbolDirective();

        // Conditional assembly: IF/ELIF/ELSE/ENDC
        if (Current.Kind is SyntaxKind.IfKeyword or SyntaxKind.ElifKeyword
            or SyntaxKind.ElseKeyword or SyntaxKind.EndcKeyword)
            return ParseConditionalDirective();

        // Macro: MACRO/ENDM or identifier followed by MACRO
        if (Current.Kind == SyntaxKind.MacroKeyword || Current.Kind == SyntaxKind.EndmKeyword)
            return ParseBlockDirective(SyntaxKind.MacroDefinition);

        // Repeat: REPT/FOR — parse with expression arguments; ENDR as flat block
        if (Current.Kind is SyntaxKind.ReptKeyword or SyntaxKind.ForKeyword)
            return ParseRepeatDirective();
        if (Current.Kind == SyntaxKind.EndrKeyword)
            return ParseBlockDirective(SyntaxKind.RepeatDirective);

        // Include: INCLUDE/INCBIN
        if (Current.Kind is SyntaxKind.IncludeKeyword or SyntaxKind.IncbinKeyword)
            return ParseBlockDirective(SyntaxKind.IncludeDirective);

        // Character map directives — flat token gobble, binder handles semantics
        if (Current.Kind is SyntaxKind.CharmapKeyword or SyntaxKind.NewcharmapKeyword
            or SyntaxKind.SetcharmapKeyword or SyntaxKind.PrecharmapKeyword
            or SyntaxKind.PopcharmapKeyword)
            return ParseBlockDirective(SyntaxKind.SymbolDirective);

        // UNION control: NEXTU/ENDU/LOAD/ENDL
        if (Current.Kind is SyntaxKind.NextuKeyword or SyntaxKind.EnduKeyword
            or SyntaxKind.LoadKeyword or SyntaxKind.EndlKeyword)
            return ParseBlockDirective(SyntaxKind.DirectiveStatement);

        // SHIFT (inside macro expansion)
        if (Current.Kind == SyntaxKind.ShiftKeyword)
            return ParseBlockDirective(SyntaxKind.DirectiveStatement);

        // RS counters: RSRESET (no args), RSSET expr
        if (Current.Kind == SyntaxKind.RsresetKeyword)
            return ParseBlockDirective(SyntaxKind.DirectiveStatement);
        if (Current.Kind == SyntaxKind.RssetKeyword)
            return ParseRssetDirective();

        // ASSERT/STATIC_ASSERT — parse with expression arguments
        if (Current.Kind is SyntaxKind.AssertKeyword or SyntaxKind.StaticAssertKeyword)
            return ParseAssertDirective();

        // WARN/FAIL/PRINT/PRINTLN
        if (Current.Kind is SyntaxKind.WarnKeyword or SyntaxKind.FailKeyword
            or SyntaxKind.PrintKeyword or SyntaxKind.PrintlnKeyword)
            return ParseBlockDirective(SyntaxKind.DirectiveStatement);

        // PUSHS/POPS/PUSHO/POPO
        if (Current.Kind is SyntaxKind.PushsKeyword or SyntaxKind.PopsKeyword
            or SyntaxKind.PushoKeyword or SyntaxKind.PopoKeyword)
            return ParseBlockDirective(SyntaxKind.DirectiveStatement);

        // OPT — assembler options
        if (Current.Kind == SyntaxKind.OptKeyword)
            return ParseBlockDirective(SyntaxKind.DirectiveStatement);

        // Inline ALIGN — pad PC to alignment boundary
        if (Current.Kind == SyntaxKind.AlignKeyword)
            return ParseAlignDirective();

        // Potential macro call or unrecognized identifier at statement level.
        // Parse as MacroCall so the binder can check if it's a known macro.
        // If not a macro, the binder ignores it (produces a diagnostic for unknown statement).
        if (Current.Kind == SyntaxKind.IdentifierToken &&
            Peek().Kind is not SyntaxKind.ColonToken and not SyntaxKind.DoubleColonToken
            and not SyntaxKind.EquKeyword and not SyntaxKind.EqusKeyword
            and not SyntaxKind.RbKeyword and not SyntaxKind.RwKeyword and not SyntaxKind.RlKeyword
            and not SyntaxKind.EqualsToken)
            return ParseBlockDirective(SyntaxKind.MacroCall);

        return null; // will be handled by the safety advance in ParseCompilationUnit
    }

    private bool IsLabelStart()
    {
        if (Current.Kind is SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken)
        {
            var next = Peek();
            if (next.Kind is SyntaxKind.ColonToken or SyntaxKind.DoubleColonToken)
                return true;
            // .label\@: — LocalLabelToken + MacroParamToken + ColonToken
            if (next.Kind == SyntaxKind.MacroParamToken &&
                Peek(2).Kind is SyntaxKind.ColonToken or SyntaxKind.DoubleColonToken)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Parse IF expr / ELIF expr / ELSE / ENDC — the condition expression (if any)
    /// is parsed as a full expression. The binder evaluates it and skips branches.
    /// </summary>
    private GreenNode ParseConditionalDirective()
    {
        var children = new List<GreenNodeBase>();
        children.Add(Advance()); // IF/ELIF/ELSE/ENDC keyword

        // IF and ELIF have a condition expression
        var keyword = ((GreenToken)children[0]).Kind;
        if (keyword is SyntaxKind.IfKeyword or SyntaxKind.ElifKeyword)
        {
            if (!AtEndOfStatement())
                children.Add(ParseExpression());
        }

        return new GreenNode(SyntaxKind.ConditionalDirective, children.ToArray());
    }

    /// <summary>
    /// Parse inline ALIGN [bits[, offset]] — pad PC to alignment boundary.
    /// </summary>
    private GreenNode ParseAlignDirective()
    {
        var children = new List<GreenNodeBase>();
        children.Add(Advance()); // ALIGN keyword
        if (!AtEndOfStatement())
        {
            children.Add(ParseExpression()); // alignment bits
            if (!AtEndOfStatement() && Current.Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance()); // comma
                if (!AtEndOfStatement())
                    children.Add(ParseExpression()); // offset
            }
        }
        return new GreenNode(SyntaxKind.DirectiveStatement, children.ToArray());
    }

    /// <summary>
    /// Parse RSSET expr — sets the RS counter to a specific value.
    /// </summary>
    private GreenNode ParseRssetDirective()
    {
        var children = new List<GreenNodeBase>();
        children.Add(Advance()); // RSSET keyword
        if (!AtEndOfStatement())
            children.Add(ParseExpression());
        return new GreenNode(SyntaxKind.DirectiveStatement, children.ToArray());
    }

    /// <summary>
    /// Parse ASSERT expr [, "message"] / STATIC_ASSERT expr [, "message"].
    /// The condition expression is parsed so the binder can evaluate it.
    /// </summary>
    private GreenNode ParseAssertDirective()
    {
        var children = new List<GreenNodeBase>();
        children.Add(Advance()); // ASSERT or STATIC_ASSERT keyword

        if (!AtEndOfStatement())
        {
            // Optional severity keyword: ASSERT WARN, expr / ASSERT FAIL, expr / ASSERT FATAL, expr
            if (Current.Kind is SyntaxKind.WarnKeyword or SyntaxKind.FailKeyword or SyntaxKind.FatalKeyword
                && Peek().Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance()); // severity keyword
                children.Add(Advance()); // comma after severity
            }

            children.Add(ParseExpression()); // condition

            // Optional comma + message string
            while (!AtEndOfStatement() && Current.Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance()); // comma
                if (!AtEndOfStatement())
                    children.Add(Advance()); // message token (string literal or other)
            }
        }

        return new GreenNode(SyntaxKind.DirectiveStatement, children.ToArray());
    }

    /// <summary>
    /// Parse REPT count / FOR var, start, stop [, step].
    /// The keyword is consumed, then comma-separated expressions are parsed.
    /// </summary>
    private GreenNode ParseRepeatDirective()
    {
        var children = new List<GreenNodeBase>();
        children.Add(Advance()); // REPT or FOR keyword

        // Parse comma-separated arguments as expressions
        if (!AtEndOfStatement())
        {
            // FOR: first argument may be an identifier (variable name)
            children.Add(ParseExpression());

            while (!AtEndOfStatement() && Current.Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance()); // comma
                children.Add(ParseExpression());
            }
        }

        return new GreenNode(SyntaxKind.RepeatDirective, children.ToArray());
    }

    /// <summary>
    /// Parse a block directive as a flat token sequence on the current line.
    /// Always consumes at least the keyword token, then any remaining tokens.
    /// Used for MACRO/ENDM, REPT/FOR/ENDR, INCLUDE/INCBIN, NEXTU/ENDU, LOAD/ENDL, SHIFT.
    /// </summary>
    private GreenNode ParseBlockDirective(SyntaxKind nodeKind)
    {
        var children = new List<GreenNodeBase>();
        // Always consume at least the leading keyword
        children.Add(Advance());
        while (!AtEndOfStatement())
            children.Add(Advance());
        return new GreenNode(nodeKind, children.ToArray());
    }

    private GreenNode ParseSectionDirective()
    {
        var children = new List<GreenNodeBase>();
        children.Add(Advance()); // SECTION keyword

        // Optional FRAGMENT or UNION modifier
        if (Current.Kind is SyntaxKind.FragmentKeyword or SyntaxKind.UnionKeyword)
            children.Add(Advance());

        // Section name (string literal)
        if (Current.Kind == SyntaxKind.StringLiteral)
            children.Add(Advance());

        // Comma separating name from type
        if (Current.Kind == SyntaxKind.CommaToken)
            children.Add(Advance());

        // Memory type keyword: ROM0, ROMX, WRAM0, WRAMX, VRAM, HRAM, SRAM, OAM
        if (IsSectionTypeKeyword(Current.Kind))
            children.Add(Advance());

        // Bracket groups [$addr], [bank(n)], [align(n)] — consumed flat as tokens.
        // SectionHeaderParser re-scans these tokens to extract address/bank values.
        while (!AtEndOfStatement())
            children.Add(Advance());

        return new GreenNode(SyntaxKind.SectionDirective, children.ToArray());
    }

    private GreenNode ParseSymbolDirective()
    {
        return Current.Kind switch
        {
            SyntaxKind.RedefKeyword  => ParseEquDefinition(),
            SyntaxKind.DefKeyword    => ParseDefDefinition(),
            SyntaxKind.ExportKeyword => ParseSymbolList(),
            SyntaxKind.PurgeKeyword  => ParseSymbolList(),
            _                        => ParseEquDefinition(), // name EQU/EQUS expr
        };
    }

    // Handles:  name EQU expr
    //           name EQUS expr
    //           REDEF name EQU expr
    //           REDEF name EQUS expr
    private GreenNode ParseEquDefinition()
    {
        var children = new List<GreenNodeBase>();

        if (Current.Kind == SyntaxKind.RedefKeyword)
            children.Add(Advance()); // REDEF

        System.Diagnostics.Debug.Assert(
            Current.Kind == SyntaxKind.IdentifierToken,
            $"ParseEquDefinition expected IdentifierToken for symbol name, got {Current.Kind}"
        );
        children.Add(Advance()); // symbol name (IdentifierToken)

        System.Diagnostics.Debug.Assert(
            Current.Kind is SyntaxKind.EquKeyword or SyntaxKind.EqusKeyword
                or SyntaxKind.RbKeyword or SyntaxKind.RwKeyword or SyntaxKind.RlKeyword
                or SyntaxKind.EqualsToken,
            $"ParseEquDefinition expected EQU/EQUS/RB/RW/RL/= keyword, got {Current.Kind}"
        );
        children.Add(Advance()); // EQU, EQUS, RB, RW, RL, or = token

        if (!AtEndOfStatement())
            children.Add(ParseExpression()); // value

        return new GreenNode(SyntaxKind.SymbolDirective, children.ToArray());
    }

    // Handles:  DEF name = expr, DEF name EQU expr, DEF name EQUS expr
    private GreenNode ParseDefDefinition()
    {
        var children = new List<GreenNodeBase>();
        children.Add(Advance());                               // DEF
        children.Add(Advance());                               // symbol name
        // Accept both = and EQU/EQUS/RB/RW/RL after the name
        if (Current.Kind is SyntaxKind.EquKeyword or SyntaxKind.EqusKeyword
            or SyntaxKind.RbKeyword or SyntaxKind.RwKeyword or SyntaxKind.RlKeyword)
            children.Add(Advance());
        else
            children.Add(ExpectToken(SyntaxKind.EqualsToken)); // =
        if (!AtEndOfStatement())
            children.Add(ParseExpression());                   // value
        return new GreenNode(SyntaxKind.SymbolDirective, children.ToArray());
    }

    // Handles:  EXPORT sym [, sym]*
    //           PURGE  sym [, sym]*
    private GreenNode ParseSymbolList()
    {
        var children = new List<GreenNodeBase>();
        children.Add(Advance()); // EXPORT or PURGE keyword

        if (!AtEndOfStatement())
        {
            children.Add(Advance()); // first symbol

            while (!AtEndOfStatement() && Current.Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance()); // comma
                children.Add(ExpectToken(SyntaxKind.IdentifierToken)); // next symbol
            }
        }

        return new GreenNode(SyntaxKind.SymbolDirective, children.ToArray());
    }

    private GreenNode ParseDataDirective()
    {
        var children = new List<GreenNodeBase>();
        children.Add(Advance()); // DB/DW/DS keyword

        // Parse comma-separated expressions
        if (!AtEndOfStatement())
        {
            children.Add(ParseExpression());

            while (!AtEndOfStatement() && Current.Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance()); // comma
                children.Add(ParseExpression());
            }
        }

        return new GreenNode(SyntaxKind.DataDirective, children.ToArray());
    }

    private GreenNode ParseLabelDeclaration()
    {
        var children = new List<GreenNodeBase>();
        children.Add(Advance()); // identifier or local label token
        // Handle .label\@: — consume optional MacroParamToken before the colon
        if (Current.Kind == SyntaxKind.MacroParamToken)
            children.Add(Advance());
        children.Add(Advance()); // : or ::
        return new GreenNode(SyntaxKind.LabelDeclaration, children.ToArray());
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
            case SyntaxKind.MacroParamToken:
                return new GreenNode(SyntaxKind.LiteralExpression, [Advance()]);

            case SyntaxKind.IdentifierToken:
            case SyntaxKind.LocalLabelToken:
                return new GreenNode(SyntaxKind.NameExpression, [Advance()]);

            // Built-in functions: HIGH(...), LOW(...), BANK(...), etc.
            case SyntaxKind.HighKeyword:
            case SyntaxKind.LowKeyword:
            case SyntaxKind.BankKeyword:
            case SyntaxKind.SizeofKeyword:
            case SyntaxKind.StartofKeyword:
            case SyntaxKind.DefKeyword:
            case SyntaxKind.IsConstKeyword:
            case SyntaxKind.StrlenKeyword:
            case SyntaxKind.StrcatKeyword:
            case SyntaxKind.StrsubKeyword:
            case SyntaxKind.RevcharKeyword:
                return ParseFunctionCallExpression();

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

    private GreenNode ParseFunctionCallExpression()
    {
        var children = new List<GreenNodeBase>
        {
            Advance(), // function keyword
            ExpectToken(SyntaxKind.OpenParenToken),
        };

        // Parse comma-separated arguments
        if (Current.Kind != SyntaxKind.CloseParenToken && Current.Kind != SyntaxKind.EndOfFileToken)
        {
            children.Add(ParseExpression());

            while (Current.Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance()); // comma
                var before = _position;
                children.Add(ParseExpression());
                // Safety: if ParseExpression didn't advance, force-consume to avoid infinite loop
                if (_position == before && Current.Kind != SyntaxKind.CloseParenToken)
                {
                    var bad = Advance();
                    ReportBadToken(bad);
                }
            }
        }

        children.Add(ExpectToken(SyntaxKind.CloseParenToken));
        return new GreenNode(SyntaxKind.FunctionCallExpression, children.ToArray());
    }

    private GreenToken ExpectToken(SyntaxKind expected)
    {
        if (Current.Kind == expected)
            return Advance();

        ReportMissingToken(expected);
        return new GreenToken(SyntaxKind.MissingToken, "");
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
