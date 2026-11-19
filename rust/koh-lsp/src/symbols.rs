use crate::{
    paths::{key, same_file},
    workspace::Workspace,
};
use serde_json::{Value, json};
use std::{collections::BTreeSet, io, path::Path};

pub struct Resolved {
    pub symbol: Value,
    pub token: Value,
    pub path: String,
    pub declaration: bool,
}

pub struct Occurrence {
    pub path: String,
    pub token: Value,
    pub declaration: bool,
}

pub fn span(value: &Value) -> (usize, usize) {
    (
        value["start"].as_u64().unwrap_or(0) as usize,
        value["length"].as_u64().unwrap_or(0) as usize,
    )
}

pub fn token_at(syntax: &Value, offset: usize) -> Option<&Value> {
    let nodes = syntax["nodes"].as_array()?;
    let (_, length) = span(nodes.first()?);
    if offset >= length {
        return None;
    }
    let tokens = syntax["tokens"].as_array()?;
    tokens
        .iter()
        .find(|token| {
            let (start, length) = span(token);
            !matches!(
                token["kind"].as_str(),
                Some("WhitespaceTrivia" | "NewlineTrivia")
            ) && start <= offset
                && offset < start.saturating_add(length)
        })
        .or_else(|| {
            tokens.iter().find(|token| {
                let kind = token["kind"].as_str().unwrap_or("");
                !kind.ends_with("Trivia") && span(token).0 >= offset && span(token).0 < length
            })
        })
}

pub fn parent<'a>(syntax: &'a Value, token: &Value) -> Option<&'a Value> {
    syntax["nodes"]
        .as_array()?
        .get(token["parent"].as_u64()? as usize)
}

pub fn child_tokens<'a>(syntax: &'a Value, node: &'a Value) -> impl Iterator<Item = &'a Value> {
    node["children"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(|child| child["token"].as_u64())
        .filter_map(|id| syntax["tokens"].as_array()?.get(id as usize))
        .filter(|token| !token["kind"].as_str().unwrap_or("").ends_with("Trivia"))
}

fn declaration(syntax: &Value, token: &Value) -> bool {
    let Some(node) = parent(syntax, token) else {
        return false;
    };
    match node["kind"].as_str() {
        Some("LabelDeclaration" | "MacroDefinition") => true,
        Some("SymbolDirective") => !child_tokens(syntax, node)
            .next()
            .is_some_and(|token| token["kind"] == "ExportKeyword"),
        _ => false,
    }
}

fn symbol_token(syntax: &Value, token: &Value) -> bool {
    matches!(
        token["kind"].as_str(),
        Some("IdentifierToken" | "LocalLabelToken")
    ) && parent(syntax, token).is_some_and(|node| {
        matches!(
            node["kind"].as_str(),
            Some(
                "LabelDeclaration"
                    | "SymbolDirective"
                    | "MacroDefinition"
                    | "NameExpression"
                    | "LabelOperand"
                    | "MacroCall"
            )
        )
    })
}

pub fn resolve_at(workspace: &Workspace, path: &Path, offset: usize) -> Option<Resolved> {
    let doc = workspace.document(path)?;
    let token = token_at(&doc.syntax, offset)?;
    if !symbol_token(&doc.syntax, token) {
        return None;
    }
    let project = workspace.project_for(path)?;
    let symbol = project.analysis["symbols"]
        .as_array()?
        .iter()
        .find(|symbol| {
            symbol["references"]
                .as_array()
                .into_iter()
                .flatten()
                .chain(std::iter::once(&symbol["definition"]))
                .any(|location| {
                    location["path"]
                        .as_str()
                        .is_some_and(|p| same_file(p, &doc.path))
                        && span(location).0 == span(token).0
                })
        })?;
    Some(Resolved {
        symbol: symbol.clone(),
        token: token.clone(),
        path: doc.path.clone(),
        declaration: declaration(&doc.syntax, token),
    })
}

pub fn occurrences(
    workspace: &Workspace,
    target: &Resolved,
    include_declarations: bool,
) -> Vec<Occurrence> {
    let mut found = Vec::new();
    let mut seen = BTreeSet::new();
    for doc in workspace.open_documents() {
        let Some(project) = workspace.project_for(Path::new(&doc.path)) else {
            continue;
        };
        let Some(symbol) = project.analysis["symbols"]
            .as_array()
            .into_iter()
            .flatten()
            .find(|s| s["id"] == target.symbol["id"])
        else {
            continue;
        };
        for location in symbol["references"]
            .as_array()
            .into_iter()
            .flatten()
            .chain(std::iter::once(&symbol["definition"]))
        {
            if !location["path"]
                .as_str()
                .is_some_and(|p| same_file(p, &doc.path))
            {
                continue;
            }
            let Some(token) = token_at(&doc.syntax, span(location).0) else {
                continue;
            };
            if span(token).0 != span(location).0 || !symbol_token(&doc.syntax, token) {
                continue;
            }
            let declaration = declaration(&doc.syntax, token);
            if (!declaration || include_declarations)
                && seen.insert((key(&doc.path), span(token).0))
            {
                found.push(Occurrence {
                    path: doc.path.clone(),
                    token: token.clone(),
                    declaration,
                });
            }
        }
    }
    found.sort_by_key(|item| (key(&item.path), span(&item.token).0));
    found
}

pub fn validate_rename(
    workspace: &mut Workspace,
    target: &Resolved,
    name: &str,
) -> io::Result<Option<String>> {
    let original = target.symbol["name"].as_str().unwrap_or("");
    let local = original.contains('.');
    let mut chars = name.strip_prefix('.').unwrap_or(name).chars();
    if name.trim().is_empty()
        || chars.next().is_none_or(|c| !c.is_alphabetic() && c != '_')
        || chars.any(|c| !c.is_alphanumeric() && c != '_' && c != '#')
    {
        return Ok(Some("Invalid identifier".into()));
    }
    if local != name.starts_with('.') {
        return Ok(Some("Cannot change local/global symbol form".into()));
    }
    let parsed=workspace.request(&json!({"version":1,"op":"parse","sources":[{"path":"<rename>","text":name}],"roots":["<rename>"]}))?;
    if parsed["documents"][0]["tokens"][0]["kind"]
        .as_str()
        .is_some_and(|kind| kind.ends_with("Keyword"))
        || matches!(
            name.to_ascii_lowercase().as_str(),
            "a" | "b" | "c" | "d" | "e" | "f" | "h" | "l" | "af" | "bc" | "de" | "hl" | "sp"
        )
    {
        return Ok(Some("Reserved keyword or register".into()));
    }
    let qualified = if local {
        format!("{}{name}", original.split('.').next().unwrap_or(""))
    } else {
        name.into()
    };
    for project in workspace.projects() {
        for existing in project.analysis["symbols"].as_array().into_iter().flatten() {
            if existing["name"] == qualified
                && existing["id"] != target.symbol["id"]
                && (existing["owner"] == target.symbol["owner"]
                    || existing["visibility"] == "Exported"
                    || target.symbol["visibility"] == "Exported")
            {
                return Ok(Some(
                    "A symbol with that name already exists in the same scope".into(),
                ));
            }
        }
    }
    Ok(None)
}
