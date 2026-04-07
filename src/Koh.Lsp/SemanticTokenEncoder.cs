using Koh.Core.Syntax;
using Microsoft.VisualStudio.LanguageServer.Protocol;

namespace Koh.Lsp;

/// <summary>
/// Encodes syntax tokens into LSP semantic token deltas.
/// Classification rules are explicit — every SyntaxKind is either classified
/// or intentionally excluded.
/// </summary>
internal static class SemanticTokenEncoder
{
    // Token type indices (must match the legend order)
    internal const int TypeKeyword = 0;
    internal const int TypeVariable = 1;    // identifiers
    internal const int TypeNumber = 2;
    internal const int TypeString = 3;
    internal const int TypeComment = 4;
    internal const int TypeOperator = 5;
    internal const int TypeLabel = 6;       // local labels
    internal const int TypeMacro = 7;
    internal const int TypeParameter = 8;   // macro parameters
    internal const int TypeRegexp = 9;      // register keywords (reusing "regexp" slot)
    internal const int TypeFunction = 10;   // condition flags (z, nz, nc, c)
    internal const int TypeEnum = 11;       // directives
    internal const int TypeType = 12;       // section types

    public static readonly string[] TokenTypes =
    [
        SemanticTokenTypes.Keyword,     // 0
        SemanticTokenTypes.Variable,    // 1
        SemanticTokenTypes.Number,      // 2
        SemanticTokenTypes.String,      // 3
        SemanticTokenTypes.Comment,     // 4
        SemanticTokenTypes.Operator,    // 5
        "label",                        // 6
        SemanticTokenTypes.Macro,       // 7
        SemanticTokenTypes.Parameter,   // 8
        "register",                     // 9
        "conditionFlag",                // 10
        "directive",                    // 11
        "sectionType",                  // 12
    ];

    public static readonly string[] TokenModifiers =
    [
        SemanticTokenModifiers.Declaration,
        SemanticTokenModifiers.Definition,
    ];

    /// <summary>
    /// All SyntaxKind values that are explicitly classified by this encoder.
    /// </summary>
    public static readonly HashSet<SyntaxKind> AllClassifiedKinds = BuildClassifiedKinds();

    /// <summary>
    /// SyntaxKind values that are intentionally not classified (structural, trivia, etc.).
    /// </summary>
    public static readonly HashSet<SyntaxKind> IntentionallyUnclassifiedKinds = BuildUnclassifiedKinds();

    /// <summary>
    /// Encode all tokens from a syntax tree into the LSP delta-encoded int array.
    /// </summary>
    public static int[] Encode(SyntaxTree tree)
    {
        var data = new List<int>();
        int prevLine = 0;
        int prevChar = 0;

        EncodeNode(tree.Root, tree.Text, data, ref prevLine, ref prevChar);
        return data.ToArray();
    }

    private static void EncodeNode(SyntaxNode node, Core.Text.SourceText source,
        List<int> data, ref int prevLine, ref int prevChar)
    {
        foreach (var child in node.ChildNodesAndTokens())
        {
            if (child.IsToken)
            {
                var token = child.AsToken!;

                // Encode trivia (comments)
                foreach (var trivia in token.LeadingTrivia)
                    EncodeTrivia(trivia, source, data, ref prevLine, ref prevChar);

                // Encode the token itself
                var typeIndex = ClassifyToken(token);
                if (typeIndex >= 0)
                {
                    var span = token.Span;
                    var startLine = source.GetLineIndex(span.Start);
                    var startChar = span.Start - source.Lines[startLine].Start;

                    int deltaLine = startLine - prevLine;
                    int deltaChar = deltaLine == 0 ? startChar - prevChar : startChar;

                    data.Add(deltaLine);
                    data.Add(deltaChar);
                    data.Add(span.Length);
                    data.Add(typeIndex);
                    data.Add(0); // no modifiers

                    prevLine = startLine;
                    prevChar = startChar;
                }

                foreach (var trivia in token.TrailingTrivia)
                    EncodeTrivia(trivia, source, data, ref prevLine, ref prevChar);
            }
            else
            {
                EncodeNode(child.AsNode!, source, data, ref prevLine, ref prevChar);
            }
        }
    }

    private static void EncodeTrivia(SyntaxTrivia trivia, Core.Text.SourceText source,
        List<int> data, ref int prevLine, ref int prevChar)
    {
        if (trivia.Kind is SyntaxKind.LineCommentTrivia or SyntaxKind.BlockCommentTrivia)
        {
            var startLine = source.GetLineIndex(trivia.Position);
            var startChar = trivia.Position - source.Lines[startLine].Start;

            int deltaLine = startLine - prevLine;
            int deltaChar = deltaLine == 0 ? startChar - prevChar : startChar;

            data.Add(deltaLine);
            data.Add(deltaChar);
            data.Add(trivia.Span.Length);
            data.Add(TypeComment);
            data.Add(0);

            prevLine = startLine;
            prevChar = startChar;
        }
    }

    /// <summary>
    /// Classify a token into a semantic token type index. Returns -1 for unclassified tokens.
    /// </summary>
    internal static int ClassifyToken(SyntaxToken token)
    {
        var kind = token.Kind;

        // Instruction keywords
        if (kind >= SyntaxKind.NopKeyword && kind <= SyntaxKind.LdhKeyword)
            return TypeKeyword;

        // Register keywords
        if (kind >= SyntaxKind.AKeyword && kind <= SyntaxKind.DeKeyword)
            return TypeRegexp;

        // HLI/HLD addressing mode keywords
        if (kind is SyntaxKind.HliKeyword or SyntaxKind.HldKeyword)
            return TypeRegexp;

        // Condition flag keywords
        if (kind is SyntaxKind.ZKeyword or SyntaxKind.NzKeyword or SyntaxKind.NcKeyword)
            return TypeFunction;

        // Directive keywords
        if (IsDirectiveKeyword(kind))
            return TypeEnum;

        // Section type keywords
        if (kind >= SyntaxKind.Rom0Keyword && kind <= SyntaxKind.OamKeyword)
            return TypeType;

        // Section modifier keywords
        if (kind is SyntaxKind.AlignKeyword or SyntaxKind.FragmentKeyword or SyntaxKind.UnionKeyword)
            return TypeType;

        // Built-in function keywords
        if (kind >= SyntaxKind.HighKeyword && kind <= SyntaxKind.StrfmtKeyword)
            return TypeKeyword;

        return kind switch
        {
            // Literals
            SyntaxKind.NumberLiteral or SyntaxKind.FixedPointLiteral => TypeNumber,
            SyntaxKind.StringLiteral or SyntaxKind.CharLiteralToken => TypeString,
            SyntaxKind.CurrentAddressToken => TypeNumber,

            // Identifiers
            SyntaxKind.IdentifierToken => TypeVariable,
            SyntaxKind.LocalLabelToken => TypeLabel,

            // Macro parameter tokens
            SyntaxKind.MacroParamToken => TypeParameter,

            // Operators
            SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.StarToken
                or SyntaxKind.SlashToken or SyntaxKind.PercentToken
                or SyntaxKind.AmpersandToken or SyntaxKind.PipeToken
                or SyntaxKind.CaretToken or SyntaxKind.TildeToken or SyntaxKind.BangToken
                or SyntaxKind.LessThanToken or SyntaxKind.GreaterThanToken
                or SyntaxKind.LessThanLessThanToken or SyntaxKind.GreaterThanGreaterThanToken
                or SyntaxKind.TripleGreaterThanToken
                or SyntaxKind.StarStarToken or SyntaxKind.PlusPlusToken
                or SyntaxKind.TripleEqualsToken or SyntaxKind.EqualsEqualsEqualsToken
                or SyntaxKind.BangEqualsEqualsToken
                or SyntaxKind.EqualsToken or SyntaxKind.EqualsEqualsToken
                or SyntaxKind.BangEqualsToken
                or SyntaxKind.LessThanEqualsToken or SyntaxKind.GreaterThanEqualsToken
                or SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken
                => TypeOperator,

            _ => -1, // Unclassified
        };
    }

    private static bool IsDirectiveKeyword(SyntaxKind kind) =>
        kind is SyntaxKind.SectionKeyword or SyntaxKind.DbKeyword or SyntaxKind.DwKeyword
            or SyntaxKind.DlKeyword or SyntaxKind.DsKeyword
            or SyntaxKind.EquKeyword or SyntaxKind.EqusKeyword
            or SyntaxKind.RedefKeyword or SyntaxKind.ExportKeyword or SyntaxKind.PurgeKeyword
            or SyntaxKind.IfKeyword or SyntaxKind.ElifKeyword or SyntaxKind.ElseKeyword
            or SyntaxKind.EndcKeyword
            or SyntaxKind.MacroKeyword or SyntaxKind.EndmKeyword or SyntaxKind.ShiftKeyword
            or SyntaxKind.ReptKeyword or SyntaxKind.ForKeyword or SyntaxKind.EndrKeyword
            or SyntaxKind.BreakKeyword
            or SyntaxKind.IncludeKeyword or SyntaxKind.IncbinKeyword
            or SyntaxKind.CharmapKeyword or SyntaxKind.NewcharmapKeyword
            or SyntaxKind.SetcharmapKeyword
            or SyntaxKind.PrecharmapKeyword or SyntaxKind.PopcharmapKeyword
            or SyntaxKind.NextuKeyword or SyntaxKind.EnduKeyword
            or SyntaxKind.LoadKeyword or SyntaxKind.EndlKeyword
            or SyntaxKind.RbKeyword or SyntaxKind.RwKeyword
            or SyntaxKind.RsresetKeyword or SyntaxKind.RssetKeyword
            or SyntaxKind.AssertKeyword or SyntaxKind.StaticAssertKeyword
            or SyntaxKind.WarnKeyword or SyntaxKind.FailKeyword or SyntaxKind.FatalKeyword
            or SyntaxKind.PrintKeyword or SyntaxKind.PrintlnKeyword or SyntaxKind.OptKeyword
            or SyntaxKind.PushsKeyword or SyntaxKind.PopsKeyword
            or SyntaxKind.PushoKeyword or SyntaxKind.PopoKeyword
            or SyntaxKind.DefKeyword;

    // =========================================================================
    // Coverage tracking
    // =========================================================================

    private static HashSet<SyntaxKind> BuildClassifiedKinds()
    {
        var set = new HashSet<SyntaxKind>();

        // Instruction keywords
        for (var k = SyntaxKind.NopKeyword; k <= SyntaxKind.LdhKeyword; k++)
            set.Add(k);

        // Register keywords
        for (var k = SyntaxKind.AKeyword; k <= SyntaxKind.DeKeyword; k++)
            set.Add(k);

        // HLI/HLD
        set.Add(SyntaxKind.HliKeyword);
        set.Add(SyntaxKind.HldKeyword);

        // Condition flags
        set.Add(SyntaxKind.ZKeyword);
        set.Add(SyntaxKind.NzKeyword);
        set.Add(SyntaxKind.NcKeyword);

        // Section type keywords
        for (var k = SyntaxKind.Rom0Keyword; k <= SyntaxKind.OamKeyword; k++)
            set.Add(k);

        // Section modifiers
        set.Add(SyntaxKind.AlignKeyword);
        set.Add(SyntaxKind.FragmentKeyword);
        set.Add(SyntaxKind.UnionKeyword);

        // Built-in function keywords
        for (var k = SyntaxKind.HighKeyword; k <= SyntaxKind.StrfmtKeyword; k++)
            set.Add(k);

        // Directive keywords — enumerate via IsDirectiveKeyword
        foreach (SyntaxKind k in Enum.GetValues<SyntaxKind>())
            if (IsDirectiveKeyword(k))
                set.Add(k);

        // Literals
        set.Add(SyntaxKind.NumberLiteral);
        set.Add(SyntaxKind.FixedPointLiteral);
        set.Add(SyntaxKind.StringLiteral);
        set.Add(SyntaxKind.CharLiteralToken);
        set.Add(SyntaxKind.CurrentAddressToken);

        // Identifiers
        set.Add(SyntaxKind.IdentifierToken);
        set.Add(SyntaxKind.LocalLabelToken);
        set.Add(SyntaxKind.MacroParamToken);

        // Operators
        set.Add(SyntaxKind.PlusToken);
        set.Add(SyntaxKind.MinusToken);
        set.Add(SyntaxKind.StarToken);
        set.Add(SyntaxKind.SlashToken);
        set.Add(SyntaxKind.PercentToken);
        set.Add(SyntaxKind.AmpersandToken);
        set.Add(SyntaxKind.PipeToken);
        set.Add(SyntaxKind.CaretToken);
        set.Add(SyntaxKind.TildeToken);
        set.Add(SyntaxKind.BangToken);
        set.Add(SyntaxKind.LessThanToken);
        set.Add(SyntaxKind.GreaterThanToken);
        set.Add(SyntaxKind.LessThanLessThanToken);
        set.Add(SyntaxKind.GreaterThanGreaterThanToken);
        set.Add(SyntaxKind.TripleGreaterThanToken);
        set.Add(SyntaxKind.StarStarToken);
        set.Add(SyntaxKind.PlusPlusToken);
        set.Add(SyntaxKind.TripleEqualsToken);
        set.Add(SyntaxKind.EqualsEqualsEqualsToken);
        set.Add(SyntaxKind.BangEqualsEqualsToken);
        set.Add(SyntaxKind.EqualsToken);
        set.Add(SyntaxKind.EqualsEqualsToken);
        set.Add(SyntaxKind.BangEqualsToken);
        set.Add(SyntaxKind.LessThanEqualsToken);
        set.Add(SyntaxKind.GreaterThanEqualsToken);
        set.Add(SyntaxKind.AmpersandAmpersandToken);
        set.Add(SyntaxKind.PipePipeToken);

        // Trivia (comments — classified in EncodeTrivia)
        set.Add(SyntaxKind.LineCommentTrivia);
        set.Add(SyntaxKind.BlockCommentTrivia);

        return set;
    }

    private static HashSet<SyntaxKind> BuildUnclassifiedKinds()
    {
        return
        [
            // Special tokens
            SyntaxKind.None,
            SyntaxKind.EndOfFileToken,
            SyntaxKind.BadToken,
            SyntaxKind.MissingToken,

            // Trivia (whitespace, newlines, skipped)
            SyntaxKind.WhitespaceTrivia,
            SyntaxKind.NewlineTrivia,
            SyntaxKind.SkippedTokensTrivia,

            // Punctuation
            SyntaxKind.CommaToken,
            SyntaxKind.OpenParenToken,
            SyntaxKind.CloseParenToken,
            SyntaxKind.OpenBracketToken,
            SyntaxKind.CloseBracketToken,
            SyntaxKind.ColonToken,
            SyntaxKind.DoubleColonToken,
            SyntaxKind.DotToken,
            SyntaxKind.HashToken,
            SyntaxKind.AtToken,

            // Anonymous label tokens
            SyntaxKind.AnonLabelForwardToken,
            SyntaxKind.AnonLabelBackwardToken,

            // Node kinds (not tokens — never appear in token stream)
            SyntaxKind.CompilationUnit,
            SyntaxKind.InstructionStatement,
            SyntaxKind.LabelDeclaration,
            SyntaxKind.DirectiveStatement,
            SyntaxKind.SectionDirective,
            SyntaxKind.DataDirective,
            SyntaxKind.SymbolDirective,
            SyntaxKind.ConditionalDirective,
            SyntaxKind.MacroDefinition,
            SyntaxKind.MacroCall,
            SyntaxKind.RepeatDirective,
            SyntaxKind.IncludeDirective,
            SyntaxKind.RegisterOperand,
            SyntaxKind.ImmediateOperand,
            SyntaxKind.IndirectOperand,
            SyntaxKind.ConditionOperand,
            SyntaxKind.LabelOperand,
            SyntaxKind.LiteralExpression,
            SyntaxKind.NameExpression,
            SyntaxKind.BinaryExpression,
            SyntaxKind.UnaryExpression,
            SyntaxKind.ParenthesizedExpression,
            SyntaxKind.FunctionCallExpression,
        ];
    }
}
