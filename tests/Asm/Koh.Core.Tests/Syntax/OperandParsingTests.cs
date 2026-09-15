using Koh.Core.Syntax;

namespace Koh.Core.Tests.Syntax;

public class OperandParsingTests
{
    private static SyntaxNode ParseInstruction(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return tree.Root.ChildNodes().First();
    }

    private static List<SyntaxNode> Operands(SyntaxNode instruction) =>
        instruction.ChildNodes().ToList();

    [Test]
    public async Task RegisterToRegister_LdAB()
    {
        var instr = ParseInstruction("ld a, b");
        var ops = Operands(instr);

        await Assert.That(ops).Count().IsEqualTo(2);
        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.RegisterOperand);
        await Assert.That(ops[1].Kind).IsEqualTo(SyntaxKind.RegisterOperand);

        // First operand contains 'a'
        var aToken = ops[0].ChildTokens().First();
        await Assert.That(aToken.Kind).IsEqualTo(SyntaxKind.AKeyword);

        // Second operand contains 'b'
        var bToken = ops[1].ChildTokens().First();
        await Assert.That(bToken.Kind).IsEqualTo(SyntaxKind.BKeyword);
    }

    [Test]
    public async Task RegisterAndImmediate_LdA_FF()
    {
        var instr = ParseInstruction("ld a, $FF");
        var ops = Operands(instr);

        await Assert.That(ops).Count().IsEqualTo(2);
        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.RegisterOperand);
        await Assert.That(ops[1].Kind).IsEqualTo(SyntaxKind.ImmediateOperand);

        // ImmediateOperand now wraps a LiteralExpression
        var expr = ops[1].ChildNodes().First();
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.LiteralExpression);
        var numToken = expr.ChildTokens().First();
        await Assert.That(numToken.Kind).IsEqualTo(SyntaxKind.NumberLiteral);
    }

    [Test]
    public async Task RegisterAndIndirect_LdA_HL()
    {
        var instr = ParseInstruction("ld a, [hl]");
        var ops = Operands(instr);

        await Assert.That(ops).Count().IsEqualTo(2);
        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.RegisterOperand);
        await Assert.That(ops[1].Kind).IsEqualTo(SyntaxKind.IndirectOperand);

        // Indirect contains [ NameExpression(hl) ]
        var tokens = ops[1].ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.OpenBracketToken);
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.CloseBracketToken);
        // hl is inside a NameExpression node
        var innerExpr = ops[1].ChildNodes().First();
        await Assert.That(innerExpr.Kind).IsEqualTo(SyntaxKind.NameExpression);
    }

    [Test]
    public async Task IndirectIncrement_LdHLPlusA()
    {
        var instr = ParseInstruction("ld [hl+], a");
        var ops = Operands(instr);

        await Assert.That(ops).Count().IsEqualTo(2);
        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.IndirectOperand);
        await Assert.That(ops[1].Kind).IsEqualTo(SyntaxKind.RegisterOperand);

        // Indirect contains [ hl + ]
        var tokens = ops[0].ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.OpenBracketToken);
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.HlKeyword);
        await Assert.That(tokens[2].Kind).IsEqualTo(SyntaxKind.PlusToken);
        await Assert.That(tokens[3].Kind).IsEqualTo(SyntaxKind.CloseBracketToken);
    }

    [Test]
    public async Task IndirectDecrement_LdA_HLMinus()
    {
        var instr = ParseInstruction("ld a, [hl-]");
        var ops = Operands(instr);

        await Assert.That(ops[1].Kind).IsEqualTo(SyntaxKind.IndirectOperand);
        // [hl-] is a special case — flat tokens, not an expression
        var tokens = ops[1].ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.OpenBracketToken);
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.HlKeyword);
        await Assert.That(tokens[2].Kind).IsEqualTo(SyntaxKind.MinusToken);
        await Assert.That(tokens[3].Kind).IsEqualTo(SyntaxKind.CloseBracketToken);
    }

    [Test]
    public async Task ConditionAndLabel_JrNzLoop()
    {
        var instr = ParseInstruction("jr nz, .loop");
        var ops = Operands(instr);

        await Assert.That(ops).Count().IsEqualTo(2);
        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.ConditionOperand);
        await Assert.That(ops[1].Kind).IsEqualTo(SyntaxKind.LabelOperand);

        var condToken = ops[0].ChildTokens().First();
        await Assert.That(condToken.Kind).IsEqualTo(SyntaxKind.NzKeyword);

        var labelToken = ops[1].ChildTokens().First();
        await Assert.That(labelToken.Kind).IsEqualTo(SyntaxKind.LocalLabelToken);
    }

    [Test]
    public async Task ConditionOnly_RetZ()
    {
        var instr = ParseInstruction("ret z");
        var ops = Operands(instr);

        await Assert.That(ops).Count().IsEqualTo(1);
        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.ConditionOperand);
    }

    [Test]
    public async Task NoOperands_Nop()
    {
        var instr = ParseInstruction("nop");
        var ops = Operands(instr);
        await Assert.That(ops).Count().IsEqualTo(0);
    }

    [Test]
    public async Task NoOperands_Rlca()
    {
        var instr = ParseInstruction("rlca");
        var ops = Operands(instr);
        await Assert.That(ops).Count().IsEqualTo(0);
    }

    [Test]
    public async Task Immediate_Rst38()
    {
        var instr = ParseInstruction("rst $38");
        var ops = Operands(instr);

        await Assert.That(ops).Count().IsEqualTo(1);
        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.ImmediateOperand);
    }

    [Test]
    public async Task BitIndex_Bit3A()
    {
        var instr = ParseInstruction("bit 3, a");
        var ops = Operands(instr);

        await Assert.That(ops).Count().IsEqualTo(2);
        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.ImmediateOperand);
        await Assert.That(ops[1].Kind).IsEqualTo(SyntaxKind.RegisterOperand);
    }

    [Test]
    public async Task RegisterPair_PushAF()
    {
        var instr = ParseInstruction("push af");
        var ops = Operands(instr);

        await Assert.That(ops).Count().IsEqualTo(1);
        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.RegisterOperand);
        var token = ops[0].ChildTokens().First();
        await Assert.That(token.Kind).IsEqualTo(SyntaxKind.AfKeyword);
    }

    [Test]
    public async Task CRegister_LdAC()
    {
        // 'c' in register position → RegisterOperand (not ConditionOperand)
        var instr = ParseInstruction("ld a, c");
        var ops = Operands(instr);

        await Assert.That(ops[1].Kind).IsEqualTo(SyntaxKind.RegisterOperand);
        var token = ops[1].ChildTokens().First();
        await Assert.That(token.Kind).IsEqualTo(SyntaxKind.CKeyword);
    }

    [Test]
    public async Task GlobalLabel_JpMyLabel()
    {
        var instr = ParseInstruction("jp MyLabel");
        var ops = Operands(instr);

        await Assert.That(ops).Count().IsEqualTo(1);
        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.LabelOperand);
        var token = ops[0].ChildTokens().First();
        await Assert.That(token.Kind).IsEqualTo(SyntaxKind.IdentifierToken);
    }

    [Test]
    public async Task GlobalLabel_CallRoutine()
    {
        var instr = ParseInstruction("call MyRoutine");
        var ops = Operands(instr);

        await Assert.That(ops).Count().IsEqualTo(1);
        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.LabelOperand);
    }

    [Test]
    public async Task CurrentAddress_JpDollar()
    {
        var instr = ParseInstruction("jp $");
        var ops = Operands(instr);

        await Assert.That(ops).Count().IsEqualTo(1);
        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.ImmediateOperand);
        // ImmediateOperand wraps a LiteralExpression containing the CurrentAddressToken
        var expr = ops[0].ChildNodes().First();
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.LiteralExpression);
        var token = expr.ChildTokens().First();
        await Assert.That(token.Kind).IsEqualTo(SyntaxKind.CurrentAddressToken);
    }

    [Test]
    public async Task CommasAreDirectChildrenOfInstruction()
    {
        var instr = ParseInstruction("ld a, b");
        var tokens = instr.ChildTokens().ToList();

        // Direct token children: mnemonic + comma
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.LdKeyword);
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.CommaToken);
    }

    [Test]
    public async Task NcCondition_JrNc()
    {
        var instr = ParseInstruction("jr nc, .done");
        var ops = Operands(instr);

        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.ConditionOperand);
        var condToken = ops[0].ChildTokens().First();
        await Assert.That(condToken.Kind).IsEqualTo(SyntaxKind.NcKeyword);
    }

    [Test]
    public async Task IndirectBC_LdA_BC()
    {
        var instr = ParseInstruction("ld a, [bc]");
        var ops = Operands(instr);

        await Assert.That(ops[1].Kind).IsEqualTo(SyntaxKind.IndirectOperand);
        // bc is inside a NameExpression
        var innerExpr = ops[1].ChildNodes().First();
        await Assert.That(innerExpr.Kind).IsEqualTo(SyntaxKind.NameExpression);
    }

    [Test]
    public async Task MultipleStatements_OperandsParsedPerLine()
    {
        var tree = SyntaxTree.Parse("ld a, b\nnop\nld c, $FF");
        var stmts = tree.Root.ChildNodes().ToList();

        await Assert.That(stmts).Count().IsEqualTo(3);

        // First: ld a, b → 2 register operands
        var ops1 = Operands(stmts[0]);
        await Assert.That(ops1).Count().IsEqualTo(2);

        // Second: nop → 0 operands
        var ops2 = Operands(stmts[1]);
        await Assert.That(ops2).Count().IsEqualTo(0);

        // Third: ld c, $FF → register + immediate
        var ops3 = Operands(stmts[2]);
        await Assert.That(ops3).Count().IsEqualTo(2);
        await Assert.That(ops3[0].Kind).IsEqualTo(SyntaxKind.RegisterOperand);
        await Assert.That(ops3[1].Kind).IsEqualTo(SyntaxKind.ImmediateOperand);
    }

    // --- Error recovery tests ---

    [Test]
    public async Task MissingCloseBracket_ProducesDiagnostic()
    {
        var tree = SyntaxTree.Parse("ld a, [hl");
        await Assert.That(tree.Diagnostics).IsNotEmpty();

        // Tree still has a well-formed IndirectOperand
        var instr = tree.Root.ChildNodes().First();
        var ops = Operands(instr);
        await Assert.That(ops[1].Kind).IsEqualTo(SyntaxKind.IndirectOperand);
    }

    [Test]
    public async Task TrailingComma_ProducesDiagnostic()
    {
        var tree = SyntaxTree.Parse("ld a,");
        await Assert.That(tree.Diagnostics).IsNotEmpty();

        // Instruction still parses — has the register operand plus a missing operand
        var instr = tree.Root.ChildNodes().First();
        await Assert.That(instr.Kind).IsEqualTo(SyntaxKind.InstructionStatement);
    }

    [Test]
    public async Task IndirectWithComma_DoesNotSwallowSeparator()
    {
        // [b, c] should stop the indirect at the comma — comma stays as instruction separator
        var tree = SyntaxTree.Parse("ld [b, c");
        var instr = tree.Root.ChildNodes().First();
        var ops = Operands(instr);

        // Should get IndirectOperand (with missing ]) and RegisterOperand for c
        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.IndirectOperand);
        await Assert.That(ops[1].Kind).IsEqualTo(SyntaxKind.RegisterOperand);
    }

    [Test]
    public async Task LdhIndirect_MultiTokenContent()
    {
        var instr = ParseInstruction("ldh a, [$FF]");
        var ops = Operands(instr);

        await Assert.That(ops).Count().IsEqualTo(2);
        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.RegisterOperand);
        await Assert.That(ops[1].Kind).IsEqualTo(SyntaxKind.IndirectOperand);

        // Inner content: [ LiteralExpression($FF) ]
        var tokens = ops[1].ChildTokens().ToList();
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.OpenBracketToken);
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.CloseBracketToken);
        var innerExpr = ops[1].ChildNodes().First();
        await Assert.That(innerExpr.Kind).IsEqualTo(SyntaxKind.LiteralExpression);
    }

    // --- \@ macro-unique-label suffix tests ---
    // Regression for: ParseLabelOperand() consumed only the identifier token and left the
    // trailing MacroParamToken(\@) orphaned (not a child of any green node). The orphan's
    // 2-char width was excluded from FullWidth sums, shifting all subsequent node positions
    // by -2 and breaking CollectMacroBody's bodyStart/bodyEnd calculations.

    [Test]
    public async Task LabelOperand_WithMacroAtSuffix_HasTwoTokenChildren()
    {
        // "call .loop\@" — the \@ suffix must be consumed as part of the LabelOperand,
        // not left as an orphaned token between nodes.
        var instr = ParseInstruction("call .loop\\@");
        var ops = Operands(instr);

        await Assert.That(ops).Count().IsEqualTo(1);
        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.LabelOperand);

        var tokens = ops[0].ChildTokens().ToList();
        await Assert.That(tokens).Count().IsEqualTo(2);
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.LocalLabelToken);
        await Assert.That(tokens[1].Kind).IsEqualTo(SyntaxKind.MacroParamToken);
        await Assert.That(tokens[1].Text).IsEqualTo("\\@");
    }

    [Test]
    public async Task LabelOperand_WithMacroAtSuffix_TreeFullWidthCoversEntireSource()
    {
        // The tree's FullWidth must equal the source text length exactly. If \@ is orphaned,
        // FullWidth is 2 bytes short and all position-based calculations drift.
        const string source = "call .loop\\@";
        var tree = SyntaxTree.Parse(source);

        // The CompilationUnit's FullWidth (via the red root's green node) must cover
        // every byte of the source text, including the \@ suffix.
        var root = tree.Root;
        await Assert.That(root.FullSpan.Length).IsEqualTo(source.Length);
    }

    [Test]
    public async Task LabelOperand_WithoutMacroAtSuffix_HasOneTokenChild()
    {
        // Ensure the fix doesn't break the normal (no \@) case.
        var instr = ParseInstruction("call .loop");
        var ops = Operands(instr);

        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.LabelOperand);
        var tokens = ops[0].ChildTokens().ToList();
        await Assert.That(tokens).Count().IsEqualTo(1);
        await Assert.That(tokens[0].Kind).IsEqualTo(SyntaxKind.LocalLabelToken);
    }

    [Test]
    public async Task MultipleStatements_AfterMacroAtOperand_HaveCorrectPositions()
    {
        // Verifies that a statement following an instruction with a \@ operand starts at
        // the correct source position. Before the fix, the next node's Position was off by
        // 2 because the orphaned \@ was excluded from the cumulative FullWidth sum.
        const string source = "call .fn\\@\nnop";
        var tree = SyntaxTree.Parse(source);
        var stmts = tree.Root.ChildNodes().ToList();

        await Assert.That(stmts).Count().IsEqualTo(2);

        // "nop" starts at position 11 (after "call .fn\@\n" which is 11 chars)
        int expectedNopPosition = "call .fn\\@\n".Length;
        await Assert.That(stmts[1].Position).IsEqualTo(expectedNopPosition);
    }

    // --- \@ in expression context (ParsePrimaryExpression) ---

    [Test]
    public async Task NameExpression_WithMacroAtSuffix_InIndirectOperand()
    {
        // [.data\@] — \@ inside indirect (expression context, not label operand)
        // must be consumed as part of the NameExpression, not left orphaned.
        const string source = "ld a, [.data\\@]";
        var tree = SyntaxTree.Parse(source);

        await Assert.That(tree.Root.FullSpan.Length).IsEqualTo(source.Length);
    }

    [Test]
    public async Task NameExpression_WithMacroAtSuffix_InArithmeticExpression()
    {
        // .table\@ + 1 — \@ in expression context (ParsePrimaryExpression)
        const string source = "db .base\\@ + 1";
        var tree = SyntaxTree.Parse(source);

        await Assert.That(tree.Root.FullSpan.Length).IsEqualTo(source.Length);
    }

    /// <summary>
    /// Regression: ParseOperand must look past \@ when checking for binary operators.
    /// Without this fix, `.data\@ + 1` routes to ParseLabelOperand (producing a
    /// LabelOperand) instead of ParseImmediateOperand (producing an ImmediateOperand
    /// wrapping a BinaryExpression).
    /// </summary>
    [Test]
    public async Task InstructionOperand_LabelAtSuffixPlusBinaryOp_RoutesToImmediateOperand()
    {
        var instr = ParseInstruction("ld a, .data\\@ + 1");
        var ops = Operands(instr);

        await Assert.That(ops).Count().IsEqualTo(2);
        // Second operand must be ImmediateOperand (expression), NOT LabelOperand
        await Assert.That(ops[1].Kind).IsEqualTo(SyntaxKind.ImmediateOperand);
        // The expression inside should be a BinaryExpression
        var expr = ops[1].ChildNodes().First();
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
    }

    [Test]
    public async Task InstructionOperand_LabelAtSuffixNoBinaryOp_RoutesToLabelOperand()
    {
        // Without a following binary operator, .label\@ should still be a LabelOperand
        var instr = ParseInstruction("call .target\\@");
        var ops = Operands(instr);

        await Assert.That(ops).Count().IsEqualTo(1);
        await Assert.That(ops[0].Kind).IsEqualTo(SyntaxKind.LabelOperand);
    }
}
