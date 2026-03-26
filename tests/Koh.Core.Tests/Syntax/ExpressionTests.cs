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
}
