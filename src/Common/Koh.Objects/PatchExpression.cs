namespace Koh.Objects;

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
}
