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
    NumberLiteral, StringLiteral, IdentifierToken, LocalLabelToken,
    CurrentAddressToken,

    // SM83 instruction keywords
    NopKeyword, LdKeyword, AddKeyword,
    AdcKeyword, SubKeyword, SbcKeyword, AndKeyword, OrKeyword, XorKeyword, CpKeyword,
    IncKeyword, DecKeyword, DaaKeyword, CplKeyword,
    RlcaKeyword, RlaKeyword, RrcaKeyword, RraKeyword,
    RlcKeyword, RlKeyword, RrcKeyword, RrKeyword,
    SlaKeyword, SraKeyword, SrlKeyword, SwapKeyword,
    BitKeyword, SetKeyword, ResKeyword,
    JpKeyword, JrKeyword, CallKeyword, RetKeyword, RetiKeyword, RstKeyword,
    PopKeyword, PushKeyword,
    DiKeyword, EiKeyword, HaltKeyword, StopKeyword,
    CcfKeyword, ScfKeyword,
    LdiKeyword, LddKeyword, LdhKeyword,

    // Condition flag keywords (C condition is contextual — CKeyword serves both register and flag)
    ZKeyword, NzKeyword, NcKeyword,

    // Register keywords
    AKeyword, BKeyword, CKeyword, DKeyword, EKeyword,
    HKeyword, LKeyword, HlKeyword, SpKeyword, AfKeyword, BcKeyword, DeKeyword,

    // Directive keywords (starter set)
    SectionKeyword, DbKeyword, DwKeyword, DsKeyword,

    // Built-in function keywords
    HighKeyword, LowKeyword, BankKeyword, SizeofKeyword, StartofKeyword,
    DefKeyword, IsConstKeyword, StrlenKeyword, StrcatKeyword, StrsubKeyword,

    // Nodes
    CompilationUnit, InstructionStatement, LabelDeclaration,
    DirectiveStatement, SectionDirective, DataDirective,

    // Operand nodes
    RegisterOperand, ImmediateOperand, IndirectOperand,
    ConditionOperand, LabelOperand,

    // Expression nodes
    LiteralExpression, NameExpression, BinaryExpression,
    UnaryExpression, ParenthesizedExpression, FunctionCallExpression,
}
