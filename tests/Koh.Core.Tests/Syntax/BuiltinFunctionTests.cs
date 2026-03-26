using Koh.Core.Syntax;

namespace Koh.Core.Tests.Syntax;

public class BuiltinFunctionTests
{
    private static SyntaxNode GetExpressionFromImmediate(string source)
    {
        var tree = SyntaxTree.Parse(source);
        var instr = tree.Root.ChildNodes().First();
        var immediate = instr.ChildNodes().First(n => n.Kind == SyntaxKind.ImmediateOperand);
        return immediate.ChildNodes().First();
    }

    [Test]
    public async Task High()
    {
        var expr = GetExpressionFromImmediate("ld a, HIGH($AABB)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.HighKeyword);
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.OpenParenToken);
        // Argument is a LiteralExpression
        await Assert.That(children[2].AsNode!.Kind).IsEqualTo(SyntaxKind.LiteralExpression);
        await Assert.That(children[3].AsToken!.Kind).IsEqualTo(SyntaxKind.CloseParenToken);
    }

    [Test]
    public async Task Low()
    {
        var expr = GetExpressionFromImmediate("ld a, LOW($AABB)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.LowKeyword);
    }

    [Test]
    public async Task Bank()
    {
        var expr = GetExpressionFromImmediate("ld a, BANK(\"ROM0\")");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.BankKeyword);
        // Argument is a LiteralExpression(StringLiteral)
        await Assert.That(children[2].AsNode!.Kind).IsEqualTo(SyntaxKind.LiteralExpression);
    }

    [Test]
    public async Task Sizeof()
    {
        var expr = GetExpressionFromImmediate("ld hl, SIZEOF(\"ROM0\")");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.SizeofKeyword);
    }

    [Test]
    public async Task Startof()
    {
        var expr = GetExpressionFromImmediate("ld hl, STARTOF(\"ROM0\")");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.StartofKeyword);
    }

    [Test]
    public async Task Def()
    {
        var expr = GetExpressionFromImmediate("ld a, DEF(MySymbol)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.DefKeyword);
        await Assert.That(children[2].AsNode!.Kind).IsEqualTo(SyntaxKind.NameExpression);
    }

    [Test]
    public async Task Strlen()
    {
        var expr = GetExpressionFromImmediate("ld a, STRLEN(\"hello\")");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.StrlenKeyword);
    }

    [Test]
    public async Task Strcat_MultipleArgs()
    {
        var expr = GetExpressionFromImmediate("ld a, STRCAT(\"a\", \"b\")");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.StrcatKeyword);
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.OpenParenToken);
        await Assert.That(children[2].AsNode!.Kind).IsEqualTo(SyntaxKind.LiteralExpression); // "a"
        await Assert.That(children[3].AsToken!.Kind).IsEqualTo(SyntaxKind.CommaToken);
        await Assert.That(children[4].AsNode!.Kind).IsEqualTo(SyntaxKind.LiteralExpression); // "b"
        await Assert.That(children[5].AsToken!.Kind).IsEqualTo(SyntaxKind.CloseParenToken);
    }

    [Test]
    public async Task Strsub_ThreeArgs()
    {
        var expr = GetExpressionFromImmediate("ld a, STRSUB(\"abc\", 2, 1)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.StrsubKeyword);
        // 3 args separated by 2 commas: keyword ( arg , arg , arg )
        // children: [0]=keyword [1]=( [2]=arg [3]=, [4]=arg [5]=, [6]=arg [7]=)
        await Assert.That(children).Count().IsEqualTo(8);
    }

    [Test]
    public async Task FunctionInExpression()
    {
        // HIGH($AABB) + 1
        var expr = GetExpressionFromImmediate("ld a, HIGH($AABB) + 1");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.BinaryExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsNode!.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        await Assert.That(children[1].AsToken!.Kind).IsEqualTo(SyntaxKind.PlusToken);
    }

    [Test]
    public async Task NestedFunctions()
    {
        // HIGH(LOW($AABB))
        var expr = GetExpressionFromImmediate("ld a, HIGH(LOW($AABB))");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);

        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.HighKeyword);
        // The argument is itself a FunctionCallExpression
        await Assert.That(children[2].AsNode!.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
    }

    [Test]
    public async Task CaseInsensitive()
    {
        var expr = GetExpressionFromImmediate("ld a, high($AABB)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
    }

    [Test]
    public async Task NoDiagnostics()
    {
        var tree = SyntaxTree.Parse("ld a, HIGH($AABB)");
        await Assert.That(tree.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task MissingCloseParen_ProducesDiagnostic()
    {
        var tree = SyntaxTree.Parse("ld a, HIGH($AABB");
        await Assert.That(tree.Diagnostics).IsNotEmpty();
        // Tree must still be well-formed
        var instr = tree.Root.ChildNodes().First();
        var ops = instr.ChildNodes().ToList();
        var immediate = ops.First(n => n.Kind == SyntaxKind.ImmediateOperand);
        var func = immediate.ChildNodes().First();
        await Assert.That(func.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
    }

    [Test]
    public async Task MissingOpenParen_ProducesDiagnostic()
    {
        var tree = SyntaxTree.Parse("ld a, HIGH $AABB");
        await Assert.That(tree.Diagnostics).IsNotEmpty();
    }

    [Test]
    public async Task TrailingComma_ProducesDiagnostic()
    {
        var tree = SyntaxTree.Parse("ld a, HIGH(1,)");
        await Assert.That(tree.Diagnostics).IsNotEmpty();
        // Tree still has a FunctionCallExpression
        var instr = tree.Root.ChildNodes().First();
        await Assert.That(instr.Kind).IsEqualTo(SyntaxKind.InstructionStatement);
    }

    [Test]
    public async Task EmptyFirstArgument_ProducesDiagnostic()
    {
        var tree = SyntaxTree.Parse("ld a, HIGH(,1)");
        await Assert.That(tree.Diagnostics).IsNotEmpty();
    }

    [Test]
    public async Task UnaryOnFunctionCall()
    {
        var expr = GetExpressionFromImmediate("ld a, -HIGH($FF)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.UnaryExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.MinusToken);
        await Assert.That(children[1].AsNode!.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
    }

    [Test]
    public async Task IsConst()
    {
        var expr = GetExpressionFromImmediate("ld a, ISCONST(MySymbol)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[0].AsToken!.Kind).IsEqualTo(SyntaxKind.IsConstKeyword);
    }

    [Test]
    public async Task ExpressionAsArgument()
    {
        var expr = GetExpressionFromImmediate("ld a, HIGH($FF00 + $10)");
        await Assert.That(expr.Kind).IsEqualTo(SyntaxKind.FunctionCallExpression);
        var children = expr.ChildNodesAndTokens().ToList();
        await Assert.That(children[2].AsNode!.Kind).IsEqualTo(SyntaxKind.BinaryExpression);
    }
}
