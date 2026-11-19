pub const TOKEN_TYPES: [&str; 13] = [
    "keyword",
    "variable",
    "number",
    "string",
    "comment",
    "operator",
    "label",
    "macro",
    "parameter",
    "register",
    "conditionFlag",
    "directive",
    "sectionType",
];
pub const TOKEN_MODIFIERS: [&str; 2] = ["declaration", "definition"];

pub fn classify(kind: &str) -> Option<u32> {
    match kind {
        "NopKeyword" | "LdKeyword" | "AddKeyword" | "AdcKeyword" | "SubKeyword" | "SbcKeyword"
        | "AndKeyword" | "OrKeyword" | "XorKeyword" | "CpKeyword" | "IncKeyword" | "DecKeyword"
        | "DaaKeyword" | "CplKeyword" | "RlcaKeyword" | "RlaKeyword" | "RrcaKeyword"
        | "RraKeyword" | "RlcKeyword" | "RlKeyword" | "RrcKeyword" | "RrKeyword" | "SlaKeyword"
        | "SraKeyword" | "SrlKeyword" | "SwapKeyword" | "BitKeyword" | "SetKeyword"
        | "ResKeyword" | "JpKeyword" | "JrKeyword" | "CallKeyword" | "RetKeyword"
        | "RetiKeyword" | "RstKeyword" | "PopKeyword" | "PushKeyword" | "DiKeyword"
        | "EiKeyword" | "HaltKeyword" | "StopKeyword" | "CcfKeyword" | "ScfKeyword"
        | "LdiKeyword" | "LddKeyword" | "LdhKeyword" | "HighKeyword" | "LowKeyword"
        | "BankKeyword" | "SizeofKeyword" | "StartofKeyword" | "IsConstKeyword"
        | "StrlenKeyword" | "StrcatKeyword" | "StrsubKeyword" | "RevcharKeyword"
        | "CharlenKeyword" | "IncharmapKeyword" | "StrcmpKeyword" | "MulKeyword"
        | "DivFuncKeyword" | "DivKeyword" | "FmodKeyword" | "PowKeyword" | "LogKeyword"
        | "RoundKeyword" | "CeilKeyword" | "FloorKeyword" | "SinKeyword" | "CosKeyword"
        | "TanKeyword" | "AsinKeyword" | "AcosKeyword" | "AtanKeyword" | "Atan2Keyword"
        | "StrfindKeyword" | "StrrfindKeyword" | "StruprKeyword" | "StrlwrKeyword"
        | "BytelenKeyword" | "StrbyteKeyword" | "StrcharKeyword" | "ReadfileKeyword"
        | "BitwidthKeyword" | "TzcountKeyword" | "StrfmtKeyword" => Some(0),
        "IdentifierToken" => Some(1),
        "NumberLiteral" | "FixedPointLiteral" | "CurrentAddressToken" => Some(2),
        "StringLiteral" | "CharLiteralToken" => Some(3),
        "LineCommentTrivia" | "BlockCommentTrivia" => Some(4),
        "PlusToken"
        | "MinusToken"
        | "StarToken"
        | "SlashToken"
        | "PercentToken"
        | "AmpersandToken"
        | "PipeToken"
        | "CaretToken"
        | "TildeToken"
        | "BangToken"
        | "LessThanToken"
        | "GreaterThanToken"
        | "LessThanLessThanToken"
        | "GreaterThanGreaterThanToken"
        | "TripleGreaterThanToken"
        | "StarStarToken"
        | "PlusPlusToken"
        | "TripleEqualsToken"
        | "EqualsEqualsEqualsToken"
        | "BangEqualsEqualsToken"
        | "EqualsToken"
        | "EqualsEqualsToken"
        | "BangEqualsToken"
        | "LessThanEqualsToken"
        | "GreaterThanEqualsToken"
        | "AmpersandAmpersandToken"
        | "PipePipeToken" => Some(5),
        "LocalLabelToken" => Some(6),
        "MacroParamToken" => Some(8),
        "AKeyword" | "BKeyword" | "CKeyword" | "DKeyword" | "EKeyword" | "HKeyword"
        | "LKeyword" | "HlKeyword" | "SpKeyword" | "AfKeyword" | "BcKeyword" | "DeKeyword"
        | "HliKeyword" | "HldKeyword" => Some(9),
        "ZKeyword" | "NzKeyword" | "NcKeyword" => Some(10),
        "SectionKeyword"
        | "DbKeyword"
        | "DwKeyword"
        | "DlKeyword"
        | "DsKeyword"
        | "EquKeyword"
        | "EqusKeyword"
        | "RedefKeyword"
        | "ExportKeyword"
        | "PurgeKeyword"
        | "IfKeyword"
        | "ElifKeyword"
        | "ElseKeyword"
        | "EndcKeyword"
        | "MacroKeyword"
        | "EndmKeyword"
        | "ShiftKeyword"
        | "ReptKeyword"
        | "ForKeyword"
        | "EndrKeyword"
        | "BreakKeyword"
        | "IncludeKeyword"
        | "IncbinKeyword"
        | "CharmapKeyword"
        | "NewcharmapKeyword"
        | "SetcharmapKeyword"
        | "PrecharmapKeyword"
        | "PopcharmapKeyword"
        | "NextuKeyword"
        | "EnduKeyword"
        | "LoadKeyword"
        | "EndlKeyword"
        | "RbKeyword"
        | "RwKeyword"
        | "RsresetKeyword"
        | "RssetKeyword"
        | "AssertKeyword"
        | "StaticAssertKeyword"
        | "WarnKeyword"
        | "FailKeyword"
        | "FatalKeyword"
        | "PrintKeyword"
        | "PrintlnKeyword"
        | "OptKeyword"
        | "PushsKeyword"
        | "PopsKeyword"
        | "PushoKeyword"
        | "PopoKeyword"
        | "DefKeyword" => Some(11),
        "Rom0Keyword" | "RomxKeyword" | "Wram0Keyword" | "WramxKeyword" | "VramKeyword"
        | "HramKeyword" | "SramKeyword" | "OamKeyword" | "AlignKeyword" | "FragmentKeyword"
        | "UnionKeyword" => Some(12),
        _ => None,
    }
}

pub fn unclassified(kind: &str) -> bool {
    matches!(
        kind,
        "None"
            | "EndOfFileToken"
            | "BadToken"
            | "MissingToken"
            | "WhitespaceTrivia"
            | "NewlineTrivia"
            | "SkippedTokensTrivia"
            | "CommaToken"
            | "OpenParenToken"
            | "CloseParenToken"
            | "OpenBracketToken"
            | "CloseBracketToken"
            | "ColonToken"
            | "DoubleColonToken"
            | "DotToken"
            | "HashToken"
            | "AtToken"
            | "AnonLabelForwardToken"
            | "AnonLabelBackwardToken"
            | "CompilationUnit"
            | "InstructionStatement"
            | "LabelDeclaration"
            | "DirectiveStatement"
            | "SectionDirective"
            | "DataDirective"
            | "SymbolDirective"
            | "ConditionalDirective"
            | "MacroDefinition"
            | "MacroCall"
            | "RepeatDirective"
            | "IncludeDirective"
            | "RegisterOperand"
            | "ImmediateOperand"
            | "IndirectOperand"
            | "ConditionOperand"
            | "LabelOperand"
            | "LiteralExpression"
            | "NameExpression"
            | "BinaryExpression"
            | "UnaryExpression"
            | "ParenthesizedExpression"
            | "FunctionCallExpression"
    )
}

pub fn encode(text: &str, syntax: &serde_json::Value) -> Vec<u32> {
    let lines = crate::paths::lines(text);
    let mut data = Vec::new();
    let mut previous_line = 0;
    let mut previous_character = 0;
    for token in syntax["tokens"].as_array().into_iter().flatten() {
        let Some(kind) = token["kind"].as_str().and_then(classify) else {
            continue;
        };
        let (start, length) = crate::symbols::span(token);
        let end = start.saturating_add(length);
        let first = lines
            .partition_point(|&(line, _)| line <= start)
            .saturating_sub(1);
        for (index, &(line_start, line_end)) in lines.iter().enumerate().skip(first) {
            if line_start >= end {
                break;
            }
            let part_start = start.max(line_start);
            let part_end = end.min(line_end);
            if part_end <= part_start {
                continue;
            }
            let character = part_start - line_start;
            let delta_line = index - previous_line;
            let delta_character = if delta_line == 0 {
                character - previous_character
            } else {
                character
            };
            data.extend([
                delta_line as u32,
                delta_character as u32,
                (part_end - part_start) as u32,
                kind,
                0,
            ]);
            previous_line = index;
            previous_character = character;
        }
    }
    data
}
