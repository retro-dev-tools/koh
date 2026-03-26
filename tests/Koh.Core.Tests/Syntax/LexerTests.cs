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
}
