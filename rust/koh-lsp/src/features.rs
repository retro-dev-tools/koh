use crate::{
    symbols::{self, child_tokens, span},
    workspace::Workspace,
};
use serde_json::{Value, json};
use std::path::Path;

pub fn inlay_hints(workspace: &Workspace, path: &Path, requested: &Value) -> Value {
    let Some(doc) = workspace.document(path) else {
        return Value::Null;
    };
    if workspace.project_for(path).is_none() {
        return Value::Null;
    }
    let start = doc.lines.offset(&requested["start"]);
    let end = doc.lines.offset(&requested["end"]);
    let mut hints = Vec::new();
    for token in doc.syntax["tokens"].as_array().into_iter().flatten() {
        if !matches!(
            token["kind"].as_str(),
            Some("IdentifierToken" | "LocalLabelToken")
        ) {
            continue;
        }
        let (offset, length) = span(token);
        if offset < start || offset > end {
            continue;
        }
        let Some(node) = symbols::parent(&doc.syntax, token) else {
            continue;
        };
        if !matches!(
            node["kind"].as_str(),
            Some("NameExpression" | "LabelOperand")
        ) {
            continue;
        }
        let Some(resolved) = symbols::resolve_at(workspace, path, offset) else {
            continue;
        };
        let Some(value) = resolved.symbol["value"].as_i64() else {
            continue;
        };
        let label = match resolved.symbol["kind"].as_str() {
            Some("Label") => format!(" = ${value:04X}"),
            Some("Constant") => format!(" = ${value:04X} ({value})"),
            _ => continue,
        };
        hints.push(json!({"position":doc.lines.position(offset+length),"label":label,"kind":1,"paddingLeft":true}));
    }
    json!(hints)
}

pub fn signature(workspace: &Workspace, path: &Path, position: &Value) -> Value {
    let Some(doc) = workspace.document(path) else {
        return Value::Null;
    };
    let offset = doc.lines.offset(position);
    let Some(call) = doc.syntax["nodes"]
        .as_array()
        .into_iter()
        .flatten()
        .find(|node| {
            let (start, length) = span(node);
            node["kind"] == "MacroCall" && start <= offset && offset < start.saturating_add(length)
        })
    else {
        return Value::Null;
    };
    let Some(name) = child_tokens(&doc.syntax, call).next() else {
        return Value::Null;
    };
    let Some(resolved) = symbols::resolve_at(workspace, path, span(name).0) else {
        return Value::Null;
    };
    if resolved.symbol["kind"] != "Macro" {
        return Value::Null;
    }
    let arity = resolved.symbol["macroArity"].as_u64().unwrap_or(0) as usize;
    let mut commas: usize = 0;
    let mut depth: usize = 0;
    for child in call["children"].as_array().into_iter().flatten() {
        if let Some(index) = child["token"].as_u64() {
            let Some(token) = doc.syntax["tokens"]
                .as_array()
                .and_then(|tokens| tokens.get(index as usize))
            else {
                continue;
            };
            if span(token).0 >= offset {
                break;
            }
            match token["kind"].as_str() {
                Some("OpenParenToken") => depth += 1,
                Some("CloseParenToken") => depth = depth.saturating_sub(1),
                Some("CommaToken") if depth == 0 => commas += 1,
                _ => {}
            }
        } else if let Some(index) = child["node"].as_u64()
            && doc.syntax["nodes"]
                .as_array()
                .and_then(|nodes| nodes.get(index as usize))
                .is_some_and(|node| span(node).0 >= offset)
        {
            break;
        }
    }
    let active = commas.min(arity.saturating_sub(1));
    let labels: Vec<_> = (1..=arity).map(|index| format!("\\{index}")).collect();
    let parameters: Vec<_> = labels.iter().map(|label| json!({"label":label})).collect();
    let label = format!(
        "{}({})",
        name["text"].as_str().unwrap_or(""),
        labels.join(", ")
    );
    json!({"signatures":[{"label":label,"parameters":parameters,"activeParameter":active}],"activeSignature":0,"activeParameter":active})
}

pub fn document_symbols(workspace: &Workspace, path: &Path) -> Value {
    let Some(doc) = workspace.document(path) else {
        return Value::Null;
    };
    let mut symbols = Vec::new();
    for node in doc.syntax["nodes"].as_array().into_iter().flatten() {
        let mut tokens = child_tokens(&doc.syntax, node);
        let selected = match node["kind"].as_str() {
            Some("LabelDeclaration") => tokens.next().map(|token| (token, 12)),
            Some("SymbolDirective") => {
                let first = tokens.next();
                let second = tokens.next();
                match (first, second) {
                    (Some(first), Some(second))
                        if first["kind"] == "IdentifierToken"
                            && matches!(
                                second["kind"].as_str(),
                                Some("EquKeyword" | "EqusKeyword")
                            ) =>
                    {
                        Some((first, 14))
                    }
                    _ => None,
                }
            }
            Some("SectionDirective") => tokens
                .find(|token| token["kind"] == "StringLiteral")
                .map(|token| (token, 2)),
            _ => None,
        };
        if let Some((token, kind)) = selected {
            let raw = token["text"].as_str().unwrap_or("");
            let name = if kind == 2 {
                raw.strip_prefix('"')
                    .and_then(|name| name.strip_suffix('"'))
                    .unwrap_or(raw)
            } else {
                raw
            };
            let (start, length) = span(node);
            let (selection, selection_length) = span(token);
            symbols.push(json!({"name":name,"kind":kind,"range":doc.lines.range(start,length),"selectionRange":doc.lines.range(selection,selection_length)}));
        }
    }
    json!(symbols)
}

pub fn completion(workspace: &Workspace, path: &Path) -> Value {
    let mut items:Vec<_>=crate::instructions::HOVERS.iter().map(|(name,_)|json!({"label":name.to_ascii_lowercase(),"kind":14,"detail":"SM83 instruction"})).collect();
    for name in DIRECTIVES {
        items.push(json!({"label":name,"kind":14,"detail":"Directive"}));
    }
    for name in [
        "a", "b", "c", "d", "e", "h", "l", "af", "bc", "de", "hl", "sp",
    ] {
        items.push(json!({"label":name,"kind":6,"detail":"Register"}));
    }
    if let Some(project) = workspace.project_for(path) {
        for symbol in project.analysis["symbols"].as_array().into_iter().flatten() {
            let kind = if symbol["kind"] == "Constant" { 21 } else { 3 };
            let value = symbol["value"]
                .as_i64()
                .map_or_else(String::new, |value| format!("{value:04X}"));
            let detail = format!("{}: ${value}", symbol["kind"].as_str().unwrap_or(""));
            items.push(json!({"label":symbol["name"],"kind":kind,"detail":detail}));
        }
    }
    json!(items)
}

const DIRECTIVES: &[&str] = &[
    "SECTION",
    "DB",
    "DW",
    "DL",
    "DS",
    "EQU",
    "EQUS",
    "DEF",
    "REDEF",
    "MACRO",
    "ENDM",
    "IF",
    "ELIF",
    "ELSE",
    "ENDC",
    "REPT",
    "FOR",
    "ENDR",
    "INCLUDE",
    "INCBIN",
    "EXPORT",
    "PURGE",
    "OPT",
    "CHARMAP",
    "NEWCHARMAP",
    "SETCHARMAP",
    "PUSHC",
    "POPC",
    "ASSERT",
    "STATIC_ASSERT",
    "WARN",
    "FAIL",
    "PRINT",
    "PRINTLN",
    "PUSHS",
    "POPS",
    "PUSHO",
    "POPO",
    "RSRESET",
    "RSSET",
    "RB",
    "RW",
    "RL",
    "ALIGN",
    "UNION",
    "NEXTU",
    "ENDU",
    "LOAD",
    "ENDL",
];
