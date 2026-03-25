namespace Koh.Core.Syntax;

public enum SyntaxKind : ushort
{
    // Special
    None = 0,
    EndOfFileToken,
    BadToken,
    MissingToken,

    // Trivia
    WhitespaceTrivia,
    LineCommentTrivia,
    BlockCommentTrivia,
    NewlineTrivia,
    SkippedTokensTrivia,

    // Punctuation
    CommaToken, OpenParenToken, CloseParenToken,
    OpenBracketToken, CloseBracketToken,
    ColonToken, DoubleColonToken, DotToken, HashToken,

    // Operators
    PlusToken, MinusToken, StarToken, SlashToken, PercentToken,
    AmpersandToken, PipeToken, CaretToken, TildeToken, BangToken,
    LessThanToken, GreaterThanToken,
    LessThanLessThanToken, GreaterThanGreaterThanToken,
    EqualsEqualsToken, BangEqualsToken,
    LessThanEqualsToken, GreaterThanEqualsToken,
    AmpersandAmpersandToken, PipePipeToken,

    // Literals
    NumberLiteral, StringLiteral, IdentifierToken,

    // SM83 instruction keywords (starter set)
    NopKeyword, LdKeyword, AddKeyword,

    // Register keywords
    AKeyword, BKeyword, CKeyword, DKeyword, EKeyword,
    HKeyword, LKeyword, HlKeyword, SpKeyword, AfKeyword, BcKeyword, DeKeyword,

    // Directive keywords (starter set)
    SectionKeyword, DbKeyword, DwKeyword, DlKeyword, DsKeyword,

    // Nodes
    CompilationUnit, InstructionStatement, LabelDeclaration,
    DirectiveStatement, SectionDirective, DataDirective,

    // Expression nodes
    LiteralExpression, NameExpression, BinaryExpression,
    UnaryExpression, ParenthesizedExpression,
}
