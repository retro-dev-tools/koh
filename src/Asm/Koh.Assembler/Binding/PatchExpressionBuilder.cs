using Koh.Assembler.Syntax;
using Koh.Assembler.Syntax.InternalSyntax;
using Koh.Objects;

namespace Koh.Assembler.Binding;

/// <summary>Builds <see cref="PatchExpression"/> from assembler syntax.</summary>
internal static class PatchExpressionBuilder
{
    /// <summary>Flattens <paramref name="node"/>; null only when there is no expression at all.</summary>
    internal static PatchExpression? From(GreenNodeBase? node)
    {
        if (node is null)
            return null;
        var tokens = new List<PatchExpressionToken>();
        Flatten(node, tokens);
        return new PatchExpression(tokens);
    }

    private static void Flatten(GreenNodeBase node, List<PatchExpressionToken> tokens)
    {
        // Raw tokens, e.g. a LabelOperand's identifier, as ExpressionEvaluator accepts them.
        if (node is GreenToken token)
        {
            switch (token.Kind)
            {
                case SyntaxKind.IdentifierToken or SyntaxKind.LocalLabelToken:
                    tokens.Add(new(PatchExpressionOp.Symbol, Name: token.Text));
                    break;
                case SyntaxKind.NumberLiteral:
                    var value = ExpressionEvaluator.ParseNumber(token.Text);
                    tokens.Add(new(PatchExpressionOp.Literal, (int)(value ?? 0)));
                    break;
                case SyntaxKind.CurrentAddressToken or SyntaxKind.AtToken:
                    tokens.Add(new(PatchExpressionOp.CurrentAddress));
                    break;
            }
            return;
        }

        if (node is not GreenNode greenNode)
            return;

        switch (greenNode.Kind)
        {
            case SyntaxKind.LiteralExpression:
                if (greenNode.GetChild(0) is GreenToken literal)
                {
                    if (literal.Kind == SyntaxKind.NumberLiteral)
                    {
                        var value = ExpressionEvaluator.ParseNumber(literal.Text);
                        tokens.Add(new(PatchExpressionOp.Literal, (int)(value ?? 0)));
                    }
                    else if (literal.Kind == SyntaxKind.CurrentAddressToken)
                    {
                        tokens.Add(new(PatchExpressionOp.CurrentAddress));
                    }
                }
                break;

            case SyntaxKind.NameExpression:
                if (greenNode.GetChild(0) is GreenToken name)
                    tokens.Add(new(PatchExpressionOp.Symbol, Name: name.Text));
                break;

            case SyntaxKind.BinaryExpression:
            {
                var left = greenNode.GetChild(0);
                var op = greenNode.GetChild(1) as GreenToken;
                var right = greenNode.GetChild(2);
                if (left != null)
                    Flatten(left, tokens);
                if (right != null)
                    Flatten(right, tokens);
                if (op != null)
                    tokens.Add(new(BinaryOp(op.Kind)));
                break;
            }

            case SyntaxKind.UnaryExpression:
            {
                var op = greenNode.GetChild(0) as GreenToken;
                var operand = greenNode.GetChild(1);
                if (operand != null)
                    Flatten(operand, tokens);
                // Unary + is identity.
                if (op != null && op.Kind != SyntaxKind.PlusToken)
                    tokens.Add(new(UnaryOp(op.Kind)));
                break;
            }

            case SyntaxKind.ParenthesizedExpression:
                if (greenNode.GetChild(1) is { } inner)
                    Flatten(inner, tokens);
                break;

            default:
                for (int i = 0; i < greenNode.ChildCount; i++)
                {
                    if (greenNode.GetChild(i) is { } child)
                        Flatten(child, tokens);
                }
                break;
        }
    }

    private static PatchExpressionOp BinaryOp(SyntaxKind kind) =>
        kind switch
        {
            SyntaxKind.PlusToken => PatchExpressionOp.Add,
            SyntaxKind.MinusToken => PatchExpressionOp.Sub,
            SyntaxKind.StarToken => PatchExpressionOp.Mul,
            SyntaxKind.SlashToken => PatchExpressionOp.Div,
            SyntaxKind.PercentToken => PatchExpressionOp.Mod,
            SyntaxKind.AmpersandToken => PatchExpressionOp.And,
            SyntaxKind.PipeToken => PatchExpressionOp.Or,
            SyntaxKind.CaretToken => PatchExpressionOp.Xor,
            SyntaxKind.LessThanLessThanToken => PatchExpressionOp.Shl,
            SyntaxKind.GreaterThanGreaterThanToken => PatchExpressionOp.Shr,
            SyntaxKind.EqualsEqualsToken => PatchExpressionOp.Eq,
            SyntaxKind.BangEqualsToken => PatchExpressionOp.Ne,
            SyntaxKind.LessThanToken => PatchExpressionOp.Lt,
            SyntaxKind.GreaterThanToken => PatchExpressionOp.Gt,
            SyntaxKind.LessThanEqualsToken => PatchExpressionOp.Le,
            SyntaxKind.GreaterThanEqualsToken => PatchExpressionOp.Ge,
            SyntaxKind.AmpersandAmpersandToken => PatchExpressionOp.LogAnd,
            SyntaxKind.PipePipeToken => PatchExpressionOp.LogOr,
            _ => PatchExpressionOp.UnknownBinary,
        };

    private static PatchExpressionOp UnaryOp(SyntaxKind kind) =>
        kind switch
        {
            SyntaxKind.MinusToken => PatchExpressionOp.Neg,
            SyntaxKind.TildeToken => PatchExpressionOp.Not,
            SyntaxKind.BangToken => PatchExpressionOp.LogNot,
            _ => PatchExpressionOp.UnknownUnary,
        };
}
