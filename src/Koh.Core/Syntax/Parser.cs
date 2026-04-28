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
    private int _currentOffset; // running byte offset — avoids O(n²) in error reporting

    public Parser(SourceText text)
    {
        _text = text;
        var (tokens, lexerDiagnostics) = Lexer.LexAll(text);
        _tokens = tokens;
        foreach (var d in lexerDiagnostics)
            _diagnostics.Report(d.Span, d.Message, d.Severity);
    }

    private GreenToken Current => _position < _tokens.Count ? _tokens[_position] : _tokens[^1];

    private GreenToken Peek(int offset = 1)
    {
        int index = _position + offset;
        return index < _tokens.Count ? _tokens[index] : _tokens[^1];
    }

    private GreenToken Advance()
    {
        var token = Current;
        if (_position < _tokens.Count - 1)
        {
            _currentOffset += token.FullWidth;
            _position++;
        }
        return token;
    }

    public SyntaxTree Parse()
    {
        var root = ParseCompilationUnit();
        var redRoot = new SyntaxNode(root, null, 0);
        return SyntaxTree.Create(_text, redRoot, _diagnostics.ToList());
    }

    // =========================================================================
    // Keyword range checks — these depend on contiguous ranges in SyntaxKind.cs
    // =========================================================================

    private static bool IsInstructionKeyword(SyntaxKind kind) =>
        kind >= SyntaxKind.NopKeyword && kind <= SyntaxKind.LdhKeyword;

    private static bool IsSectionTypeKeyword(SyntaxKind kind) =>
        kind >= SyntaxKind.Rom0Keyword && kind <= SyntaxKind.OamKeyword;

    // z, nz, nc — NOT CKeyword (always RegisterOperand; binder disambiguates).
    private static bool IsConditionKeyword(SyntaxKind kind) =>
        kind >= SyntaxKind.ZKeyword && kind <= SyntaxKind.NcKeyword;

    private static bool IsRegisterKeyword(SyntaxKind kind) =>
        kind >= SyntaxKind.AKeyword && kind <= SyntaxKind.DeKeyword;

    // HighKeyword..StrfmtKeyword must remain contiguous in SyntaxKind.cs.
    private static bool IsBuiltInFunctionKeyword(SyntaxKind kind) =>
        kind >= SyntaxKind.HighKeyword && kind <= SyntaxKind.StrfmtKeyword;

    private static bool IsKeyword(SyntaxKind kind) =>
        kind >= SyntaxKind.NopKeyword && kind <= SyntaxKind.StrfmtKeyword;

    // =========================================================================
    // Top-level
    // =========================================================================

    private GreenNode ParseCompilationUnit()
    {
        var children = new List<GreenNodeBase>();

        while (Current.Kind != SyntaxKind.EndOfFileToken)
        {
            var startPos = _position;
            var statement = ParseStatement();
            if (statement != null)
                children.Add(statement);

            while (Current.Kind == SyntaxKind.DoubleColonToken)
                Advance();

            // Safety: force-advance to avoid infinite loop.
            // Suppress diagnostics for { } — may be EQUS interpolation markers.
            if (_position == startPos)
            {
                var bad = Advance();
                if (bad.Kind != SyntaxKind.BadToken || (bad.Text != "{" && bad.Text != "}"))
                    ReportBadToken(bad);
            }
        }

        children.Add(Advance()); // EOF
        return new GreenNode(SyntaxKind.CompilationUnit, children.ToArray());
    }

    private GreenNodeBase? ParseStatement()
    {
        if (IsLabelStart())
            return ParseLabelDeclaration();

        if (IsInstructionKeyword(Current.Kind))
            return ParseInstruction();

        // name EQU/EQUS/RB/RW/RL/= expr
        if (Current.Kind == SyntaxKind.IdentifierToken &&
            Peek().Kind is SyntaxKind.EquKeyword or SyntaxKind.EqusKeyword
                or SyntaxKind.RbKeyword or SyntaxKind.RwKeyword or SyntaxKind.RlKeyword
                or SyntaxKind.EqualsToken)
            return ParseEquDefinition();

        return Current.Kind switch
        {
            SyntaxKind.SectionKeyword => ParseSectionDirective(),

            SyntaxKind.DbKeyword or SyntaxKind.DwKeyword
                or SyntaxKind.DlKeyword or SyntaxKind.DsKeyword => ParseDataDirective(),

            SyntaxKind.RedefKeyword => ParseEquDefinition(),
            SyntaxKind.DefKeyword when Peek().Kind != SyntaxKind.OpenParenToken => ParseDefDefinition(),
            SyntaxKind.ExportKeyword or SyntaxKind.PurgeKeyword => ParseSymbolList(),

            SyntaxKind.IfKeyword or SyntaxKind.ElifKeyword
                or SyntaxKind.ElseKeyword or SyntaxKind.EndcKeyword => ParseConditionalDirective(),

            SyntaxKind.MacroKeyword => ParseBlockDirective(SyntaxKind.MacroDefinition),
            SyntaxKind.EndmKeyword => ParseTerminatorDirective(SyntaxKind.MacroDefinition, "ENDM"),

            SyntaxKind.ReptKeyword or SyntaxKind.ForKeyword => ParseRepeatDirective(),
            SyntaxKind.EndrKeyword => ParseTerminatorDirective(SyntaxKind.RepeatDirective, "ENDR"),

            SyntaxKind.IncludeKeyword or SyntaxKind.IncbinKeyword
                => ParseBlockDirective(SyntaxKind.IncludeDirective),

            SyntaxKind.CharmapKeyword or SyntaxKind.NewcharmapKeyword
                or SyntaxKind.SetcharmapKeyword or SyntaxKind.PrecharmapKeyword
                or SyntaxKind.PopcharmapKeyword => ParseBlockDirective(SyntaxKind.SymbolDirective),

            SyntaxKind.UnionKeyword or SyntaxKind.NextuKeyword or SyntaxKind.EnduKeyword
                or SyntaxKind.LoadKeyword or SyntaxKind.EndlKeyword
                or SyntaxKind.ShiftKeyword or SyntaxKind.BreakKeyword
                or SyntaxKind.RsresetKeyword
                or SyntaxKind.WarnKeyword or SyntaxKind.FailKeyword
                or SyntaxKind.PushsKeyword or SyntaxKind.PopsKeyword
                or SyntaxKind.PushoKeyword or SyntaxKind.PopoKeyword
                or SyntaxKind.OptKeyword => ParseBlockDirective(SyntaxKind.DirectiveStatement),

            SyntaxKind.RssetKeyword => ParseRssetDirective(),
            SyntaxKind.AssertKeyword or SyntaxKind.StaticAssertKeyword => ParseAssertDirective(),
            SyntaxKind.PrintKeyword or SyntaxKind.PrintlnKeyword => ParsePrintDirective(),
            SyntaxKind.AlignKeyword => ParseAlignDirective(),

            // Macro call: identifier not already matched as label or EQU definition
            SyntaxKind.IdentifierToken
                when Peek().Kind is not SyntaxKind.ColonToken and not SyntaxKind.DoubleColonToken
                    and not SyntaxKind.EquKeyword and not SyntaxKind.EqusKeyword
                    and not SyntaxKind.RbKeyword and not SyntaxKind.RwKeyword
                    and not SyntaxKind.RlKeyword and not SyntaxKind.EqualsToken
                => ParseBlockDirective(SyntaxKind.MacroCall),

            _ => null, // safety advance in ParseCompilationUnit handles this
        };
    }

    private bool IsLabelStart()
    {
        if (Current.Kind is SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken)
        {
            var next = Peek();
            if (next.Kind is SyntaxKind.ColonToken or SyntaxKind.DoubleColonToken)
                return true;
            if (next.Kind == SyntaxKind.MacroParamToken &&
                Peek(2).Kind is SyntaxKind.ColonToken or SyntaxKind.DoubleColonToken)
                return true;
            // Compound label: Alpha.local1: or Alpha.local1 (standalone)
            if (Current.Kind == SyntaxKind.IdentifierToken && next.Kind == SyntaxKind.LocalLabelToken)
            {
                var after = Peek(2);
                if (after.Kind is SyntaxKind.ColonToken or SyntaxKind.DoubleColonToken
                    or SyntaxKind.EndOfFileToken)
                    return true;
                if (HasNewlineTrivia(next))
                    return true;
            }
        }

        // Standalone local label on its own line
        if (Current.Kind == SyntaxKind.LocalLabelToken &&
            (Peek().Kind == SyntaxKind.EndOfFileToken || HasNewlineTrivia(Current)))
            return true;

        // Anonymous label: bare colon at statement start. Content may follow on the
        // same line (e.g., ":	ld a, [hli]") — the colon is always the label; the rest
        // is parsed as the next statement by ParseCompilationUnit's main loop.
        if (Current.Kind == SyntaxKind.ColonToken)
            return true;

        return false;
    }

    // =========================================================================
    // Directive parsers
    // =========================================================================

    private GreenNode ParseConditionalDirective()
    {
        var children = new List<GreenNodeBase>();
        children.Add(Advance()); // IF/ELIF/ELSE/ENDC

        var keyword = ((GreenToken)children[0]).Kind;
        if (keyword is SyntaxKind.IfKeyword or SyntaxKind.ElifKeyword)
        {
            if (!AtEndOfStatement())
                children.Add(ParseExpression());
        }
        else if (!AtEndOfStatement())
        {
            _diagnostics.Report(new TextSpan(_currentOffset, 0),
                $"Unexpected tokens after {(keyword == SyntaxKind.EndcKeyword ? "ENDC" : "ELSE")}");
            while (!AtEndOfStatement()) Advance();
        }

        return new GreenNode(SyntaxKind.ConditionalDirective, children.ToArray());
    }

    private GreenNode ParseAlignDirective()
    {
        var children = new List<GreenNodeBase>();
        children.Add(Advance()); // ALIGN
        if (!AtEndOfStatement())
        {
            children.Add(ParseExpression());
            if (!AtEndOfStatement() && Current.Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance());
                if (!AtEndOfStatement())
                    children.Add(ParseExpression());
            }
        }
        return new GreenNode(SyntaxKind.DirectiveStatement, children.ToArray());
    }

    private GreenNode ParseRssetDirective()
    {
        var children = new List<GreenNodeBase> { Advance() }; // RSSET
        if (!AtEndOfStatement())
            children.Add(ParseExpression());
        return new GreenNode(SyntaxKind.DirectiveStatement, children.ToArray());
    }

    private GreenNode ParseAssertDirective()
    {
        var children = new List<GreenNodeBase> { Advance() }; // ASSERT / STATIC_ASSERT

        if (!AtEndOfStatement())
        {
            if (Current.Kind is SyntaxKind.WarnKeyword or SyntaxKind.FailKeyword or SyntaxKind.FatalKeyword
                && Peek().Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance()); // severity
                children.Add(Advance()); // comma
            }
            children.Add(ParseExpression());
            while (!AtEndOfStatement() && Current.Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance());
                if (!AtEndOfStatement())
                    children.Add(Advance()); // message token
            }
        }

        return new GreenNode(SyntaxKind.DirectiveStatement, children.ToArray());
    }

    private GreenNode ParseRepeatDirective()
    {
        var children = new List<GreenNodeBase> { Advance() }; // REPT / FOR

        if (!AtEndOfStatement())
        {
            children.Add(ParseExpression());
            while (!AtEndOfStatement() && Current.Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance());
                children.Add(ParseExpression());
            }
        }

        return new GreenNode(SyntaxKind.RepeatDirective, children.ToArray());
    }

    // Flat token-gobble for directives whose structure is validated by the binder.
    // This defers structural validation (e.g. "INCLUDE missing file argument") to a
    // later phase, simplifying the parser at the cost of later error reporting.
    private GreenNode ParseBlockDirective(SyntaxKind nodeKind)
    {
        var children = new List<GreenNodeBase> { Advance() };
        while (!AtEndOfStatement())
            children.Add(Advance());
        return new GreenNode(nodeKind, children.ToArray());
    }

    private GreenNode ParseTerminatorDirective(SyntaxKind nodeKind, string name)
    {
        var children = new List<GreenNodeBase> { Advance() };
        if (!AtEndOfStatement())
        {
            _diagnostics.Report(new TextSpan(_currentOffset, 0), $"Unexpected tokens after {name}");
            while (!AtEndOfStatement()) children.Add(Advance());
        }
        return new GreenNode(nodeKind, children.ToArray());
    }

    private GreenNode ParsePrintDirective()
    {
        var children = new List<GreenNodeBase> { Advance() }; // PRINT / PRINTLN

        if (!AtEndOfStatement())
        {
            if (IsRegisterKeyword(Current.Kind))
            {
                var saved = _position;
                Advance();
                if (AtEndOfStatement())
                {
                    _diagnostics.Report(new TextSpan(_currentOffset, 0),
                        "PRINT/PRINTLN requires a string or expression argument, not a bare register name");
                    _position = saved;
                    children.Add(Advance());
                }
                else
                {
                    _position = saved;
                    children.Add(ParseExpression());
                }
            }
            else
            {
                children.Add(ParseExpression());
            }
        }
        return new GreenNode(SyntaxKind.DirectiveStatement, children.ToArray());
    }

    private GreenNode ParseSectionDirective()
    {
        var children = new List<GreenNodeBase> { Advance() }; // SECTION

        if (Current.Kind is SyntaxKind.FragmentKeyword or SyntaxKind.UnionKeyword)
            children.Add(Advance());
        if (Current.Kind == SyntaxKind.StringLiteral)
            children.Add(Advance());
        if (Current.Kind == SyntaxKind.CommaToken)
            children.Add(Advance());
        if (IsSectionTypeKeyword(Current.Kind))
            children.Add(Advance());

        // Remaining bracket groups consumed flat — SectionHeaderParser re-scans them.
        while (!AtEndOfStatement())
            children.Add(Advance());

        return new GreenNode(SyntaxKind.SectionDirective, children.ToArray());
    }

    // =========================================================================
    // Symbol directives
    // =========================================================================

    // name EQU/EQUS/RB/RW/RL/= expr  |  REDEF name EQU/EQUS expr
    private GreenNode ParseEquDefinition()
    {
        var children = new List<GreenNodeBase>();

        if (Current.Kind == SyntaxKind.RedefKeyword)
            children.Add(Advance());

        if (Current.Kind != SyntaxKind.IdentifierToken)
        {
            ReportMissingToken(SyntaxKind.IdentifierToken);
            return new GreenNode(SyntaxKind.SymbolDirective, children.ToArray());
        }
        children.Add(Advance());

        if (Current.Kind is not (SyntaxKind.EquKeyword or SyntaxKind.EqusKeyword
                or SyntaxKind.RbKeyword or SyntaxKind.RwKeyword or SyntaxKind.RlKeyword
                or SyntaxKind.EqualsToken))
        {
            ReportMissingToken(SyntaxKind.EqualsToken);
            return new GreenNode(SyntaxKind.SymbolDirective, children.ToArray());
        }
        children.Add(Advance());

        if (!AtEndOfStatement())
            children.Add(ParseExpression());

        return new GreenNode(SyntaxKind.SymbolDirective, children.ToArray());
    }

    // DEF name = expr  |  DEF name EQU/EQUS expr
    private GreenNode ParseDefDefinition()
    {
        var children = new List<GreenNodeBase>
        {
            Advance(),                           // DEF
        };
        // Name may be an interpolated identifier: {equs}name, name{equs}, etc.
        // Consume all tokens that form the name (BadToken("{"), identifiers, BadToken("}"))
        // until we reach a keyword that starts the definition (EQU, EQUS, =, RB, RW, RL).
        children.AddRange(ConsumeInterpolatedName());

        if (Current.Kind is SyntaxKind.EquKeyword or SyntaxKind.EqusKeyword
            or SyntaxKind.RbKeyword or SyntaxKind.RwKeyword or SyntaxKind.RlKeyword)
            children.Add(Advance());
        else
            children.Add(ExpectToken(SyntaxKind.EqualsToken));
        if (!AtEndOfStatement())
            children.Add(ParseExpression());
        return new GreenNode(SyntaxKind.SymbolDirective, children.ToArray());
    }

    // EXPORT sym [, sym]*  |  PURGE sym [, sym]*
    private GreenNode ParseSymbolList()
    {
        var children = new List<GreenNodeBase> { Advance() };
        if (!AtEndOfStatement())
        {
            // First symbol (may be interpolated)
            children.AddRange(ConsumeInterpolatedName());
            while (!AtEndOfStatement() && Current.Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance()); // comma
                if (!AtEndOfStatement())
                    children.AddRange(ConsumeInterpolatedName());
            }
        }
        return new GreenNode(SyntaxKind.SymbolDirective, children.ToArray());
    }

    /// <summary>
    /// Consume a symbol name that may contain {equs} interpolation sequences.
    /// Handles: plain identifier, {equs}suffix, prefix{equs}, prefix{equs}suffix, etc.
    /// Does NOT report errors for BadToken("{") or BadToken("}") — they are expected in
    /// interpolated names and will be resolved by the AssemblyExpander.
    /// </summary>
    private IEnumerable<GreenToken> ConsumeInterpolatedName()
    {
        var parts = new List<GreenToken>();
        // Consume leading { sequences and identifier parts until we hit a non-name token
        while (!AtEndOfStatement())
        {
            var kind = Current.Kind;
            if (kind == SyntaxKind.BadToken && Current.Text == "{")
            {
                parts.Add(Advance()); // {
                // consume name inside braces
                while (!AtEndOfStatement() && !(Current.Kind == SyntaxKind.BadToken && Current.Text == "}"))
                    parts.Add(Advance());
                if (!AtEndOfStatement() && Current.Kind == SyntaxKind.BadToken && Current.Text == "}")
                    parts.Add(Advance()); // }
                continue;
            }
            if (kind == SyntaxKind.IdentifierToken || kind == SyntaxKind.LocalLabelToken)
            {
                parts.Add(Advance());
                continue;
            }
            // Allow keywords that appear as raw identifiers (#DEF etc.)
            // Merge '#' + keyword into a single IdentifierToken to match ConsumeRawIdentifierOrAdvance.
            if (kind == SyntaxKind.HashToken && IsKeyword(Peek().Kind))
            {
                var hash = Advance(); // #
                var kw = Advance();   // keyword
                parts.Add(new GreenToken(SyntaxKind.IdentifierToken, "#" + kw.Text,
                    hash.LeadingTrivia, kw.TrailingTrivia));
                continue;
            }
            break; // stop at EQU, EQUS, =, RB, etc.
        }
        if (parts.Count == 0)
        {
            // Nothing consumed — advance one token to avoid infinite loops, emit missing
            ReportMissingToken(SyntaxKind.IdentifierToken);
            parts.Add(new GreenToken(SyntaxKind.MissingToken, ""));
        }
        return parts;
    }

    // =========================================================================
    // Data directives
    // =========================================================================

    private GreenNode ParseDataDirective()
    {
        var children = new List<GreenNodeBase>();
        var keyword = Advance();
        children.Add(keyword);

        // DS ALIGN[n, offset], fill
        if (keyword.Kind == SyntaxKind.DsKeyword && Current.Kind == SyntaxKind.AlignKeyword)
        {
            children.Add(Advance()); // ALIGN
            if (Current.Kind == SyntaxKind.OpenBracketToken)
            {
                children.Add(Advance()); // [
                children.Add(ParseExpression());
                if (Current.Kind == SyntaxKind.CommaToken)
                {
                    children.Add(Advance());
                    children.Add(ParseExpression());
                }
                if (Current.Kind == SyntaxKind.CloseBracketToken)
                    children.Add(Advance()); // ]
            }
            if (!AtEndOfStatement() && Current.Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance());
                if (!AtEndOfStatement())
                    children.Add(ParseExpression());
            }
            return new GreenNode(SyntaxKind.DataDirective, children.ToArray());
        }

        // Comma-separated expressions with trailing comma support
        if (!AtEndOfStatement())
        {
            children.Add(ParseExpression());
            while (!AtEndOfStatement() && Current.Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance());
                if (AtEndOfStatement())
                {
                    ReportTrailingComma();
                    break;
                }
                children.Add(ParseExpression());
            }
        }

        return new GreenNode(SyntaxKind.DataDirective, children.ToArray());
    }

    // =========================================================================
    // Labels
    // =========================================================================

    private GreenNode ParseLabelDeclaration()
    {
        // Anonymous label: bare colon
        if (Current.Kind == SyntaxKind.ColonToken)
            return new GreenNode(SyntaxKind.LabelDeclaration, [Advance()]);

        var children = new List<GreenNodeBase> { Advance() }; // identifier or local label

        // Compound label: Identifier + LocalLabel (e.g. Alpha.local1)
        if (Current.Kind == SyntaxKind.LocalLabelToken &&
            children[0] is GreenToken firstToken &&
            firstToken.Kind == SyntaxKind.IdentifierToken &&
            !HasNewlineTrivia(firstToken))
        {
            var localToken = Advance();
            children[0] = new GreenToken(SyntaxKind.IdentifierToken,
                firstToken.Text + localToken.Text,
                firstToken.LeadingTrivia, localToken.TrailingTrivia);
        }

        if (Current.Kind == SyntaxKind.MacroParamToken)
            children.Add(Advance());
        if (Current.Kind is SyntaxKind.ColonToken or SyntaxKind.DoubleColonToken)
            children.Add(Advance());
        return new GreenNode(SyntaxKind.LabelDeclaration, children.ToArray());
    }

    // =========================================================================
    // Instructions
    // =========================================================================

    private GreenNode ParseInstruction()
    {
        var children = new List<GreenNodeBase> { Advance() }; // mnemonic

        if (!AtEndOfStatement())
        {
            children.Add(ParseOperand());
            while (!AtEndOfStatement() && Current.Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance());
                children.Add(ParseOperand());
            }
        }

        return new GreenNode(SyntaxKind.InstructionStatement, children.ToArray());
    }

    // =========================================================================
    // Statement boundary detection
    // =========================================================================

    // At _position == 0, returns false — no previous token to check.
    // Leading newlines on the very first token are not detected, but the first
    // token always starts at file position 0 in practice.
    private bool AtEndOfStatement()
    {
        if (Current.Kind is SyntaxKind.EndOfFileToken or SyntaxKind.DoubleColonToken)
            return true;
        return _position > 0 && HasNewlineTrivia(_tokens[_position - 1]);
    }

    // =========================================================================
    // Operand parsing
    // =========================================================================

    private GreenNodeBase ParseOperand()
    {
        var kind = Current.Kind;

        if (kind == SyntaxKind.EndOfFileToken)
        {
            ReportMissingToken(SyntaxKind.IdentifierToken);
            return new GreenNode(SyntaxKind.ImmediateOperand, [new GreenToken(SyntaxKind.MissingToken, "")]);
        }

        if (IsConditionKeyword(kind))
            return new GreenNode(SyntaxKind.ConditionOperand, [Advance()]);
        if (kind == SyntaxKind.BangToken && IsNegatedCondition())
            return ParseNegatedConditionOperand();

        if (kind == SyntaxKind.OpenBracketToken)
            return ParseIndirectOperand();

        if (IsRegisterKeyword(kind) && !IsBinaryOperator(Peek().Kind))
            return new GreenNode(SyntaxKind.RegisterOperand, [Advance()]);

        if (kind is SyntaxKind.AnonLabelForwardToken or SyntaxKind.AnonLabelBackwardToken)
        {
            if (!IsBinaryOperator(Peek().Kind))
                return new GreenNode(SyntaxKind.LabelOperand, [Advance()]);
            return new GreenNode(SyntaxKind.ImmediateOperand, [ParseExpression()]);
        }

        if (kind is SyntaxKind.LocalLabelToken or SyntaxKind.IdentifierToken)
        {
            var lookAhead = Peek();
            var afterLabel = lookAhead.Kind == SyntaxKind.MacroParamToken ? Peek(2) : lookAhead;
            if (!IsBinaryOperator(afterLabel.Kind))
                return ParseLabelOperand();
        }

        return new GreenNode(SyntaxKind.ImmediateOperand, [ParseExpression()]);
    }

    // Called only when Current.Kind == BangToken. Peeks past consecutive ! tokens.
    private bool IsNegatedCondition()
    {
        int offset = 1;
        while (Peek(offset).Kind == SyntaxKind.BangToken)
            offset++;
        var target = Peek(offset);
        return IsConditionKeyword(target.Kind) || target.Kind == SyntaxKind.CKeyword;
    }

    // Leading trivia from ! tokens is intentionally dropped — the ! tokens are
    // purely semantic modifiers, and round-trip fidelity is not a current goal.
    private GreenNode ParseNegatedConditionOperand()
    {
        int bangCount = 0;
        while (Current.Kind == SyntaxKind.BangToken)
        {
            Advance();
            bangCount++;
        }

        var condToken = Advance();
        if (bangCount % 2 != 0)
        {
            var (invertedKind, invertedText) = condToken.Kind switch
            {
                SyntaxKind.ZKeyword  => (SyntaxKind.NzKeyword, "nz"),
                SyntaxKind.NzKeyword => (SyntaxKind.ZKeyword,  "z"),
                SyntaxKind.NcKeyword => (SyntaxKind.CKeyword,  "c"),
                SyntaxKind.CKeyword  => (SyntaxKind.NcKeyword, "nc"),
                _                    => (condToken.Kind, condToken.Text),
            };
            condToken = new GreenToken(invertedKind, invertedText,
                condToken.LeadingTrivia, condToken.TrailingTrivia);
        }

        return new GreenNode(SyntaxKind.ConditionOperand, [condToken]);
    }

    private GreenNode ParseLabelOperand()
    {
        var children = new List<GreenNodeBase> { Advance() };
        if (Current.Kind == SyntaxKind.LocalLabelToken &&
            children[0] is GreenToken firstToken &&
            firstToken.Kind == SyntaxKind.IdentifierToken &&
            !HasNewlineTrivia(firstToken))
        {
            var localToken = Advance();
            children[0] = new GreenToken(SyntaxKind.IdentifierToken,
                firstToken.Text + localToken.Text,
                firstToken.LeadingTrivia, localToken.TrailingTrivia);
        }
        // \@ suffix is part of the label — consuming it here ensures its width
        // is included in FullWidth sums (critical for position calculations).
        if (Current.Kind == SyntaxKind.MacroParamToken)
            children.Add(Advance());
        return new GreenNode(SyntaxKind.LabelOperand, children.ToArray());
    }

    private GreenNode ParseIndirectOperand()
    {
        var children = new List<GreenNodeBase> { Advance() }; // [

        // [HL+] / [HL-] — post-increment/decrement, not an expression
        if (Current.Kind == SyntaxKind.HlKeyword
            && Peek().Kind is SyntaxKind.PlusToken or SyntaxKind.MinusToken
            && Peek(2).Kind == SyntaxKind.CloseBracketToken)
        {
            children.Add(Advance()); // hl
            children.Add(Advance()); // + or -
        }
        // [hli] / [hld] — RGBDS aliases for [HL+] / [HL-]
        else if (Current.Kind is SyntaxKind.HliKeyword or SyntaxKind.HldKeyword)
        {
            children.Add(Advance());
        }
        else if (Current.Kind is not SyntaxKind.CloseBracketToken and not SyntaxKind.EndOfFileToken)
        {
            children.Add(ParseExpression());
        }

        if (Current.Kind == SyntaxKind.CloseBracketToken)
            children.Add(Advance());
        else
        {
            children.Add(new GreenToken(SyntaxKind.MissingToken, ""));
            ReportMissingToken(SyntaxKind.CloseBracketToken);
        }

        return new GreenNode(SyntaxKind.IndirectOperand, children.ToArray());
    }

    // =========================================================================
    // Pratt expression parser
    // =========================================================================

    private GreenNodeBase ParseExpression(int parentPrecedence = 0)
    {
        var left = ParsePrefixExpression();

        while (true)
        {
            var precedence = GetBinaryPrecedence(Current.Kind);
            if (precedence == 0 || precedence <= parentPrecedence)
                break;

            var op = Advance();
            var rightPrec = IsRightAssociative(op.Kind) ? precedence - 1 : precedence;
            var right = ParseExpression(rightPrec);
            left = new GreenNode(SyntaxKind.BinaryExpression, [left, op, right]);
        }

        return left;
    }

    private GreenNodeBase ParsePrefixExpression()
    {
        // # before a keyword is a raw identifier, not unary operator
        if (Current.Kind == SyntaxKind.HashToken && IsKeyword(Peek().Kind))
            return ParsePrimaryExpression();

        if (Current.Kind is SyntaxKind.MinusToken or SyntaxKind.TildeToken
            or SyntaxKind.BangToken or SyntaxKind.PlusToken or SyntaxKind.HashToken)
        {
            var op = Advance();
            return new GreenNode(SyntaxKind.UnaryExpression, [op, ParsePrefixExpression()]);
        }
        return ParsePrimaryExpression();
    }

    private GreenNodeBase ParsePrimaryExpression()
    {
        switch (Current.Kind)
        {
            case SyntaxKind.NumberLiteral or SyntaxKind.FixedPointLiteral
                or SyntaxKind.CurrentAddressToken or SyntaxKind.AtToken
                or SyntaxKind.StringLiteral or SyntaxKind.CharLiteralToken
                or SyntaxKind.MacroParamToken:
                return new GreenNode(SyntaxKind.LiteralExpression, [Advance()]);

            case SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken:
            {
                var name = Advance();
                if (Current.Kind == SyntaxKind.LocalLabelToken &&
                    name.Kind == SyntaxKind.IdentifierToken &&
                    !HasNewlineTrivia(name))
                {
                    var localToken = Advance();
                    name = new GreenToken(SyntaxKind.IdentifierToken,
                        name.Text + localToken.Text,
                        name.LeadingTrivia, localToken.TrailingTrivia);
                }
                if (Current.Kind == SyntaxKind.MacroParamToken)
                    return new GreenNode(SyntaxKind.NameExpression, [name, Advance()]);
                return new GreenNode(SyntaxKind.NameExpression, [name]);
            }

            // Raw identifier: #keyword — treat keyword as identifier name (case-sensitive, '#' prefix preserved)
            case SyntaxKind.HashToken when IsKeyword(Peek().Kind):
            {
                var hash = Advance(); // #
                var kw = Advance();
                var id = new GreenToken(SyntaxKind.IdentifierToken, "#" + kw.Text,
                    hash.LeadingTrivia, kw.TrailingTrivia);
                return new GreenNode(SyntaxKind.NameExpression, [id]);
            }

            case SyntaxKind.AnonLabelForwardToken or SyntaxKind.AnonLabelBackwardToken:
                return new GreenNode(SyntaxKind.LiteralExpression, [Advance()]);

            case SyntaxKind.OpenParenToken:
            {
                var open = Advance();
                var expr = ParseExpression();
                GreenNodeBase close = Current.Kind == SyntaxKind.CloseParenToken
                    ? Advance()
                    : ExpectToken(SyntaxKind.CloseParenToken);
                return new GreenNode(SyntaxKind.ParenthesizedExpression, [open, expr, close]);
            }

            case SyntaxKind.OpenBracketToken:
            {
                // [register] inside expressions (used in sizeof([bc]) etc.)
                var children = new List<GreenNodeBase> { Advance() };
                while (Current.Kind is not SyntaxKind.CloseBracketToken
                    and not SyntaxKind.EndOfFileToken && !AtEndOfStatement())
                    children.Add(Advance());
                if (Current.Kind == SyntaxKind.CloseBracketToken)
                    children.Add(Advance());
                return new GreenNode(SyntaxKind.IndirectOperand, children.ToArray());
            }

            default:
                // Built-in functions: HIGH(...), LOW(...), SIN(...), STRLEN(...), etc.
                // HighKeyword..StrfmtKeyword is a contiguous range in SyntaxKind.cs.
                if (IsBuiltInFunctionKeyword(Current.Kind))
                    return ParseFunctionCallExpression();

                if (IsRegisterKeyword(Current.Kind))
                    return new GreenNode(SyntaxKind.NameExpression, [Advance()]);

                // Intentionally does NOT consume — the parent handles recovery.
                ReportMissingToken(SyntaxKind.NumberLiteral);
                return new GreenNode(SyntaxKind.LiteralExpression,
                    [new GreenToken(SyntaxKind.MissingToken, "")]);
        }
    }

    private GreenNode ParseFunctionCallExpression()
    {
        var children = new List<GreenNodeBase>
        {
            Advance(), // function keyword
            ExpectToken(SyntaxKind.OpenParenToken),
        };

        if (Current.Kind is not SyntaxKind.CloseParenToken and not SyntaxKind.EndOfFileToken)
        {
            children.Add(Current.Kind == SyntaxKind.OpenBracketToken
                ? ParseBracketedArgument()
                : ParseExpression());

            while (Current.Kind == SyntaxKind.CommaToken)
            {
                children.Add(Advance());
                var before = _position;
                children.Add(Current.Kind == SyntaxKind.OpenBracketToken
                    ? ParseBracketedArgument()
                    : ParseExpression());
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

    private GreenToken ConsumeRawIdentifierOrAdvance()
    {
        if (Current.Kind == SyntaxKind.HashToken && IsKeyword(Peek().Kind))
        {
            var hash = Advance();
            var kw = Advance();
            // Raw identifier: preserve '#' prefix so '#DEF' and '#def' remain distinct
            // case-sensitive symbols and don't collide with each other or with bare identifiers.
            return new GreenToken(SyntaxKind.IdentifierToken, "#" + kw.Text,
                hash.LeadingTrivia, kw.TrailingTrivia);
        }
        return Advance();
    }

    private GreenNode ParseBracketedArgument()
    {
        var children = new List<GreenNodeBase> { Advance() }; // [
        while (Current.Kind is not SyntaxKind.CloseBracketToken
            and not SyntaxKind.EndOfFileToken and not SyntaxKind.CloseParenToken)
            children.Add(Advance());
        if (Current.Kind == SyntaxKind.CloseBracketToken)
            children.Add(Advance());
        return new GreenNode(SyntaxKind.IndirectOperand, children.ToArray());
    }

    private GreenToken ExpectToken(SyntaxKind expected)
    {
        if (Current.Kind == expected)
            return Advance();
        ReportMissingToken(expected);
        return new GreenToken(SyntaxKind.MissingToken, "");
    }

    // =========================================================================
    // Operator precedence (RGBDS-compatible, higher = tighter)
    // =========================================================================

    // 1: ||  2: &&  3: == != === !==  4: < > <= >=
    // 5: |   6: ^   7: &
    // 8: << >> >>>  9: + - ++  10: * / %  11: ** (right-assoc)
    private static int GetBinaryPrecedence(SyntaxKind kind) =>
        kind switch
        {
            SyntaxKind.PipePipeToken => 1,
            SyntaxKind.AmpersandAmpersandToken => 2,
            SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken
                or SyntaxKind.EqualsEqualsEqualsToken or SyntaxKind.TripleEqualsToken
                or SyntaxKind.BangEqualsEqualsToken => 3,
            SyntaxKind.LessThanToken or SyntaxKind.GreaterThanToken
                or SyntaxKind.LessThanEqualsToken or SyntaxKind.GreaterThanEqualsToken => 4,
            SyntaxKind.PipeToken => 5,
            SyntaxKind.CaretToken => 6,
            SyntaxKind.AmpersandToken => 7,
            SyntaxKind.LessThanLessThanToken or SyntaxKind.GreaterThanGreaterThanToken
                or SyntaxKind.TripleGreaterThanToken => 8,
            SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.PlusPlusToken => 9,
            SyntaxKind.StarToken or SyntaxKind.SlashToken or SyntaxKind.PercentToken => 10,
            SyntaxKind.StarStarToken => 11,
            _ => 0,
        };

    private static bool IsBinaryOperator(SyntaxKind kind) => GetBinaryPrecedence(kind) > 0;

    private static bool IsRightAssociative(SyntaxKind kind) => kind == SyntaxKind.StarStarToken;

    // =========================================================================
    // Diagnostics
    // =========================================================================

    private static bool HasNewlineTrivia(GreenToken token)
    {
        var trivia = token.TrailingTrivia;
        for (int i = 0; i < trivia.Count; i++)
            if (trivia[i].Kind == SyntaxKind.NewlineTrivia)
                return true;
        return false;
    }

    private void ReportTrailingComma() =>
        _diagnostics.Report(new TextSpan(_currentOffset, 0),
            "Trailing comma in data directive", DiagnosticSeverity.Warning);

    private void ReportMissingToken(SyntaxKind expected) =>
        _diagnostics.Report(new TextSpan(_currentOffset, 0), $"Expected '{expected}'");

    private void ReportBadToken(GreenToken token)
    {
        // Token was just Advance()'d — _currentOffset is past it.
        int tokenStart = _currentOffset - token.FullWidth;
        _diagnostics.Report(
            new TextSpan(tokenStart + token.LeadingTriviaWidth, token.Width),
            $"Unexpected token '{token.Kind}'");
    }
}
