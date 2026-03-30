using Koh.Core;
using Koh.Core.Binding;
using Koh.Core.Syntax;

namespace Koh.Core.Tests.Syntax;

/// <summary>
/// Tests for the Pratt expression parser. Expressions are tested via instruction
/// operands since that's where they appear in assembly source.
/// </summary>
public class ExpressionTests
{
    private static SyntaxNode ParseFirstOperand(string source)
    {
        var tree = SyntaxTree.Parse(source);
        var instr = tree.Root.ChildNodes().First();
        return instr.ChildNodes().First();
    }

    private static SyntaxNode GetExpressionFromImmediate(string source)
    {
        var tree = SyntaxTree.Parse(source);
        var instr = tree.Root.ChildNodes().First();
        var immediate = instr.ChildNodes().First(n => n.Kind == SyntaxKind.ImmediateOperand);
        return immediate.ChildNodes().First();
    }

    // --- Literal expressions ---

    [Test]
    public async Task NumberLiteral()
    {
        var operand = ParseFirstOperand("rst $38");
        await Assert.That(operand.Kind).IsEqualTo(SyntaxKind.ImmediateOperand);
        var expr = operand.ChildNodes().First();
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.LiteralExpression);
    }

    [Test]
    public async Task DecimalLiteral()
    {
        var expr = GetExpressionFromImmediate("rst 42");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.LiteralExpression);
        var token = expr.ChildTokens().First();
        await Assert.That(token.Text).IsEqualTo("42");
    }

    // --- Binary expressions ---

    [Test]
    public async Task Addition()
    {
        // ld a, 1 + 2
        var expr = GetExpressionFromImmediate("ld a, 1 + 2");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.BinaryExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsNode!.Kind).IsEqualTo(SyntaxKind.LiteralExpression);
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.PlusToken);
        await Assert.That(children[2].AsNode!.Kind).IsEqualTo(SyntaxKind.LiteralExpression);
    }

    [Test]
    public async Task Precedence_MulBeforeAdd()
    {
        // ld a, 1 + 2 * 3 → BinaryExpression(+, 1, BinaryExpression(*, 2, 3))
        var expr = GetExpressionFromImmediate("ld a, 1 + 2 * 3");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.BinaryExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.PlusToken);

        // Right operand should be BinaryExpression(*, 2, 3)
        var right = children[2].AsNode!;
        await Assert.That(right.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
        var rightChildren = right.ChildNodesAndTokens().ToList();
        await Assert.That(rightChildren[1].AsToken!.Kind).IsEqualTo(SyntaxKind.StarToken);
    }

    [Test]
    public async Task Precedence_ShiftBeforeAdd()
    {
        // + binds tighter than << (precedence 9 vs 8), so:
        // 1 << 2 + 3 = 1 << (2 + 3)
        var expr = GetExpressionFromImmediate("ld a, 1 << 2 + 3");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.BinaryExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.LessThanLessThanToken);

        // Right child is (2 + 3)
        var right = children[2].AsNode!;
        await Assert.That(right.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
        var rightOp = right.ChildNodesAndTokens().ToList()[1].AsToken!;
        await Assert.That(rightOp.Kind).IsEqualTo(SyntaxKind.PlusToken);
    }

    // --- Unary expressions ---

    [Test]
    public async Task UnaryMinus()
    {
        var expr = GetExpressionFromImmediate("add sp, -4");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.UnaryExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.MinusToken);
        await Assert.That(children[1].AsNode!.Kind).IsEqualTo(SyntaxKind.LiteralExpression);
    }

    [Test]
    public async Task UnaryBitwiseNot()
    {
        var expr = GetExpressionFromImmediate("ld a, ~$FF");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.UnaryExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.TildeToken);
    }

    [Test]
    public async Task UnaryLogicalNot()
    {
        var expr = GetExpressionFromImmediate("ld a, !0");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.UnaryExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.BangToken);
    }

    // --- Parenthesized expressions ---

    [Test]
    public async Task Parenthesized()
    {
        var expr = GetExpressionFromImmediate("ld a, (1 + 2) * 3");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.BinaryExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        // Left should be ParenthesizedExpression
        var left = children[0].AsNode!;
        await Assert.That(left.Kind).IsEqualTo(SyntaxKind.ParenthesizedExpression);
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.StarToken);
    }

    // --- Name expressions ---

    [Test]
    public async Task NameExpression_Identifier()
    {
        var operand = ParseFirstOperand("jp MyLabel");
        await Assert.That(operand.Kind).IsEqualTo(SyntaxKind.LabelOperand);
    }

    [Test]
    public async Task NameExpression_InArithmetic()
    {
        // ld a, MyConst + 1
        var expr = GetExpressionFromImmediate("ld a, MyConst + 1");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.BinaryExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsNode!.Kind).IsEqualTo(SyntaxKind.NameExpression);
    }

    // --- Current address ---

    [Test]
    public async Task CurrentAddress_InExpression()
    {
        var expr = GetExpressionFromImmediate("ld a, $ + 2");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
    }

    // --- Complex expressions ---

    [Test]
    public async Task ComplexExpression()
    {
        // $FF00 + 3 * 2
        var expr = GetExpressionFromImmediate("ld hl, $FF00 + 3 * 2");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
    }

    [Test]
    public async Task BitwiseOperators()
    {
        var expr = GetExpressionFromImmediate("ld a, $F0 & $0F");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.AmpersandToken);
    }

    [Test]
    public async Task ComparisonOperator()
    {
        var expr = GetExpressionFromImmediate("ld a, 1 == 2");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.EqualsEqualsToken);
    }

    [Test]
    public async Task NoDiagnostics_SimpleExpression()
    {
        var tree = SyntaxTree.Parse("ld a, 1 + 2");
        await Assert.That(tree.Diagnostics).IsEmpty();
    }

    // --- Expression inside indirect operand ---

    [Test]
    public async Task IndirectWithExpression()
    {
        var tree = SyntaxTree.Parse("ld a, [$FF00 + $10]");
        var instr = tree.Root.ChildNodes().First();
        var ops = instr.ChildNodes().ToList();
        await Assert.That(ops[1].Kind).IsEqualTo(SyntaxKind.IndirectOperand);
        var indirectChildren = ops[1].ChildNodes().ToList();
        await Assert.That(indirectChildren.Any(n => n.Kind == SyntaxKind.BinaryExpression)).IsTrue();
    }

    // --- Associativity ---

    [Test]
    public async Task LeftAssociativity_Subtraction()
    {
        // 4 - 2 - 1 must be (4 - 2) - 1, not 4 - (2 - 1)
        var expr = GetExpressionFromImmediate("ld a, 4 - 2 - 1");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.BinaryExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.MinusToken);

        // Left child should be (4 - 2), i.e. also a BinaryExpression
        var left = children[0].AsNode!;
        await Assert.That(left.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
        // Right child should be literal 1
        var right = children[2].AsNode!;
        await Assert.That(right.Kind).IsEqualTo(SyntaxKind.LiteralExpression);
    }

    [Test]
    public async Task LeftAssociativity_Addition()
    {
        // 1 + 2 + 3 = (1 + 2) + 3
        var expr = GetExpressionFromImmediate("ld a, 1 + 2 + 3");
        var children = expr.ChildNodesAndTokens().ToList();
        // Left child is BinaryExpression(1 + 2)
        await Assert.That(children[0].AsNode!.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
        // Right child is literal 3
        await Assert.That(children[2].AsNode!.Kind).IsEqualTo(SyntaxKind.LiteralExpression);
    }

    // --- Additional operator coverage ---

    [Test]
    public async Task RightShift()
    {
        var expr = GetExpressionFromImmediate("ld a, $FF >> 4");
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.GreaterThanGreaterThanToken);
    }

    [Test]
    public async Task BitwisePipe()
    {
        var expr = GetExpressionFromImmediate("ld a, $F0 | $0F");
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.PipeToken);
    }

    [Test]
    public async Task BitwiseXor()
    {
        var expr = GetExpressionFromImmediate("ld a, $FF ^ $0F");
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.CaretToken);
    }

    [Test]
    public async Task Precedence_XorBindsTighterThanPipe()
    {
        // a | b ^ c = a | (b ^ c) because ^ has higher precedence
        var expr = GetExpressionFromImmediate("ld a, 1 | 2 ^ 3");
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.PipeToken);
        var right = children[2].AsNode!;
        await Assert.That(right.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
    }

    [Test]
    public async Task LogicalAnd()
    {
        var expr = GetExpressionFromImmediate("ld a, 1 && 2");
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.AmpersandAmpersandToken);
    }

    [Test]
    public async Task LogicalOr()
    {
        var expr = GetExpressionFromImmediate("ld a, 1 || 2");
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.PipePipeToken);
    }

    [Test]
    public async Task NotEqual()
    {
        var expr = GetExpressionFromImmediate("ld a, 1 != 2");
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.BangEqualsToken);
    }

    [Test]
    public async Task Division()
    {
        var expr = GetExpressionFromImmediate("ld a, 10 / 2");
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.SlashToken);
    }

    [Test]
    public async Task Modulo()
    {
        var expr = GetExpressionFromImmediate("ld a, 10 % 3");
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.PercentToken);
    }

    // --- Register in expression ---

    [Test]
    public async Task RegisterInExpression_SpPlusOffset()
    {
        // sp + $05 → ImmediateOperand(BinaryExpression(NameExpression(sp), +, Literal($05)))
        var expr = GetExpressionFromImmediate("add sp, sp + $05");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsNode!.Kind).IsEqualTo(SyntaxKind.NameExpression);
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.PlusToken);
    }

    // --- Unary edge cases ---

    [Test]
    public async Task UnaryPlus()
    {
        var expr = GetExpressionFromImmediate("ld a, +1");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.UnaryExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.PlusToken);
    }

    [Test]
    public async Task UnaryOnParenthesized()
    {
        var expr = GetExpressionFromImmediate("ld a, -(1 + 2)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.UnaryExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[1].AsNode!.Kind).IsEqualTo(SyntaxKind.ParenthesizedExpression);
    }

    [Test]
    public async Task DoubleUnary()
    {
        var expr = GetExpressionFromImmediate("ld a, ~~$FF");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.UnaryExpression);
        var inner = expr.ChildNodes().First();
        await Assert.That(inner.Kind).IsEqualTo(SyntaxKind.UnaryExpression);
    }

    // --- Error recovery ---

    [Test]
    public async Task MissingCloseParen_ProducesDiagnostic()
    {
        var tree = SyntaxTree.Parse("ld a, (1 + 2");
        await Assert.That(tree.Diagnostics).IsNotEmpty();
    }

    // --- Local label in expression ---

    [Test]
    public async Task LocalLabel_InArithmetic()
    {
        var expr = GetExpressionFromImmediate("ld hl, .loop + 2");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsNode!.Kind).IsEqualTo(SyntaxKind.NameExpression);
    }

    // =========================================================================
    // Semantic evaluation tests (expression results at bind time)
    // =========================================================================

    private static EmitModel Emit(string source)
    {
        var tree = SyntaxTree.Parse(source);
        return Compilation.Create(tree).Emit();
    }

    // --- RGBDS: bit-functions.asm ---

    [Test]
    public async Task BitWidth_Zero_IsZero()
    {
        // RGBDS: bit-functions.asm
        var model = Emit("assert BITWIDTH(0) == 0");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task BitWidth_42_IsSix()
    {
        // RGBDS: bit-functions.asm
        var model = Emit("assert BITWIDTH(42) == 6");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task BitWidth_NegativeOne_Is32()
    {
        // RGBDS: bit-functions.asm
        var model = Emit("assert BITWIDTH(-1) == 32");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task BitWidth_MinInt_Is32()
    {
        // RGBDS: bit-functions.asm
        var model = Emit("assert BITWIDTH($80000000) == 32");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task TzCount_Zero_Is32()
    {
        // RGBDS: bit-functions.asm
        var model = Emit("assert TZCOUNT(0) == 32");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task TzCount_42_IsOne()
    {
        // RGBDS: bit-functions.asm — 42 = 0b101010, lowest set bit is bit 1
        var model = Emit("assert TZCOUNT(42) == 1");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task TzCount_NegativeOne_IsZero()
    {
        // RGBDS: bit-functions.asm — all bits set, no trailing zeros
        var model = Emit("assert TZCOUNT(-1) == 0");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task TzCount_MinInt_Is31()
    {
        // RGBDS: bit-functions.asm — $80000000 = only bit 31 set
        var model = Emit("assert TZCOUNT($80000000) == 31");
        await Assert.That(model.Success).IsTrue();
    }

    // --- RGBDS: math.asm — integer exponentiation (**) and arithmetic ---

    [Test]
    public async Task Exponentiation_2Pow10_Is1024()
    {
        // RGBDS: math.asm — ** operator
        var model = Emit("assert 2 ** 10 == 1024");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Exponentiation_NegBase_OddExp_IsNegative()
    {
        // RGBDS: math.asm — -(3**4) == -81
        var model = Emit("assert -(3 ** 4) == -81");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Exponentiation_2Pow30_IsLargeValue()
    {
        // RGBDS: math.asm — 2**30 == $40000000
        var model = Emit("assert 2 ** 30 == $4000_0000");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task IntegerDivision_TruncatesDown()
    {
        // RGBDS: math.asm — 37/2 == 18
        var model = Emit("assert 37 / 2 == 18");
        await Assert.That(model.Success).IsTrue();
    }

    // --- RGBDS: exponent.asm — ** over loop ---

    [Test]
    public async Task Exponentiation_ZeroPow_IsOne()
    {
        // RGBDS: exponent.asm — any x**0 == 1
        var model = Emit("assert 5 ** 0 == 1");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Exponentiation_ThreePowFour_Is81()
    {
        // RGBDS: exponent.asm
        var model = Emit("assert 3 ** 4 == 81");
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task Exponentiation_NegativeBase_EvenExp_IsPositive()
    {
        // RGBDS: exponent.asm — (-3)**4 == 81
        var model = Emit("assert (-3) ** 4 == 81");
        await Assert.That(model.Success).IsTrue();
    }

    // --- RGBDS: ccode.asm — condition code operators (! prefix) ---

    [Test]
    public async Task ConditionCode_LogicalNot_JpNz()
    {
        // RGBDS: ccode.asm — "jp !nz, Label" is valid (nz negated)
        var model = Emit("""
            SECTION "ccode test", ROM0[0]
            Label:
            .local1
                jp z, Label
                jr nz, .local1
            .local2
                jp !nz, Label
                jr !z, .local2
            """);
        await Assert.That(model.Success).IsTrue();
    }

    [Test]
    public async Task ConditionCode_DoubleNot_IsOriginal()
    {
        // RGBDS: ccode.asm — !!z == z, !!nz == nz
        var model = Emit("""
            SECTION "ccode test", ROM0[0]
            Label:
            .local3
                jp !!z, Label
                jr !!nz, .local3
                call !!c, Label
                call !!nc, Label
            """);
        await Assert.That(model.Success).IsTrue();
    }

    // --- RGBDS: pc-operand.asm — @ in instruction operands ---

    [Test]
    public async Task PcOperand_RstAtFixed()
    {
        // RGBDS: pc-operand.asm — rst @ at address 0 means rst 0
        var model = Emit("""
            SECTION "fixed", ROM0[0]
                rst @
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xC7); // RST 0x00
    }

    [Test]
    public async Task PcOperand_LdDeAtAddress()
    {
        // RGBDS: pc-operand.asm — ld de, @ at offset 1 yields ld de, 1
        var model = Emit("""
            SECTION "fixed", ROM0[0]
                rst @
                ld de, @
            """);
        await Assert.That(model.Success).IsTrue();
        // rst @ = 1 byte at 0, then ld de,@ is ld de,1 = 0x11 0x01 0x00
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0xC7);
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0x11);
        await Assert.That(model.Sections[0].Data[2]).IsEqualTo((byte)0x01);
    }

    [Test]
    public async Task PcOperand_JrAtIsJrZero()
    {
        // RGBDS: jr-@.asm — jr @ in a fixed section at 0 encodes as jr 0 (opcode $18, offset -2)
        var model = Emit("""
            SECTION "fixed", ROM0[0]
                jr @
            """);
        await Assert.That(model.Success).IsTrue();
        await Assert.That(model.Sections[0].Data[0]).IsEqualTo((byte)0x18); // JR
        await Assert.That(model.Sections[0].Data[1]).IsEqualTo((byte)0xFE); // offset -2 (back to self)
    }

    // --- RGBDS: pc.asm — @ in PRINTLN ---

    [Test]
    public async Task Pc_PrintlnInFixedSection()
    {
        // RGBDS: pc.asm — PRINTLN "{@}" in section at $1A4 outputs "$1A4"
        var sw = new System.IO.StringWriter();
        var tree = SyntaxTree.Parse("""
            SECTION "fixed", ROM0[420]
                PRINTLN "{@}"
                ds 69
                PRINTLN "{@}"
            """);
        var model = Compilation.Create(sw, tree).Emit();
        await Assert.That(model.Success).IsTrue();
        var output = sw.ToString();
        await Assert.That(output).Contains("$1A4");
        await Assert.That(output).Contains("$1E9");
    }

    // --- RGBDS: ds-@.asm — @ in DS fill expressions ---

    [Test]
    public async Task DsAt_FixedSectionFill()
    {
        // RGBDS: ds-@.asm — ds 4, @ fills 4 bytes with the current PC value at each byte
        var model = Emit("""
            SECTION "zero", ROM0[0]
            zero:
            SECTION "test fixed", ROM0[0]
            FixedStart:
                ds 4, @
            """);
        await Assert.That(model.Success).IsTrue();
        // At ROM0[0], ds 4, @ → each byte is filled with @ at that position: 0,1,2,3
        var sec = model.Sections.First(s => s.Name == "test fixed");
        await Assert.That(sec.Data[0]).IsEqualTo((byte)0);
        await Assert.That(sec.Data[1]).IsEqualTo((byte)1);
        await Assert.That(sec.Data[2]).IsEqualTo((byte)2);
        await Assert.That(sec.Data[3]).IsEqualTo((byte)3);
    }

    // --- RGBDS: assert-const.asm — link-time assert on a known-constant @ ---

    [Test]
    public async Task Assert_PcAtFixedAddressIsConst()
    {
        // RGBDS: assert-const.asm — @ in a fixed ROM0 section is known at assembly time
        var model = Emit("""
            SECTION "rgbasm passing asserts", ROM0[0]
                db 0
                assert @
            """);
        // @ == 1 at the assert point (after db 0), so assert 1 should pass
        await Assert.That(model.Success).IsTrue();
    }
}
