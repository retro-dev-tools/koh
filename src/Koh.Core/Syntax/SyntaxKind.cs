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
    EqualsToken,
    EqualsEqualsToken, BangEqualsToken,
    LessThanEqualsToken, GreaterThanEqualsToken,
    AmpersandAmpersandToken, PipePipeToken,

    // Literals
    NumberLiteral, StringLiteral, IdentifierToken, LocalLabelToken,
    CurrentAddressToken,

    // Macro parameter tokens: \1..\9, \@, \#, \NARG
    // These are lexed as a single token so that macro bodies parse with correct
    // positions and the parameter placeholder survives as a tree node.
    MacroParamToken,

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

    // Directive keywords
    SectionKeyword, DbKeyword, DwKeyword, DsKeyword,
    EquKeyword, EqusKeyword, RedefKeyword, ExportKeyword, PurgeKeyword,

    // Conditional assembly keywords
    IfKeyword, ElifKeyword, ElseKeyword, EndcKeyword,

    // Macro keywords
    MacroKeyword, EndmKeyword, ShiftKeyword,

    // Repeat/loop keywords
    ReptKeyword, ForKeyword, EndrKeyword,

    // Include keywords
    IncludeKeyword, IncbinKeyword,

    // Character map keywords
    CharmapKeyword, NewcharmapKeyword, SetcharmapKeyword,
    PrecharmapKeyword, PopcharmapKeyword,

    // UNION/LOAD keywords
    NextuKeyword, EnduKeyword, LoadKeyword, EndlKeyword,

    // RS counter keywords (RlKeyword is defined in the instruction range above — RL is
    // both the rotate-left instruction and an RS long directive, disambiguated by context)
    RbKeyword, RwKeyword, RsresetKeyword, RssetKeyword,

    // Control directives
    AssertKeyword, StaticAssertKeyword, WarnKeyword, FailKeyword, FatalKeyword,
    PrintKeyword, PrintlnKeyword, OptKeyword,

    // Stack directives
    PushsKeyword, PopsKeyword, PushoKeyword, PopoKeyword,

    // Section type keywords — Rom0Keyword..OamKeyword must remain contiguous;
    // Parser.IsSectionTypeKeyword relies on a range check.
    Rom0Keyword, RomxKeyword, Wram0Keyword, WramxKeyword,
    VramKeyword, HramKeyword, SramKeyword, OamKeyword,

    // Section modifier / constraint keywords (not memory types)
    AlignKeyword, FragmentKeyword, UnionKeyword,

    // Built-in function keywords
    HighKeyword, LowKeyword, BankKeyword, SizeofKeyword, StartofKeyword,
    DefKeyword, IsConstKeyword, StrlenKeyword, StrcatKeyword, StrsubKeyword, RevcharKeyword,
    StrfindKeyword, StrrfindKeyword, StruprKeyword, StrlwrKeyword,
    BytelenKeyword, StrbyteKeyword, CharlenKeyword, StrcharKeyword, IncharmapKeyword,

    // Nodes
    CompilationUnit, InstructionStatement, LabelDeclaration,
    DirectiveStatement, SectionDirective, DataDirective, SymbolDirective,
    ConditionalDirective, MacroDefinition, MacroCall,
    RepeatDirective, IncludeDirective,

    // Operand nodes
    RegisterOperand, ImmediateOperand, IndirectOperand,
    ConditionOperand, LabelOperand,

    // Expression nodes
    LiteralExpression, NameExpression, BinaryExpression,
    UnaryExpression, ParenthesizedExpression, FunctionCallExpression,
}
