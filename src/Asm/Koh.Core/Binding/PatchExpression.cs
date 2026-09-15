using Koh.Core.Syntax;
using Koh.Core.Syntax.InternalSyntax;

namespace Koh.Core.Binding;

/// <summary>Operation of one <see cref="PatchExpressionToken"/>.</summary>
public enum PatchExpressionOp : byte
{
    Literal,
    Symbol,
    CurrentAddress,
    Add,
    Sub,
    Mul,
    Div,
    Mod,
    And,
    Or,
    Xor,
    Shl,
    Shr,
    Eq,
    Ne,
    Lt,
    Gt,
    Le,
    Ge,
    LogAnd,
    LogOr,
    Neg,
    Not,
    LogNot,

    /// <summary>A binary operator the flattening does not recognise.</summary>
    UnknownBinary,

    /// <summary>A unary operator the flattening does not recognise.</summary>
    UnknownUnary,
}

/// <summary>One postfix token: a literal <see cref="Value"/>, a symbol <see cref="Name"/>, or an operator.</summary>
public readonly record struct PatchExpressionToken(
    PatchExpressionOp Op,
    int Value = 0,
    string? Name = null
);

/// <summary>A patch's unresolved expression in postfix order, independent of the assembler syntax tree.</summary>
public sealed class PatchExpression(IReadOnlyList<PatchExpressionToken> tokens)
{
    public IReadOnlyList<PatchExpressionToken> Tokens { get; } = tokens;

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
