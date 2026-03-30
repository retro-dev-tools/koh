using Koh.Core;
using Koh.Core.Binding;
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

    [Test]
    public async Task Lexer_AdcKeyword()
    {
        var tokens = Lex("adc");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.AdcKeyword);
    }

    [Test]
    public async Task Lexer_JpKeyword()
    {
        var tokens = Lex("jp");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.JpKeyword);
    }

    [Test]
    public async Task Lexer_BitKeyword()
    {
        var tokens = Lex("bit");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.BitKeyword);
    }

    [Test]
    public async Task Lexer_HaltKeyword()
    {
        var tokens = Lex("halt");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.HaltKeyword);
    }

    [Test]
    public async Task Lexer_NzKeyword()
    {
        var tokens = Lex("nz");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.NzKeyword);
    }

    [Test]
    public async Task Lexer_AllInstructionKeywords()
    {
        var expected = new Dictionary<string, SyntaxKind>
        {
            ["adc"] = SyntaxKind.AdcKeyword,
            ["sub"] = SyntaxKind.SubKeyword,
            ["sbc"] = SyntaxKind.SbcKeyword,
            ["and"] = SyntaxKind.AndKeyword,
            ["or"] = SyntaxKind.OrKeyword,
            ["xor"] = SyntaxKind.XorKeyword,
            ["cp"] = SyntaxKind.CpKeyword,
            ["inc"] = SyntaxKind.IncKeyword,
            ["dec"] = SyntaxKind.DecKeyword,
            ["daa"] = SyntaxKind.DaaKeyword,
            ["cpl"] = SyntaxKind.CplKeyword,
            ["rlca"] = SyntaxKind.RlcaKeyword,
            ["rla"] = SyntaxKind.RlaKeyword,
            ["rrca"] = SyntaxKind.RrcaKeyword,
            ["rra"] = SyntaxKind.RraKeyword,
            ["rlc"] = SyntaxKind.RlcKeyword,
            ["rl"] = SyntaxKind.RlKeyword,
            ["rrc"] = SyntaxKind.RrcKeyword,
            ["rr"] = SyntaxKind.RrKeyword,
            ["sla"] = SyntaxKind.SlaKeyword,
            ["sra"] = SyntaxKind.SraKeyword,
            ["srl"] = SyntaxKind.SrlKeyword,
            ["swap"] = SyntaxKind.SwapKeyword,
            ["bit"] = SyntaxKind.BitKeyword,
            ["set"] = SyntaxKind.SetKeyword,
            ["res"] = SyntaxKind.ResKeyword,
            ["jp"] = SyntaxKind.JpKeyword,
            ["jr"] = SyntaxKind.JrKeyword,
            ["call"] = SyntaxKind.CallKeyword,
            ["ret"] = SyntaxKind.RetKeyword,
            ["reti"] = SyntaxKind.RetiKeyword,
            ["rst"] = SyntaxKind.RstKeyword,
            ["pop"] = SyntaxKind.PopKeyword,
            ["push"] = SyntaxKind.PushKeyword,
            ["di"] = SyntaxKind.DiKeyword,
            ["ei"] = SyntaxKind.EiKeyword,
            ["halt"] = SyntaxKind.HaltKeyword,
            ["stop"] = SyntaxKind.StopKeyword,
            ["ccf"] = SyntaxKind.CcfKeyword,
            ["scf"] = SyntaxKind.ScfKeyword,
            ["ldi"] = SyntaxKind.LdiKeyword,
            ["ldd"] = SyntaxKind.LddKeyword,
            ["ldh"] = SyntaxKind.LdhKeyword,
        };

        foreach (var (text, expectedKind) in expected)
        {
            var tokens = Lex(text);
            await Assert.That(tokens[0].Kind).IsEqualTo(expectedKind);
        }
    }

    [Test]
    public async Task Lexer_ConditionFlags()
    {
        var tokens = Lex("z");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.ZKeyword);

        tokens = Lex("nz");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.NzKeyword);

        tokens = Lex("nc");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.NcKeyword);
    }

    [Test]
    public async Task Lexer_CRegister_ServesAsBothRegisterAndConditionFlag()
    {
        // "c" always lexes as CKeyword — the parser disambiguates register vs condition flag
        var tokens = Lex("c");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.CKeyword);
    }

    [Test]
    public async Task Lexer_DollarSign_CurrentAddress()
    {
        var tokens = Lex("$");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.CurrentAddressToken);
        await Assert.That(tokens[0].Text).IsEqualTo("$");
    }

    [Test]
    public async Task Lexer_DollarHex_StillWorks()
    {
        var tokens = Lex("$FF");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.NumberLiteral);
        await Assert.That(tokens[0].Text).IsEqualTo("$FF");
    }

    [Test]
    public async Task Lexer_LocalLabel()
    {
        var tokens = Lex(".loop");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.LocalLabelToken);
        await Assert.That(tokens[0].Text).IsEqualTo(".loop");
    }

    [Test]
    public async Task Lexer_DotAlone_IsDotToken()
    {
        var tokens = Lex(".");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.DotToken);
    }

    [Test]
    public async Task Lexer_PercentAlone_IsPercentToken()
    {
        var tokens = Lex("% 2");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.PercentToken);
    }

    [Test]
    public async Task Lexer_AmpersandAlone_IsAmpersandToken()
    {
        var tokens = Lex("& 1");
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.AmpersandToken);
    }

    // -------------------------------------------------------------------------
    // String escape handling
    // -------------------------------------------------------------------------

    [Test]
    public async Task String_WithEscapedQuote()
    {
        var tokens = Lex("db \"hello\\\"world\"");
        var str = tokens[1]; // db, then the string literal
        await Assert.That(str.Kind).IsEqualTo(SyntaxKind.StringLiteral);
        await Assert.That(str.Text).IsEqualTo("\"hello\\\"world\"");
    }

    [Test]
    public async Task String_WithEscapedNewline()
    {
        var tokens = Lex("db \"hello\\nworld\"");
        var str = tokens[1];
        await Assert.That(str.Kind).IsEqualTo(SyntaxKind.StringLiteral);
        await Assert.That(str.Text).IsEqualTo("\"hello\\nworld\"");
    }

    [Test]
    public async Task String_WithEscapedBackslash()
    {
        var tokens = Lex("db \"path\\\\file\"");
        var str = tokens[1];
        await Assert.That(str.Kind).IsEqualTo(SyntaxKind.StringLiteral);
        await Assert.That(str.Text).IsEqualTo("\"path\\\\file\"");
    }

    [Test]
    public async Task String_Unterminated_StopsAtNewline()
    {
        // The unterminated string ends at the newline; nop on the next line is a
        // separate token, not part of the string.
        var tokens = Lex("db \"hello\nnop");
        var str = tokens[1];
        await Assert.That(str.Kind).IsEqualTo(SyntaxKind.StringLiteral);
        await Assert.That(str.Text).IsEqualTo("\"hello");
        // nop must still be recognised as its own token
        await Assert.That(tokens[2].Kind).IsEqualTo(SyntaxKind.NopKeyword);
    }

    // -------------------------------------------------------------------------
    // Block comment trivia
    // -------------------------------------------------------------------------

    [Test]
    public async Task BlockComment_Simple()
    {
        // Block comment in leading trivia of nop
        var tokens = Lex("/* comment */ nop");
        var nop = tokens[0];
        await Assert.That(nop.Kind).IsEqualTo(SyntaxKind.NopKeyword);
        var leading = nop.LeadingTrivia.ToList();
        await Assert.That(leading.Any(t => t.Kind == SyntaxKind.BlockCommentTrivia)).IsTrue();
    }

    [Test]
    public async Task BlockComment_PreservesText()
    {
        var tokens = Lex("/* hello */ nop");
        var nop = tokens[0];
        var comment = nop.LeadingTrivia.First(t => t.Kind == SyntaxKind.BlockCommentTrivia);
        await Assert.That(comment.Text).IsEqualTo("/* hello */");
    }

    [Test]
    public async Task BlockComment_Nested()
    {
        // Nested: /* outer /* inner */ still comment */ — depth goes 1 -> 2 -> 1 -> 0
        var tokens = Lex("/* outer /* inner */ still comment */ nop");
        var nop = tokens[0];
        var comment = nop.LeadingTrivia.First(t => t.Kind == SyntaxKind.BlockCommentTrivia);
        await Assert.That(comment.Text).IsEqualTo("/* outer /* inner */ still comment */");
    }

    [Test]
    public async Task BlockComment_MultiLine_IsLeadingTriviaOfNextToken()
    {
        // A multi-line block comment before a token goes into its leading trivia.
        var tokens = Lex("/* line1\nline2\nline3 */ nop");
        var nop = tokens[0];
        var leading = nop.LeadingTrivia.ToList();
        await Assert.That(leading.Any(t => t.Kind == SyntaxKind.BlockCommentTrivia)).IsTrue();
        var comment = leading.First(t => t.Kind == SyntaxKind.BlockCommentTrivia);
        await Assert.That(comment.Text).IsEqualTo("/* line1\nline2\nline3 */");
    }

    [Test]
    public async Task BlockComment_Unterminated_ProducesDiagnostic()
    {
        // An unterminated block comment must surface as an error diagnostic.
        var tree = SyntaxTree.Parse("/* never closed");
        await Assert.That(tree.Diagnostics.Any(d =>
            d.Message.Contains("Unterminated") || d.Message.Contains("block comment"))).IsTrue();
    }

    [Test]
    public async Task BlockComment_InTrailingTrivia()
    {
        // Same-line block comment after a token lands in trailing trivia.
        var tokens = Lex("nop /* trailing */");
        var nop = tokens[0];
        var trailing = nop.TrailingTrivia.ToList();
        await Assert.That(trailing.Any(t => t.Kind == SyntaxKind.BlockCommentTrivia)).IsTrue();
    }

    [Test]
    public async Task BlockComment_BetweenTokens_IsTrailingOfPreceding()
    {
        // ld /* comment */ a, b — the comment on the same line as ld is trailing trivia of ld.
        var tokens = Lex("ld /* comment */ a, b");
        var ld = tokens[0];
        await Assert.That(ld.Kind).IsEqualTo(SyntaxKind.LdKeyword);
        var trailing = ld.TrailingTrivia.ToList();
        await Assert.That(trailing.Any(t => t.Kind == SyntaxKind.BlockCommentTrivia)).IsTrue();
        // a must still be the next token
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.AKeyword);
    }

    [Test]
    public async Task BlockComment_MultiLine_InTrailingTrivia_BreaksStatement()
    {
        // nop /* comment\nstill comment */ — the multi-line block comment in
        // trailing trivia position must act as a statement boundary so that
        // tokens on the following line are parsed as a new statement.
        var tree = SyntaxTree.Parse("nop /* comment\nstill comment */\nnop");
        var statements = tree.Root.ChildNodes().ToList();
        await Assert.That(statements).HasCount().EqualTo(2);
    }

    [Test]
    public async Task LineComment_StillWorks()
    {
        // Confirm that adding block comment support did not break line comments.
        var tokens = Lex("; line comment\nnop");
        var nop = tokens[0];
        await Assert.That(nop.Kind).IsEqualTo(SyntaxKind.NopKeyword);
        var leading = nop.LeadingTrivia.ToList();
        await Assert.That(leading.Any(t => t.Kind == SyntaxKind.LineCommentTrivia)).IsTrue();
    }

    // -------------------------------------------------------------------------
    // RGBDS-derived: block comments
    // -------------------------------------------------------------------------

    // RGBDS: block-comment
    [Test]
    public async Task BlockComment_InlinePrintln_OutputsText()
    {
        // Block comments inside / between tokens are treated as whitespace; the
        // surrounding PRINTLN still receives the correct string argument.
        var (model, output) = EmitWithOutput("""
            PRINTLN /* block comments are ignored // ** */ "hi"
            PRINTLN "block (/* ... */) comments at ends of line are fine" /* hi */
            PRINTLN /* block comments
            can span multiple lines
            */ "multiline"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(output).Contains("hi");
        await Assert.That(output).Contains("block (/* ... */) comments at ends of line are fine");
        await Assert.That(output).Contains("multiline");
    }

    // RGBDS: block-comment-contents-error
    [Test]
    public async Task BlockComment_NestedSlashStar_WarnButAssemblySucceeds()
    {
        // A /* inside a block comment triggers a warning but assembly must still
        // complete successfully and PRINTLN output must be produced.
        var (model, output) = EmitWithOutput("""
            /* block comments containing /* throw warnings */
            PRINTLN "reachable"
            """);
        // Assembly succeeds (the nested /* is a warning, not an error).
        await Assert.That(model.Success).IsTrue();
        await Assert.That(output).Contains("reachable");
    }

    // RGBDS: weird-comments
    [Test]
    public async Task BlockComment_WeirdEdgeCases_OutputsText()
    {
        // Edge case: /*/ is a valid block comment opening, //*/ closes on the second *.
        var (model, output) = EmitWithOutput("""
            PRINTLN /* // PRINT "commented out" */ "this is not commented out"
            PRINTLN /*//*/ "this is not commented out"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(output).Contains("this is not commented out");
    }

    // -------------------------------------------------------------------------
    // RGBDS-derived: numeric literals
    // -------------------------------------------------------------------------

    // RGBDS: underscore-in-numeric-literal
    [Test]
    public async Task NumericLiteral_UnderscoresAllowed_Decimal()
    {
        // Underscores may appear inside decimal literals as digit separators.
        var model = Emit("""
            SECTION "Test", ROM0
            db 1_23
            dw 12_345
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // RGBDS: underscore-in-numeric-literal
    [Test]
    public async Task NumericLiteral_UnderscoresAllowed_HexBinaryOctal()
    {
        // Underscores may appear inside hex, binary, and octal literals.
        var model = Emit("""
            SECTION "Test", ROM0
            dw $ab_cd
            db &2_0_0
            db %1111_0000
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // RGBDS: number-prefixes
    [Test]
    public async Task NumericLiteral_CStylePrefixes_Hex()
    {
        // 0x / 0X are equivalent to the $ hex prefix.
        var model = Emit("""
            SECTION "Test", ROM0
            MACRO test
                assert (\1) == (\2)
            ENDM
            test 0xDEAD, $DEAD
            test 0XcafeBABE, $cafeBABE
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // RGBDS: number-prefixes
    [Test]
    public async Task NumericLiteral_CStylePrefixes_BinaryOctal()
    {
        // 0b / 0B are equivalent to %; 0o / 0O are equivalent to &.
        var model = Emit("""
            SECTION "Test", ROM0
            MACRO test
                assert (\1) == (\2)
            ENDM
            test 0b101010, %101010
            test 0o755, &755
            test 0B11100100, %11100100
            test 0O644, &644
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // RGBDS: invalid-underscore
    [Test]
    public async Task NumericLiteral_DoubleUnderscore_IsError()
    {
        // Two consecutive underscores inside a numeric literal must be rejected.
        var model = Emit("""
            SECTION "Test", ROM0
            db 0
            println 123__456
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d =>
            d.Message.Contains("_") || d.Message.Contains("underscore") || d.Message.Contains("constant")))
            .IsTrue();
    }

    // RGBDS: invalid-underscore
    [Test]
    public async Task NumericLiteral_TrailingUnderscore_IsError()
    {
        // A trailing underscore at the end of a numeric literal must be rejected.
        var model = Emit("""
            SECTION "Test", ROM0
            db 0
            println 12345_
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d =>
            d.Message.Contains("trailing") || d.Message.Contains("_") || d.Message.Contains("constant")))
            .IsTrue();
    }

    // -------------------------------------------------------------------------
    // RGBDS-derived: raw strings
    // -------------------------------------------------------------------------

    // RGBDS: raw-strings
    [Test]
    public async Task RawString_HashPrefix_DisablesEscapes()
    {
        // #"..." disables escape processing: \t \1 etc. are literal backslash sequences.
        var model = Emit("""
            SECTION "Test", ROM0
            assert !strcmp( \
                #"\t\1", \
                "\\t\\1" )
            nop
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // RGBDS: raw-strings
    [Test]
    public async Task RawString_TripleQuote_AllowsEmbeddedNewline()
    {
        // #"""...""" (triple-quote raw string) may contain unescaped newlines.
        // The ASM source uses triple-quote delimiters, so we cannot nest it inside
        // a C# triple-quoted raw literal — use a verbatim string instead.
        var model = Emit(
            "SECTION \"Test\", ROM0\n" +
            "assert !strcmp( \\\n" +
            "    #\"\"\"new\nline\"\"\", \\\n" +
            "    \"new\\nline\" )\n" +
            "nop\n");
        await Assert.That(model.Success).IsTrue();
    }

    // RGBDS: raw-strings
    [Test]
    public async Task RawString_Empty_EqualsEmptyString()
    {
        // #"" is a valid raw string equal to the empty string "".
        var model = Emit("""
            SECTION "Test", ROM0
            assert !strcmp( #"", "" )
            nop
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // RGBDS: raw-string-symbols
    [Test]
    public async Task RawString_HashSymbol_YieldsStringValue()
    {
        // #name where name is an EQUS symbol yields the raw string value of that symbol
        // (i.e., without interpolation / escape processing).
        var (model, output) = EmitWithOutput("""
            def hello equs "world"
            def name equs "hello"
            PRINTLN "{name}"
            PRINTLN #name
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(output).Contains("hello");
    }

    // -------------------------------------------------------------------------
    // RGBDS-derived: line continuation
    // -------------------------------------------------------------------------

    // RGBDS: line-continuation-string
    [Test]
    public async Task LineContinuation_BackslashInString_ContinuesOnNextLine()
    {
        // A backslash followed by optional whitespace and a comment at EOL continues
        // the logical line; PRINTLN assembles all parts into one string.
        var (model, output) = EmitWithOutput("""
            println "Line \
            continuations work!"
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(output).Contains("Line");
        await Assert.That(output).Contains("continuations");
    }

    // RGBDS: line-continuation
    [Test]
    public async Task LineContinuation_NonWhitespaceAfterBackslash_IsError()
    {
        // Non-whitespace (other than a comment) after a line-continuation backslash
        // must be rejected with a diagnostic.
        var model = Emit("""
            SECTION "Test", ROM0
            MACRO \ spam
              WARN "spam"
            ENDM
            nop
            """);
        await Assert.That(model.Success).IsFalse();
        await Assert.That(model.Diagnostics.Any(d =>
            d.Message.Contains("continuation") || d.Message.Contains("backslash") ||
            d.Message.Contains("Invalid") || d.Message.Contains("character")))
            .IsTrue();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    private static (EmitModel Model, string Output) EmitWithOutput(string source)
    {
        var sw = new StringWriter();
        var tree = SyntaxTree.Parse(source);
        var model = Compilation.Create(sw, tree).Emit();
        return (model, sw.ToString());
    }
}
