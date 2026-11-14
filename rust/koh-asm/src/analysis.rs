use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Source {
    pub path: String,
    pub text: String,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct Analysis {
    pub documents: Vec<Document>,
    pub symbols: Vec<Symbol>,
    pub diagnostics: Vec<Diagnostic>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Document {
    pub path: String,
    pub tokens: Vec<Token>,
    pub nodes: Vec<Node>,
    pub scopes: Vec<Scope>,
    pub includes: Vec<Include>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Token {
    pub id: usize,
    pub kind: String,
    pub text: String,
    pub start: usize,
    pub length: usize,
    pub parent: usize,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Node {
    pub id: usize,
    pub kind: String,
    pub start: usize,
    pub length: usize,
    pub children: Vec<Child>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(untagged)]
pub enum Child {
    Token { token: usize },
    Node { node: usize },
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Scope {
    pub start: usize,
    pub end: usize,
    pub name: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Include {
    pub path: String,
    pub start: usize,
    pub length: usize,
}

#[derive(Clone, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct Location {
    pub path: String,
    pub start: usize,
    pub length: usize,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Symbol {
    pub id: String,
    pub name: String,
    pub kind: String,
    pub visibility: String,
    pub value: Option<i32>,
    pub section: Option<String>,
    pub owner: String,
    pub definition: Option<Location>,
    pub references: Vec<Location>,
    pub macro_arity: usize,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Diagnostic {
    pub path: String,
    pub start: usize,
    pub length: usize,
    pub severity: String,
    pub message: String,
}

#[derive(Clone, Debug)]
struct Statement {
    ids: Vec<usize>,
    op: String,
    head: Option<usize>,
    definition: Option<(usize, String)>,
    macro_name: Option<usize>,
    arity: usize,
    exported: bool,
}

struct Parsed {
    document: Document,
    statements: Vec<Statement>,
}

fn trivia(kind: &str) -> bool {
    kind.ends_with("Trivia") || kind == "EndOfFileToken"
}
fn identifier(kind: &str) -> bool {
    matches!(kind, "IdentifierToken" | "LocalLabelToken")
}

fn keyword(word: &str) -> Option<String> {
    let upper = word.to_ascii_uppercase();
    let special = match upper.as_str() {
        "GLOBAL" => Some("ExportKeyword"),
        "STATIC_ASSERT" | "STATICASSERT" => Some("StaticAssertKeyword"),
        "ISCONST" => Some("IsConstKeyword"),
        _ => None,
    };
    if let Some(s) = special {
        return Some(s.into());
    }
    const WORDS: &str = "NOP LD ADD ADC SUB SBC AND OR XOR CP INC DEC DAA CPL RLCA RLA RRCA RRA RLC RL RRC RR SLA SRA SRL SWAP BIT SET RES JP JR CALL RET RETI RST POP PUSH DI EI HALT STOP CCF SCF LDI LDD LDH Z NZ NC A B C D E H L HL SP AF BC DE HLI HLD SECTION DB DW DL DS EQU EQUS REDEF EXPORT GLOBAL PURGE IF ELIF ELSE ENDC MACRO ENDM SHIFT REPT FOR ENDR BREAK INCLUDE INCBIN CHARMAP NEWCHARMAP SETCHARMAP PRECHARMAP POPCHARMAP NEXTU ENDU LOAD ENDL RB RW RSRESET RSSET ASSERT WARN FAIL FATAL PRINT PRINTLN OPT PUSHS POPS PUSHO POPO ROM0 ROMX WRAM0 WRAMX VRAM HRAM SRAM OAM ALIGN FRAGMENT UNION HIGH LOW BANK SIZEOF STARTOF DEF STRLEN STRCAT STRSUB REVCHAR CHARLEN INCHARMAP STRCMP MUL DIV FMOD POW LOG ROUND CEIL FLOOR SIN COS TAN ASIN ACOS ATAN ATAN2 STRFIND STRRFIND STRUPR STRLWR BYTELEN STRBYTE STRCHAR READFILE BITWIDTH TZCOUNT STRFMT";
    if !WORDS.split_whitespace().any(|w| w == upper) {
        return None;
    }
    let lower = upper.to_ascii_lowercase();
    let mut chars = lower.chars();
    Some(format!(
        "{}{}Keyword",
        chars.next().unwrap().to_ascii_uppercase(),
        chars.as_str()
    ))
}

fn instruction(word: &str) -> bool {
    "NOP LD ADD ADC SUB SBC AND OR XOR CP INC DEC DAA CPL RLCA RLA RRCA RRA RLC RL RRC RR SLA SRA SRL SWAP BIT SET RES JP JR CALL RET RETI RST POP PUSH DI EI HALT STOP CCF SCF LDI LDD LDH".split_whitespace().any(|w| w == word)
}

fn register(kind: &str) -> bool {
    matches!(
        kind,
        "AKeyword"
            | "BKeyword"
            | "CKeyword"
            | "DKeyword"
            | "EKeyword"
            | "HKeyword"
            | "LKeyword"
            | "HlKeyword"
            | "SpKeyword"
            | "AfKeyword"
            | "BcKeyword"
            | "DeKeyword"
    )
}

fn location(doc: &Document, token: usize) -> Location {
    let t = &doc.tokens[token];
    Location {
        path: doc.path.clone(),
        start: t.start,
        length: t.length,
    }
}

fn diagnostic(doc: &Document, token: usize, message: impl Into<String>) -> Diagnostic {
    let p = location(doc, token);
    Diagnostic {
        path: p.path,
        start: p.start,
        length: p.length.max(1),
        severity: "Error".into(),
        message: message.into(),
    }
}

fn lex(source: &Source, diagnostics: &mut Vec<Diagnostic>) -> Vec<Token> {
    let text = &source.text;
    let mut tokens = Vec::new();
    let mut byte = 0;
    let mut utf16 = 0;
    let mut interpolations = Vec::new();
    while byte < text.len() {
        let start = byte;
        let rest = &text[byte..];
        let c = rest.chars().next().unwrap();
        let mut closed = true;
        let kind: String;
        if rest.starts_with("/*") {
            byte = rest[2..].find("*/").map_or(text.len(), |n| start + n + 4);
            closed = text[start..byte].ends_with("*/");
            kind = "BlockCommentTrivia".into();
        } else if c == ';' {
            byte += rest.find(['\n', '\r']).unwrap_or(rest.len());
            kind = "LineCommentTrivia".into();
        } else if matches!(c, '\n' | '\r') {
            byte += if rest.starts_with("\r\n") { 2 } else { 1 };
            kind = "NewlineTrivia".into();
        } else if c.is_whitespace() || c == '\u{feff}' {
            byte += c.len_utf8();
            while byte < text.len() {
                let next = text[byte..].chars().next().unwrap();
                if !next.is_whitespace() || matches!(next, '\n' | '\r') {
                    break;
                }
                byte += next.len_utf8();
            }
            kind = "WhitespaceTrivia".into();
        } else if c == '\\' {
            let after = &rest[1..];
            let stripped = after.trim_start_matches([' ', '\t']);
            let after_comment = if stripped.starts_with(';') {
                &stripped[stripped.find(['\n', '\r']).unwrap_or(stripped.len())..]
            } else {
                stripped
            };
            if after_comment.starts_with(['\n', '\r']) {
                byte = text.len() - after_comment.len()
                    + if after_comment.starts_with("\r\n") {
                        2
                    } else {
                        1
                    };
                kind = "WhitespaceTrivia".into();
            } else {
                byte += 1;
                if text[byte..].starts_with('<') {
                    byte += text[byte..].find('>').map_or(text.len() - byte, |n| n + 1);
                } else if let Some(n) = text[byte..].chars().next() {
                    byte += n.len_utf8();
                    if n.is_alphabetic() {
                        while byte < text.len()
                            && text[byte..].chars().next().unwrap().is_alphanumeric()
                        {
                            byte += text[byte..].chars().next().unwrap().len_utf8();
                        }
                    }
                }
                kind = "MacroParamToken".into();
            }
        } else if c == '"' || c == '\'' || rest.starts_with("#\"") {
            let raw = rest.starts_with("#\"");
            let quote = if raw { '"' } else { c };
            let opening = start + usize::from(raw);
            let triple = quote == '"' && text[opening..].starts_with("\"\"\"");
            let delimiter = if triple { 3 } else { 1 };
            byte = opening + delimiter;
            closed = false;
            while byte < text.len() {
                if (triple && text[byte..].starts_with("\"\"\""))
                    || (!triple && text[byte..].starts_with(quote))
                {
                    byte += delimiter;
                    closed = true;
                    break;
                }
                let next = text[byte..].chars().next().unwrap();
                if !triple && matches!(next, '\n' | '\r') {
                    break;
                }
                byte += next.len_utf8();
                if next == '\\' && !raw && byte < text.len() {
                    if text[byte..].starts_with("\r\n") {
                        byte += 2;
                    } else {
                        byte += text[byte..].chars().next().unwrap().len_utf8();
                    }
                }
            }
            kind = if quote == '\'' {
                "CharLiteralToken"
            } else {
                "StringLiteral"
            }
            .into();
        } else if c.is_ascii_digit()
            || c == '$'
            || c == '`'
            || (c == '%'
                && rest
                    .as_bytes()
                    .get(1)
                    .is_some_and(|b| matches!(b, b'0' | b'1')))
            || (c == '&'
                && rest
                    .as_bytes()
                    .get(1)
                    .is_some_and(|b| matches!(b, b'0'..=b'7')))
        {
            byte += c.len_utf8();
            while byte < text.len() {
                let n = text[byte..].chars().next().unwrap();
                if !(n.is_ascii_alphanumeric() || matches!(n, '_' | '.')) {
                    break;
                }
                byte += n.len_utf8();
            }
            kind = if text[start..byte].contains('.') {
                "FixedPointLiteral"
            } else {
                "NumberLiteral"
            }
            .into();
        } else if c.is_alphabetic()
            || matches!(c, '_' | '#')
            || (c == '.'
                && rest
                    .chars()
                    .nth(1)
                    .is_some_and(|c| c.is_alphabetic() || c == '_'))
        {
            byte += c.len_utf8();
            while byte < text.len() {
                let n = text[byte..].chars().next().unwrap();
                if !crate::source::ident(n) {
                    break;
                }
                byte += n.len_utf8();
            }
            let name = &text[start..byte];
            kind = if c == '.' {
                "LocalLabelToken".into()
            } else {
                keyword(name).unwrap_or_else(|| "IdentifierToken".into())
            };
        } else {
            let operator = [
                ("!==", "BangEqualsEqualsToken"),
                ("===", "TripleEqualsToken"),
                (">>>", "TripleGreaterThanToken"),
                ("<<", "LessThanLessThanToken"),
                (">>", "GreaterThanGreaterThanToken"),
                ("**", "StarStarToken"),
                ("++", "PlusPlusToken"),
                ("==", "EqualsEqualsToken"),
                ("!=", "BangEqualsToken"),
                ("<=", "LessThanEqualsToken"),
                (">=", "GreaterThanEqualsToken"),
                ("&&", "AmpersandAmpersandToken"),
                ("||", "PipePipeToken"),
                ("::", "DoubleColonToken"),
            ]
            .into_iter()
            .find(|(op, _)| rest.starts_with(op));
            if let Some((op, k)) = operator {
                byte += op.len();
                kind = k.into();
            } else if c == ':' && rest.chars().nth(1).is_some_and(|n| matches!(n, '+' | '-')) {
                let sign = rest.chars().nth(1).unwrap();
                byte += 1;
                while text[byte..].starts_with(sign) {
                    byte += 1;
                }
                kind = if sign == '+' {
                    "AnonLabelForwardToken"
                } else {
                    "AnonLabelBackwardToken"
                }
                .into();
            } else {
                byte += c.len_utf8();
                kind = match c {
                    ',' => "CommaToken",
                    '(' => "OpenParenToken",
                    ')' => "CloseParenToken",
                    '[' => "OpenBracketToken",
                    ']' => "CloseBracketToken",
                    ':' => "ColonToken",
                    '.' => "DotToken",
                    '+' => "PlusToken",
                    '-' => "MinusToken",
                    '*' => "StarToken",
                    '/' => "SlashToken",
                    '%' => "PercentToken",
                    '&' => "AmpersandToken",
                    '|' => "PipeToken",
                    '^' => "CaretToken",
                    '~' => "TildeToken",
                    '!' => "BangToken",
                    '<' => "LessThanToken",
                    '>' => "GreaterThanToken",
                    '=' => "EqualsToken",
                    '@' => "CurrentAddressToken",
                    '{' => {
                        interpolations.push(utf16);
                        "HashToken"
                    }
                    '}' => {
                        interpolations.pop();
                        "HashToken"
                    }
                    _ => "BadToken",
                }
                .into();
            }
        }
        let length = text[start..byte].encode_utf16().count();
        let token = Token {
            id: tokens.len(),
            kind,
            text: text[start..byte].into(),
            start: utf16,
            length,
            parent: 0,
        };
        if !closed || token.kind == "BadToken" {
            diagnostics.push(Diagnostic {
                path: source.path.clone(),
                start: utf16,
                length: length.max(1),
                severity: "Error".into(),
                message: if !closed {
                    "unterminated literal or comment".into()
                } else {
                    format!("unexpected character {c}")
                },
            });
        }
        utf16 += length;
        tokens.push(token);
    }
    tokens.push(Token {
        id: tokens.len(),
        kind: "EndOfFileToken".into(),
        text: String::new(),
        start: utf16,
        length: 0,
        parent: 0,
    });
    for start in interpolations {
        diagnostics.push(Diagnostic {
            path: source.path.clone(),
            start,
            length: 1,
            severity: "Error".into(),
            message: "unclosed interpolation".into(),
        });
    }
    tokens
}

impl Parsed {
    fn node(&mut self, kind: &str, ids: &[usize]) -> usize {
        let id = self.document.nodes.len();
        let start = ids.first().map_or(0, |i| self.document.tokens[*i].start);
        let end = ids.last().map_or(start, |i| {
            self.document.tokens[*i].start + self.document.tokens[*i].length
        });
        self.document.nodes.push(Node {
            id,
            kind: kind.into(),
            start,
            length: end - start,
            children: Vec::new(),
        });
        id
    }

    fn token(&mut self, parent: usize, token: usize) {
        self.document.tokens[token].parent = parent;
        self.document.nodes[parent]
            .children
            .push(Child::Token { token });
    }

    fn child(&mut self, parent: usize, node: usize) {
        self.document.nodes[parent]
            .children
            .push(Child::Node { node });
    }

    fn expression(&mut self, ids: &[usize], depth: usize) -> usize {
        for (index, token) in ids.iter().enumerate() {
            let t = &self.document.tokens[*token];
            if t.kind.ends_with("Keyword") && !register(&t.kind) {
                let function = ids[index + 1..]
                    .iter()
                    .find(|i| !trivia(&self.document.tokens[**i].kind))
                    .is_some_and(|i| self.document.tokens[*i].kind == "OpenParenToken");
                if !function {
                    self.document.tokens[*token].kind = "IdentifierToken".into();
                }
            }
        }
        let significant: Vec<usize> = ids
            .iter()
            .copied()
            .filter(|i| !trivia(&self.document.tokens[*i].kind))
            .collect();
        let kind = if significant.len() == 1 {
            if identifier(&self.document.tokens[significant[0]].kind) {
                "NameExpression"
            } else {
                "LiteralExpression"
            }
        } else if significant.first().is_some_and(|i| {
            matches!(
                self.document.tokens[*i].kind.as_str(),
                "PlusToken" | "MinusToken" | "BangToken" | "TildeToken"
            )
        }) {
            "UnaryExpression"
        } else {
            "BinaryExpression"
        };
        let id = self.node(kind, ids);
        let mut i = 0;
        while i < ids.len() {
            let token = ids[i];
            let t = &self.document.tokens[token];
            if (identifier(&t.kind) || t.kind.ends_with("Keyword")) && significant.len() > 1 {
                let next = ids[i + 1..]
                    .iter()
                    .position(|n| !trivia(&self.document.tokens[*n].kind))
                    .map(|n| n + i + 1);
                if depth < 64
                    && next.is_some_and(|n| self.document.tokens[ids[n]].kind == "OpenParenToken")
                {
                    let open = next.unwrap();
                    if let Some(end) = self.matching(ids, open) {
                        let function = self.node("FunctionCallExpression", &ids[i..=end]);
                        for n in &ids[i..=open] {
                            self.token(function, *n);
                        }
                        self.expressions(function, &ids[open + 1..end], depth + 1);
                        self.token(function, ids[end]);
                        self.child(id, function);
                        i = end + 1;
                        continue;
                    }
                }
                if identifier(&self.document.tokens[token].kind) {
                    let name = self.node("NameExpression", &[token]);
                    self.token(name, token);
                    self.child(id, name);
                } else {
                    self.token(id, token);
                }
            } else if depth < 64 && t.kind == "OpenParenToken" {
                if let Some(end) = self.matching(ids, i) {
                    let paren = self.node("ParenthesizedExpression", &ids[i..=end]);
                    self.token(paren, token);
                    self.expressions(paren, &ids[i + 1..end], depth + 1);
                    self.token(paren, ids[end]);
                    self.child(id, paren);
                    i = end + 1;
                    continue;
                }
                self.token(id, token);
            } else {
                self.token(id, token);
            }
            i += 1;
        }
        id
    }

    fn matching(&self, ids: &[usize], start: usize) -> Option<usize> {
        let mut depth = 0;
        for (index, token) in ids.iter().enumerate().skip(start) {
            match self.document.tokens[*token].kind.as_str() {
                "OpenParenToken" => depth += 1,
                "CloseParenToken" => {
                    depth -= 1;
                    if depth == 0 {
                        return Some(index);
                    }
                }
                _ => (),
            }
        }
        None
    }

    fn expressions(&mut self, parent: usize, ids: &[usize], depth: usize) {
        let mut start = 0;
        let mut nesting = 0;
        for (i, token) in ids.iter().enumerate() {
            match self.document.tokens[*token].kind.as_str() {
                "OpenParenToken" | "OpenBracketToken" => nesting += 1,
                "CloseParenToken" | "CloseBracketToken" => nesting -= 1,
                "CommaToken" if nesting == 0 => {
                    if i > start {
                        let expression = self.expression(&ids[start..i], depth);
                        self.child(parent, expression);
                    }
                    self.token(parent, *token);
                    start = i + 1;
                }
                _ => (),
            }
        }
        if start < ids.len() {
            let expression = self.expression(&ids[start..], depth);
            self.child(parent, expression);
        }
    }

    fn statement(&mut self, ids: &[usize], diagnostics: &mut Vec<Diagnostic>) {
        let sig: Vec<usize> = ids
            .iter()
            .copied()
            .filter(|i| !trivia(&self.document.tokens[*i].kind))
            .collect();
        if sig.is_empty() {
            for i in ids {
                self.token(0, *i);
            }
            return;
        }
        let mut label = None;
        let mut offset = 0;
        let mut declaration = None;
        if sig.len() >= 2
            && matches!(
                self.document.tokens[sig[1]].kind.as_str(),
                "ColonToken" | "DoubleColonToken"
            )
        {
            label = Some(sig[0]);
            offset = ids.iter().position(|i| *i == sig[1]).unwrap() + 1;
        } else if sig.len() >= 2
            && identifier(&self.document.tokens[sig[0]].kind)
            && (self.document.tokens[sig[0]].text.starts_with('.')
                || instruction(&self.document.tokens[sig[1]].text.to_ascii_uppercase()))
        {
            label = Some(sig[0]);
            offset = ids.iter().position(|i| *i == sig[0]).unwrap() + 1;
        }
        if let Some(name) = label {
            self.document.tokens[name].kind = if self.document.tokens[name].text.starts_with('.') {
                "LocalLabelToken"
            } else {
                "IdentifierToken"
            }
            .into();
            let exported = sig
                .get(1)
                .is_some_and(|i| self.document.tokens[*i].kind == "DoubleColonToken");
            let macro_label = ids[offset..]
                .iter()
                .find(|i| !trivia(&self.document.tokens[**i].kind))
                .is_some_and(|i| self.document.tokens[*i].text.eq_ignore_ascii_case("MACRO"));
            let node = self.node("LabelDeclaration", &ids[..offset]);
            for token in &ids[..offset] {
                self.token(node, *token);
            }
            self.child(0, node);
            self.statements.push(Statement {
                ids: ids[..offset].to_vec(),
                op: "LABEL".into(),
                head: Some(name),
                definition: Some((name, if macro_label { "Macro" } else { "Label" }.into())),
                macro_name: None,
                arity: 0,
                exported,
            });
        }
        let ids = &ids[offset..];
        let sig: Vec<usize> = ids
            .iter()
            .copied()
            .filter(|i| !trivia(&self.document.tokens[*i].kind))
            .collect();
        if sig.is_empty() {
            for i in ids {
                self.token(0, *i);
            }
            return;
        }
        let head = sig[0];
        let op = self.document.tokens[head].text.to_ascii_uppercase();
        let (kind, macro_name) = if op == "MACRO" {
            let name = label.or_else(|| sig.get(1).copied());
            if label.is_none() {
                declaration = name.map(|n| (n, "Macro".into()));
            }
            ("MacroDefinition", name)
        } else if op == "ENDM" {
            ("MacroDefinition", None)
        } else if matches!(op.as_str(), "DEF" | "REDEF") {
            if let Some(name) = sig.get(1) {
                declaration = Some((
                    *name,
                    if sig
                        .get(2)
                        .is_some_and(|i| self.document.tokens[*i].text.eq_ignore_ascii_case("EQUS"))
                    {
                        "StringConstant"
                    } else {
                        "Constant"
                    }
                    .into(),
                ));
            }
            ("SymbolDirective", None)
        } else if sig.get(1).is_some_and(|i| {
            matches!(
                self.document.tokens[*i].text.to_ascii_uppercase().as_str(),
                "EQU" | "EQUS" | "SET" | "=" | "RB" | "RW" | "RL"
            )
        }) {
            declaration = Some((
                head,
                if self.document.tokens[sig[1]]
                    .text
                    .eq_ignore_ascii_case("EQUS")
                {
                    "StringConstant"
                } else {
                    "Constant"
                }
                .into(),
            ));
            ("SymbolDirective", None)
        } else if matches!(op.as_str(), "EXPORT" | "GLOBAL" | "PURGE") {
            ("SymbolDirective", None)
        } else if op == "NEWCHARMAP" {
            declaration = sig.get(1).map(|i| (*i, "CharMap".into()));
            ("DirectiveStatement", None)
        } else if matches!(op.as_str(), "SECTION" | "LOAD") {
            ("SectionDirective", None)
        } else if matches!(op.as_str(), "DB" | "DW" | "DL" | "DS") {
            ("DataDirective", None)
        } else if matches!(op.as_str(), "IF" | "ELIF" | "ELSE" | "ENDC") {
            ("ConditionalDirective", None)
        } else if matches!(op.as_str(), "REPT" | "FOR" | "ENDR") {
            ("RepeatDirective", None)
        } else if matches!(op.as_str(), "INCLUDE" | "INCBIN") {
            ("IncludeDirective", None)
        } else if instruction(&op) {
            ("InstructionStatement", None)
        } else if self.document.tokens[head].kind == "IdentifierToken" {
            ("MacroCall", None)
        } else {
            ("DirectiveStatement", None)
        };
        let node = self.node(kind, ids);
        self.child(0, node);
        if let Some((token, _)) = declaration.as_ref() {
            if !matches!(
                self.document.tokens[*token].kind.as_str(),
                "StringLiteral" | "NumberLiteral"
            ) {
                self.document.tokens[*token].kind =
                    if self.document.tokens[*token].text.starts_with('.') {
                        "LocalLabelToken"
                    } else {
                        "IdentifierToken"
                    }
                    .into();
            }
        }
        let start_args = if declaration.is_some() && kind == "SymbolDirective" {
            let count = if matches!(op.as_str(), "DEF" | "REDEF") {
                3
            } else {
                2
            };
            sig.get(count)
                .and_then(|token| ids.iter().position(|i| i == token))
                .unwrap_or(ids.len())
        } else {
            ids.iter().position(|i| *i == head).unwrap() + 1
        };
        if (matches!(
            kind,
            "InstructionStatement"
                | "MacroCall"
                | "DataDirective"
                | "IncludeDirective"
                | "ConditionalDirective"
                | "RepeatDirective"
        ) || (kind == "DirectiveStatement" && declaration.is_none()))
            || (kind == "SymbolDirective" && declaration.is_some())
        {
            for token in &ids[..start_args] {
                self.token(node, *token);
            }
            if kind == "InstructionStatement" {
                self.operands(node, &ids[start_args..], &op);
            } else {
                self.expressions(node, &ids[start_args..], 0);
            }
        } else {
            for token in ids {
                self.token(node, *token);
            }
        }
        if op == "INCLUDE" {
            if let Some(token) = sig
                .iter()
                .find(|i| self.document.tokens[**i].kind == "StringLiteral")
            {
                let t = &self.document.tokens[*token];
                if let Ok(path) = crate::expr::parse(&t.text)
                    .and_then(|e| crate::expr::eval_string(&e, &|_| None, 0))
                {
                    if !path.is_empty() {
                        self.document.includes.push(Include {
                            path,
                            start: t.start,
                            length: t.length,
                        });
                    }
                }
            }
        }
        if kind == "InstructionStatement"
            && !ids.iter().any(|i| {
                self.document.tokens[*i].kind == "MacroParamToken"
                    || self.document.tokens[*i].text == "{"
            })
        {
            let arg_text = self.clean_text(&ids[start_args..]);
            match crate::source::args(&arg_text).and_then(|a| crate::encode::encode(&op, &a)) {
                Ok(_) => (),
                Err(message) => diagnostics.push(diagnostic(&self.document, head, message)),
            }
        }
        self.statements.push(Statement {
            ids: ids.to_vec(),
            op,
            head: Some(head),
            definition: declaration,
            macro_name,
            arity: 0,
            exported: false,
        });
    }

    fn operands(&mut self, parent: usize, ids: &[usize], op: &str) {
        let mut start = 0;
        let mut nesting = 0;
        let mut groups = Vec::new();
        for (i, token) in ids.iter().enumerate() {
            match self.document.tokens[*token].kind.as_str() {
                "OpenParenToken" | "OpenBracketToken" => nesting += 1,
                "CloseParenToken" | "CloseBracketToken" => nesting -= 1,
                "CommaToken" if nesting == 0 => {
                    groups.push((start, i, Some(*token)));
                    start = i + 1;
                }
                _ => (),
            }
        }
        groups.push((start, ids.len(), None));
        for (index, (start, end, comma)) in groups.into_iter().enumerate() {
            let part = &ids[start..end];
            let sig: Vec<_> = part
                .iter()
                .copied()
                .filter(|i| !trivia(&self.document.tokens[*i].kind))
                .collect();
            if !sig.is_empty() {
                for token in &sig {
                    let t = &self.document.tokens[*token];
                    if t.kind.ends_with("Keyword")
                        && !register(&t.kind)
                        && !matches!(t.kind.as_str(), "ZKeyword" | "NzKeyword" | "NcKeyword")
                    {
                        let function = sig
                            .iter()
                            .position(|i| i == token)
                            .and_then(|i| sig.get(i + 1))
                            .is_some_and(|i| self.document.tokens[*i].kind == "OpenParenToken");
                        if !function {
                            self.document.tokens[*token].kind = "IdentifierToken".into();
                        }
                    }
                }
                let first = &self.document.tokens[sig[0]];
                let kind = if matches!(op, "JP" | "JR" | "CALL" | "RET")
                    && index == 0
                    && matches!(
                        first.text.to_ascii_uppercase().as_str(),
                        "Z" | "NZ" | "NC" | "C"
                    ) {
                    "ConditionOperand"
                } else if sig.len() == 1 && register(&first.kind) {
                    "RegisterOperand"
                } else if first.kind == "OpenBracketToken" {
                    "IndirectOperand"
                } else if sig.len() == 1 && identifier(&first.kind) {
                    "LabelOperand"
                } else {
                    "ImmediateOperand"
                };
                let operand = self.node(kind, part);
                self.child(parent, operand);
                if matches!(
                    kind,
                    "RegisterOperand" | "ConditionOperand" | "LabelOperand"
                ) {
                    for token in part {
                        self.token(operand, *token);
                    }
                } else {
                    self.expressions(operand, part, 0);
                }
            } else {
                for token in part {
                    self.token(parent, *token);
                }
            }
            if let Some(token) = comma {
                self.token(parent, token);
            }
        }
    }

    fn clean_text(&self, ids: &[usize]) -> String {
        ids.iter()
            .map(|i| {
                let token = &self.document.tokens[*i];
                if trivia(&token.kind) {
                    " "
                } else {
                    token.text.as_str()
                }
            })
            .collect()
    }
}

fn parse_document(source: Source, diagnostics: &mut Vec<Diagnostic>) -> Parsed {
    let tokens = lex(&source, diagnostics);
    let mut parsed = Parsed {
        document: Document {
            path: source.path.clone(),
            nodes: vec![Node {
                id: 0,
                kind: "CompilationUnit".into(),
                start: 0,
                length: source.text.encode_utf16().count(),
                children: Vec::new(),
            }],
            tokens,
            scopes: Vec::new(),
            includes: Vec::new(),
        },
        statements: Vec::new(),
    };
    let mut start = 0;
    for i in 0..parsed.document.tokens.len() {
        let kind = &parsed.document.tokens[i].kind;
        let separator = if kind == "DoubleColonToken" {
            let significant: Vec<_> = (start..i)
                .filter(|n| !trivia(&parsed.document.tokens[*n].kind))
                .collect();
            !(significant.len() == 1 && identifier(&parsed.document.tokens[significant[0]].kind))
        } else {
            matches!(kind.as_str(), "NewlineTrivia" | "EndOfFileToken")
        };
        if separator {
            parsed.statement(&(start..i).collect::<Vec<_>>(), diagnostics);
            parsed.token(0, i);
            start = i + 1;
        }
    }
    let mut macros: Vec<usize> = Vec::new();
    for i in 0..parsed.statements.len() {
        if parsed.statements[i].op == "MACRO" {
            macros.push(i);
        }
        if let Some(current) = macros.last().copied() {
            let mut arity = parsed.statements[current].arity;
            for token in &parsed.statements[i].ids {
                let t = &parsed.document.tokens[*token];
                if t.kind == "MacroParamToken" {
                    if let Ok(n) = t.text[1..].parse::<usize>() {
                        arity = arity.max(n);
                    }
                }
            }
            parsed.statements[current].arity = arity;
        }
        if parsed.statements[i].op == "ENDM" {
            macros.pop();
        }
    }
    parsed
}

fn normalize(path: &str) -> String {
    crate::assembler::source_path(path)
        .to_string_lossy()
        .replace('\\', "/")
}

fn included(path: &str, relative: &str) -> String {
    if relative.starts_with('/')
        || relative.starts_with("file://")
        || relative.as_bytes().get(1) == Some(&b':')
    {
        return normalize(relative);
    }
    let path = normalize(path);
    let parent = path.rsplit_once('/').map_or("", |s| s.0);
    normalize(&format!("{parent}/{relative}"))
}

#[derive(Clone)]
struct Reference {
    owner: String,
    name: String,
    location: Location,
    macro_call: bool,
}

#[derive(Default)]
struct Context {
    anchor: String,
    section: Option<String>,
    macros: Vec<String>,
    strings: HashMap<String, String>,
}

struct Semantic<'a> {
    parsed: &'a mut Vec<Parsed>,
    by_path: HashMap<String, usize>,
    overlay_paths: HashMap<String, usize>,
    symbols: Vec<Symbol>,
    definitions: HashMap<(String, String), usize>,
    references: Vec<Reference>,
    exports: HashSet<(String, String)>,
    diagnostics: Vec<Diagnostic>,
    reachable: HashSet<usize>,
}

impl Semantic<'_> {
    fn file_index(&self, path: &str) -> Option<usize> {
        self.overlay_paths
            .get(&crate::assembler::overlay_key(path))
            .or_else(|| self.by_path.get(&normalize(path)))
            .copied()
    }
    fn qualify(ctx: &Context, name: &str) -> String {
        let name = name.strip_prefix('#').unwrap_or(name);
        if name.starts_with('.') {
            format!("{}{name}", ctx.anchor)
        } else {
            name.into()
        }
    }

    fn walk(&mut self, index: usize, owner: &str, ctx: &mut Context, stack: &mut HashSet<usize>) {
        self.reachable.insert(index);
        if stack.len() >= 64 || !stack.insert(index) {
            return;
        }
        let statements = self.parsed[index].statements.clone();
        let path = self.parsed[index].document.path.clone();
        let mut scope_start = 0;
        let mut scope_name = ctx.anchor.clone();
        for statement in statements {
            if let Some((token, kind)) = &statement.definition {
                let token = *token;
                let name = Self::qualify(ctx, &self.parsed[index].document.tokens[token].text);
                if kind == "Label"
                    && !self.parsed[index].document.tokens[token]
                        .text
                        .starts_with('.')
                {
                    if !scope_name.is_empty() {
                        let end = self.parsed[index].document.tokens[token].start;
                        self.parsed[index].document.scopes.push(Scope {
                            start: scope_start,
                            end,
                            name: scope_name,
                        });
                    }
                    ctx.anchor = name.clone();
                    scope_start = self.parsed[index].document.tokens[token].start;
                    scope_name = name.clone();
                }
                let key = (owner.to_owned(), name.clone());
                let location = location(&self.parsed[index].document, token);
                let existing = self.definitions.get(&key).copied();
                let redef = statement.op == "REDEF";
                if existing.is_none() || redef {
                    let value = if kind == "Constant" {
                        self.constant(index, &statement, owner, ctx)
                    } else {
                        None
                    };
                    let symbol = Symbol {
                        id: format!("{owner}::{name}"),
                        name: name.clone(),
                        kind: kind.clone(),
                        visibility: if statement.exported {
                            "Exported"
                        } else {
                            "Local"
                        }
                        .into(),
                        value,
                        section: if kind == "Label" && ctx.macros.is_empty() {
                            ctx.section.clone()
                        } else {
                            None
                        },
                        owner: owner.into(),
                        definition: Some(location),
                        references: Vec::new(),
                        macro_arity: 0,
                    };
                    if let Some(existing) = existing {
                        self.symbols[existing] = symbol;
                    } else {
                        let id = self.symbols.len();
                        self.definitions.insert(key.clone(), id);
                        self.symbols.push(symbol);
                    }
                }
                if statement.exported {
                    self.exports.insert(key);
                }
                if kind == "StringConstant" && ctx.macros.is_empty() {
                    if let Some(raw) = self.declaration_text(index, &statement) {
                        if let Some(value) = self.string_value(&raw, owner, ctx) {
                            ctx.strings.insert(name, value);
                        }
                    }
                }
            }
            if statement.op == "MACRO" {
                if let Some(token) = statement.macro_name {
                    let name = Self::qualify(ctx, &self.parsed[index].document.tokens[token].text);
                    if let Some(id) = self.definitions.get(&(owner.into(), name.clone())) {
                        self.symbols[*id].macro_arity = statement.arity;
                    }
                    ctx.macros.push(ctx.anchor.clone());
                    ctx.anchor = format!("__macro_{name}");
                }
            } else if statement.op == "ENDM" {
                if let Some(anchor) = ctx.macros.pop() {
                    ctx.anchor = anchor;
                }
            }
            if statement.op == "SECTION" || statement.op == "LOAD" {
                let ids = &statement.ids;
                if let Some(token) = ids
                    .iter()
                    .find(|i| self.parsed[index].document.tokens[**i].kind == "StringLiteral")
                {
                    let value = &self.parsed[index].document.tokens[*token].text;
                    ctx.section = crate::expr::parse(value)
                        .and_then(|e| crate::expr::eval_string(&e, &|_| None, 0))
                        .ok();
                }
            }
            if statement.op == "INCLUDE" {
                if let Some(head) = statement.head {
                    let after = statement.ids.iter().position(|i| *i == head).unwrap() + 1;
                    let ids = &statement.ids[after..];
                    if let Some(relative) = self
                        .string_value(&self.parsed[index].clean_text(ids), owner, ctx)
                        .filter(|s| !s.is_empty())
                    {
                        let target = included(&path, &relative);
                        if self.file_index(&target).is_none() && self.parsed.len() < 4096 {
                            if let Ok(text) = std::fs::read_to_string(
                                target.strip_prefix("file://").unwrap_or(&target),
                            ) {
                                let child = self.parsed.len();
                                self.parsed.push(parse_document(
                                    Source {
                                        path: target.clone(),
                                        text,
                                    },
                                    &mut self.diagnostics,
                                ));
                                self.by_path.insert(target.clone(), child);
                            }
                        }
                        let tokens: Vec<_> = ids
                            .iter()
                            .filter(|i| !trivia(&self.parsed[index].document.tokens[**i].kind))
                            .copied()
                            .collect();
                        if let (Some(first), Some(last)) = (tokens.first(), tokens.last()) {
                            let first = &self.parsed[index].document.tokens[*first];
                            let last = &self.parsed[index].document.tokens[*last];
                            let include = Include {
                                path: relative,
                                start: first.start,
                                length: last.start + last.length - first.start,
                            };
                            if !self.parsed[index]
                                .document
                                .includes
                                .iter()
                                .any(|i| i.start == include.start && i.path == include.path)
                            {
                                self.parsed[index].document.includes.push(include);
                            }
                        }
                        if let Some(child) = self.file_index(&target) {
                            self.walk(child, owner, ctx, stack);
                        }
                    }
                }
            }
            for token in &statement.ids {
                let t = &self.parsed[index].document.tokens[*token];
                if !identifier(&t.kind)
                    || statement.definition.as_ref().is_some_and(|d| d.0 == *token)
                    || statement.macro_name == Some(*token)
                {
                    continue;
                }
                let parent = &self.parsed[index].document.nodes[t.parent].kind;
                let is_export = matches!(statement.op.as_str(), "EXPORT" | "GLOBAL");
                if matches!(
                    parent.as_str(),
                    "NameExpression" | "LabelOperand" | "MacroCall"
                ) || is_export
                {
                    let name = Self::qualify(ctx, &t.text);
                    if is_export {
                        self.exports.insert((owner.into(), name.clone()));
                    }
                    self.references.push(Reference {
                        owner: owner.into(),
                        name,
                        location: location(&self.parsed[index].document, *token),
                        macro_call: statement.head == Some(*token) && parent == "MacroCall",
                    });
                }
            }
        }
        if !scope_name.is_empty() {
            let end = self.parsed[index].document.nodes[0].length;
            self.parsed[index].document.scopes.push(Scope {
                start: scope_start,
                end,
                name: scope_name,
            });
        }
        stack.remove(&index);
    }

    fn declaration_text(&self, index: usize, statement: &Statement) -> Option<String> {
        let name = statement.definition.as_ref()?.0;
        let ids = &statement.ids;
        let after = ids.iter().position(|i| *i == name)? + 1;
        let operator = ids[after..]
            .iter()
            .position(|i| !trivia(&self.parsed[index].document.tokens[*i].kind))?
            + after;
        Some(self.parsed[index].clean_text(&ids[operator + 1..]))
    }

    fn string_value(&self, raw: &str, owner: &str, ctx: &Context) -> Option<String> {
        fn freeze(
            expr: crate::expr::Expr,
            strings: &HashMap<String, String>,
            depth: usize,
        ) -> Option<crate::expr::Expr> {
            if depth > 64 {
                return None;
            }
            use crate::expr::Expr;
            Some(match expr {
                Expr::Symbol(name) => match strings.get(&name) {
                    Some(value) => freeze(crate::expr::parse(value).ok()?, strings, depth + 1)?,
                    None => Expr::Symbol(name),
                },
                Expr::Unary(op, a) => Expr::Unary(op, Box::new(freeze(*a, strings, depth + 1)?)),
                Expr::Binary(op, a, b) => Expr::Binary(
                    op,
                    Box::new(freeze(*a, strings, depth + 1)?),
                    Box::new(freeze(*b, strings, depth + 1)?),
                ),
                Expr::Call(name, args) => Expr::Call(
                    name,
                    args.into_iter()
                        .map(|a| freeze(a, strings, depth + 1))
                        .collect::<Option<_>>()?,
                ),
                expr => expr,
            })
        }
        let tree = freeze(crate::expr::parse(raw).ok()?, &ctx.strings, 0)?;
        if tree.references_pc() {
            return None;
        }
        crate::expr::eval_string(
            &tree,
            &|name| {
                self.definitions
                    .get(&(owner.into(), Self::qualify(ctx, name)))
                    .and_then(|i| self.symbols[*i].value)
            },
            0,
        )
        .ok()
    }

    fn constant(
        &self,
        index: usize,
        statement: &Statement,
        owner: &str,
        ctx: &Context,
    ) -> Option<i32> {
        let raw = self.declaration_text(index, statement)?;
        let tree = crate::expr::parse(&raw).ok()?;
        if tree.references_pc() {
            return None;
        }
        crate::expr::eval(
            &tree,
            &|n| {
                self.definitions
                    .get(&(owner.into(), Self::qualify(ctx, n)))
                    .and_then(|i| self.symbols[*i].value)
            },
            0,
        )
        .ok()
    }

    fn resolve(mut self) -> (Vec<Symbol>, Vec<Diagnostic>) {
        for (owner, name) in &self.exports {
            if let Some(id) = self.definitions.get(&(owner.clone(), name.clone())) {
                self.symbols[*id].visibility = "Exported".into();
            }
        }
        for reference in self.references {
            let local = self
                .definitions
                .get(&(reference.owner.clone(), reference.name.clone()))
                .copied();
            let macro_local = if reference.macro_call {
                self.symbols.iter().position(|s| {
                    s.owner == reference.owner
                        && s.kind == "Macro"
                        && s.name.eq_ignore_ascii_case(&reference.name)
                })
            } else {
                None
            };
            let exported: Vec<_> = self
                .symbols
                .iter()
                .enumerate()
                .filter(|(_, s)| s.visibility == "Exported" && s.name == reference.name)
                .map(|(i, _)| i)
                .collect();
            if let Some(id) = local
                .or(macro_local)
                .or_else(|| (exported.len() == 1).then(|| exported[0]))
            {
                if self.symbols[id].definition.as_ref() != Some(&reference.location)
                    && !self.symbols[id].references.contains(&reference.location)
                {
                    self.symbols[id].references.push(reference.location);
                }
            }
        }
        (self.symbols, self.diagnostics)
    }
}

pub fn parse(sources: &[Source]) -> Analysis {
    let mut diagnostics = Vec::new();
    let documents = sources
        .iter()
        .cloned()
        .map(|source| parse_document(source, &mut diagnostics).document)
        .collect();
    Analysis {
        documents,
        symbols: Vec::new(),
        diagnostics,
    }
}

pub fn analyze(sources: &[Source], roots: &[String]) -> Analysis {
    let mut diagnostics = Vec::new();
    let mut parsed: Vec<_> = sources
        .iter()
        .cloned()
        .map(|s| parse_document(s, &mut diagnostics))
        .collect();
    let known: HashMap<_, _> = parsed
        .iter()
        .enumerate()
        .map(|(i, p)| (normalize(&p.document.path), i))
        .collect();
    let overlay_paths = parsed
        .iter()
        .enumerate()
        .map(|(i, p)| (crate::assembler::overlay_key(&p.document.path), i))
        .collect();
    let roots: Vec<_> = if roots.is_empty() {
        sources.iter().map(|s| s.path.clone()).collect()
    } else {
        roots.to_vec()
    };
    let mut semantic = Semantic {
        parsed: &mut parsed,
        by_path: known,
        overlay_paths,
        symbols: Vec::new(),
        definitions: HashMap::new(),
        references: Vec::new(),
        exports: HashSet::new(),
        diagnostics: Vec::new(),
        reachable: HashSet::new(),
    };
    for root in roots {
        if let Some(index) = semantic.file_index(&root) {
            semantic.walk(index, &root, &mut Context::default(), &mut HashSet::new());
        }
    }
    let reachable: HashSet<_> = semantic
        .reachable
        .iter()
        .map(|i| semantic.parsed[*i].document.path.clone())
        .collect();
    diagnostics.retain(|d| reachable.contains(&d.path));
    let (symbols, semantic_diagnostics) = semantic.resolve();
    diagnostics.extend(semantic_diagnostics);
    Analysis {
        documents: parsed.into_iter().map(|p| p.document).collect(),
        symbols,
        diagnostics,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn source(path: &str, text: &str) -> Source {
        Source {
            path: path.into(),
            text: text.into(),
        }
    }

    #[test]
    fn unicode_spans_and_parent_contexts_are_utf16() {
        let input = source(
            "/unicode.asm",
            "; 😀\nSECTION \"é\",ROM0\nStart:\n ld a,Start+1\n",
        );
        let result = analyze(&[input.clone()], &[input.path.clone()]);
        let doc = &result.documents[0];
        let names: Vec<_> = doc.tokens.iter().filter(|t| t.text == "Start").collect();
        assert_eq!(names.len(), 2);
        assert_eq!(
            names[0].start,
            input.text[..input.text.find("Start:").unwrap()]
                .encode_utf16()
                .count()
        );
        assert_eq!(doc.nodes[names[0].parent].kind, "LabelDeclaration");
        assert_eq!(doc.nodes[names[1].parent].kind, "NameExpression");
        assert_eq!(result.symbols[0].references.len(), 1);
        assert_eq!(doc.tokens[0].length, 4);
    }

    #[test]
    fn includes_scopes_and_owner_local_symbols_resolve() {
        let result = analyze(
            &[
                source(
                    "/project/main.asm",
                    "SECTION \"Main\",ROM0\nMain:\n INCLUDE \"common.inc\"\n jr .loop\n jp Helper\n",
                ),
                source("/project/common.inc", ".loop:\n nop\nHelper:\n ret\n"),
                source("/other.asm", "Helper:\n jp Helper\n"),
            ],
            &["/project/main.asm".into()],
        );
        let local = result
            .symbols
            .iter()
            .find(|s| s.name == "Main.loop")
            .unwrap();
        assert!(
            result
                .symbols
                .iter()
                .all(|s| s.owner == "/project/main.asm")
        );
        assert_eq!(
            local.definition.as_ref().unwrap().path,
            "/project/common.inc"
        );
        assert_eq!(result.documents[0].includes[0].path, "common.inc");
        assert!(
            result
                .symbols
                .iter()
                .find(|s| s.name == "Helper")
                .unwrap()
                .references
                .iter()
                .any(|r| r.path == "/project/main.asm")
        );
    }

    #[test]
    fn exported_lookup_does_not_override_owner_local_shadow() {
        let result = analyze(
            &[
                source("/a.asm", "Shared:: nop\n"),
                source("/b.asm", "Shared: nop\n jp Shared\n"),
                source("/c.asm", "jp Shared\n"),
            ],
            &[],
        );
        let a = result.symbols.iter().find(|s| s.owner == "/a.asm").unwrap();
        let b = result.symbols.iter().find(|s| s.owner == "/b.asm").unwrap();
        assert_eq!(a.references.len(), 1);
        assert_eq!(a.references[0].path, "/c.asm");
        assert_eq!(b.references.len(), 1);
        assert_eq!(b.references[0].path, "/b.asm");
        assert_ne!(a.id, b.id);
    }

    #[test]
    fn macro_arity_comments_and_nested_argument_nodes_survive() {
        let input = source(
            "/macro.asm",
            "MACRO Emit\n ld a,\\1\n db \\3\nENDM\nEmit HIGH(Value),2,3 ; Value is a comment\nDEF Value EQU 42\n",
        );
        let result = analyze(&[input], &[]);
        let mac = result.symbols.iter().find(|s| s.kind == "Macro").unwrap();
        assert_eq!(mac.macro_arity, 3);
        assert_eq!(mac.references.len(), 1);
        assert!(
            result.documents[0]
                .tokens
                .iter()
                .any(|t| t.kind == "LineCommentTrivia")
        );
        let value = result.symbols.iter().find(|s| s.name == "Value").unwrap();
        assert_eq!(value.value, Some(42));
        assert_eq!(value.references.len(), 1);
        assert!(
            result.documents[0]
                .nodes
                .iter()
                .any(|n| n.kind == "MacroCall")
        );
    }

    #[test]
    fn invalid_instruction_and_unterminated_literal_are_diagnostics() {
        let result = analyze(&[source("/bad.asm", "ld a,\ndb \"unterminated\n")], &[]);
        assert!(result.diagnostics.len() >= 2);
        assert!(result.diagnostics.iter().all(|d| d.path == "/bad.asm"));
    }

    #[test]
    fn parse_has_no_binding_errors_and_unreachable_overlays_are_isolated() {
        let unknown = source("/unknown.asm", "DEF Value EQU Missing+1\n");
        assert!(parse(&[unknown.clone()]).diagnostics.is_empty());
        assert_eq!(analyze(&[unknown], &[]).symbols[0].value, None);
        let result = analyze(
            &[
                source("/good.asm", "SECTION \"Main\",ROM0\nnop\n"),
                source("/bad.asm", "ld a,\n"),
            ],
            &["/good.asm".into()],
        );
        assert!(result.diagnostics.is_empty());
    }

    #[test]
    fn keyword_spelled_labels_and_empty_include_paths_keep_editor_semantics() {
        let result = analyze(
            &[source(
                "/keyword.asm",
                "SECTION \"Main\",ROM0\nGlobal:\n nop\n jp Global\nINCLUDE \"\"\n",
            )],
            &[],
        );
        let doc = &result.documents[0];
        assert!(doc.includes.is_empty());
        let names: Vec<_> = doc.tokens.iter().filter(|t| t.text == "Global").collect();
        assert!(names.iter().all(|t| t.kind == "IdentifierToken"));
        assert_eq!(result.symbols[0].references.len(), 1);
        assert_eq!(result.symbols[0].value, None);
    }

    #[test]
    fn dynamic_overlay_includes_use_native_expression_strings() {
        let result = analyze(
            &[
                source(
                    "/main.asm",
                    "DEF File EQUS STRCAT(\"\\\"shared\",\".inc\\\"\")\nSECTION \"Main\",ROM0\nINCLUDE File\njp Target\n",
                ),
                source("/shared.inc", "Target: nop\n"),
            ],
            &["/main.asm".into()],
        );
        let target = result.symbols.iter().find(|s| s.name == "Target").unwrap();
        assert_eq!(target.owner, "/main.asm");
        assert_eq!(target.references.len(), 1);
        assert_eq!(result.documents[0].includes[0].path, "shared.inc");
        let file = result.symbols.iter().find(|s| s.name == "File").unwrap();
        assert_eq!(file.references.len(), 1);
    }

    #[test]
    fn interpolation_placeholders_preserve_reference_spans_without_syntax_errors() {
        let input = source(
            "/interpolation.asm",
            "DEF Value EQU 7\nSECTION \"Main\",ROM0\nld a,{Value}\n",
        );
        let result = analyze(&[input.clone()], &[]);
        assert!(result.diagnostics.is_empty(), "{:?}", result.diagnostics);
        let value = result.symbols.iter().find(|s| s.name == "Value").unwrap();
        assert_eq!(value.references.len(), 1);
        assert_eq!(
            value.references[0].start,
            input.text.rfind("Value").unwrap()
        );
    }

    #[test]
    fn dynamic_includes_load_missing_disk_sources_under_the_root_owner() {
        struct Fixture(std::path::PathBuf);
        impl Drop for Fixture {
            fn drop(&mut self) {
                let _ = std::fs::remove_dir_all(&self.0);
            }
        }
        let fixture = Fixture(std::env::temp_dir().join(format!(
                "koh-analysis-include-{}-{}",
                std::process::id(),
                std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .unwrap()
                    .as_nanos()
            )));
        std::fs::create_dir(&fixture.0).unwrap();
        std::fs::write(fixture.0.join("body.inc"), "Target: nop\n").unwrap();
        let root = fixture.0.join("main.asm").to_string_lossy().into_owned();
        let result = analyze(
            &[source(
                &root,
                "DEF File EQUS \"\\\"body.inc\\\"\"\nSECTION \"Main\",ROM0\nINCLUDE File\njp Target\n",
            )],
            &[root.clone()],
        );
        assert_eq!(result.documents.len(), 2);
        let target = result.symbols.iter().find(|s| s.name == "Target").unwrap();
        assert_eq!(target.owner, root);
        assert_eq!(target.references.len(), 1);
    }
}
