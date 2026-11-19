use crate::{
    paths::{self, invalid, key, same_file},
    symbols::{self, span},
    workspace::Workspace,
};
use serde_json::{Value, json};
use std::{
    collections::BTreeMap,
    io,
    path::{Path, PathBuf},
};

#[derive(Default)]
pub struct Server {
    workspace: Workspace,
    uris: BTreeMap<String, String>,
    shutdown: bool,
    pub exit_code: Option<i32>,
}

impl Server {
    fn path_for_uri(&self, uri: &str) -> io::Result<PathBuf> {
        if let Some((key, _)) = self
            .uris
            .iter()
            .find(|(_, existing)| existing.as_str() == uri)
            && let Some(doc) = self.workspace.document(Path::new(key))
        {
            return Ok(PathBuf::from(&doc.path));
        }
        if uri
            .split_once(':')
            .is_some_and(|(scheme, _)| scheme.eq_ignore_ascii_case("untitled"))
        {
            use std::hash::{Hash, Hasher};
            url::Url::parse(uri).map_err(|_| invalid("invalid untitled URI"))?;
            let mut hash = std::collections::hash_map::DefaultHasher::new();
            uri.hash(&mut hash);
            let root = self
                .workspace
                .folder()
                .map_or_else(std::env::current_dir, |folder| Ok(folder.to_path_buf()))?;
            return Ok(root.join(format!(".__koh_untitled_{:016x}.asm", hash.finish())));
        }
        paths::from_uri(uri)
    }

    fn document_path(&self, params: &Value) -> io::Result<PathBuf> {
        self.path_for_uri(required(&params["textDocument"], "uri")?)
    }

    pub fn completion_code(&self) -> i32 {
        self.exit_code.unwrap_or(if self.shutdown { 0 } else { 1 })
    }

    pub fn handle(&mut self, message: &Value) -> Vec<Value> {
        let id = message.get("id");
        if message["jsonrpc"] != "2.0"
            || !message["method"].is_string()
            || id.is_some_and(|id| !id.is_null() && !id.is_string() && !id.is_number())
        {
            return vec![
                json!({"jsonrpc":"2.0","id":null,"error":{"code":-32600,"message":"Invalid Request"}}),
            ];
        }
        let method = message["method"].as_str().unwrap();
        if method == "exit" {
            self.exit_code = Some(if self.shutdown { 0 } else { 1 });
            return Vec::new();
        }
        let mut output = Vec::new();
        let result = self.route(method, &message["params"], &mut output);
        match (id,result) {
            (Some(id),Ok(result))=>output.push(json!({"jsonrpc":"2.0","id":id,"result":result})),
            (Some(id),Err(error))=> {
                let code=match error.kind() {io::ErrorKind::InvalidInput=>-32602,io::ErrorKind::Unsupported=>-32601,_=>-32603};
                output.push(json!({"jsonrpc":"2.0","id":id,"error":{"code":code,"message":error.to_string()}}));
            }
            (None,Err(error)) if error.kind()!=io::ErrorKind::Unsupported=>output.push(json!({"jsonrpc":"2.0","method":"window/logMessage","params":{"type":1,"message":error.to_string()}})),
            _=>{},
        }
        output
    }

    fn route(
        &mut self,
        method: &str,
        params: &Value,
        output: &mut Vec<Value>,
    ) -> io::Result<Value> {
        match method {
            "initialize" => {
                if let Some(root) = params["rootUri"].as_str() {
                    self.workspace.initialize(&paths::from_uri(root)?)?;
                }
                Ok(json!({"capabilities":{
                    "textDocumentSync":{"openClose":true,"change":1,"save":{"includeText":false}},
                    "hoverProvider":true,"definitionProvider":true,"referencesProvider":true,
                    "renameProvider":{"prepareProvider":true},
                    "documentSymbolProvider":true,
                    "completionProvider":{"triggerCharacters":["."],"resolveProvider":false},
                    "semanticTokensProvider":{"full":true,"legend":{"tokenTypes":crate::tokens::TOKEN_TYPES,"tokenModifiers":crate::tokens::TOKEN_MODIFIERS}},
                    "inlayHintProvider":true,
                    "signatureHelpProvider":{"triggerCharacters":[","]}
                }}))
            }
            "initialized" | "$/cancelRequest" | "$/setTrace" => Ok(Value::Null),
            "shutdown" => {
                self.shutdown = true;
                Ok(Value::Null)
            }
            "textDocument/didOpen" => {
                let uri = required(&params["textDocument"], "uri")?;
                let text = required(&params["textDocument"], "text")?;
                let path = self.path_for_uri(uri)?;
                self.uris.insert(key(&path.to_string_lossy()), uri.into());
                self.workspace.open(&path, text)?;
                self.publish(output)?;
                Ok(Value::Null)
            }
            "textDocument/didChange" => {
                let path = self.document_path(params)?;
                let changes = params["contentChanges"]
                    .as_array()
                    .ok_or_else(|| invalid("missing contentChanges"))?;
                if let Some(change) = changes.last() {
                    if change.get("range").is_some() {
                        return Err(invalid("full document synchronization required"));
                    }
                    self.workspace.open(&path, required(change, "text")?)?;
                }
                self.publish(output)?;
                Ok(Value::Null)
            }
            "textDocument/didClose" => {
                let path = self.document_path(params)?;
                self.workspace.close(&path)?;
                output.push(json!({"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":self.uri(&path)?,"diagnostics":[]}}));
                self.uris.remove(&key(&path.to_string_lossy()));
                self.publish(output)?;
                Ok(Value::Null)
            }
            "textDocument/didSave" => {
                let path = self.document_path(params)?;
                if path
                    .file_name()
                    .is_some_and(|name| name.to_string_lossy().eq_ignore_ascii_case("koh.yaml"))
                {
                    self.workspace.reload()?;
                }
                self.publish(output)?;
                Ok(Value::Null)
            }
            "textDocument/hover" => self.hover(params),
            "textDocument/definition" => self.definition(params),
            "textDocument/documentSymbol" => Ok(crate::features::document_symbols(
                &self.workspace,
                &self.document_path(params)?,
            )),
            "textDocument/completion" => Ok(crate::features::completion(
                &self.workspace,
                &self.document_path(params)?,
            )),
            "textDocument/semanticTokens/full" => {
                let path = self.document_path(params)?;
                Ok(self.workspace.document(&path).map_or(
                    Value::Null,
                    |doc| json!({"data":crate::tokens::encode(&doc.text,&doc.syntax)}),
                ))
            }
            "textDocument/inlayHint" => {
                let path = self.document_path(params)?;
                for point in ["start", "end"] {
                    if params["range"][point]["line"].as_u64().is_none()
                        || params["range"][point]["character"].as_u64().is_none()
                    {
                        return Err(invalid("missing or invalid inlay range"));
                    }
                }
                Ok(crate::features::inlay_hints(
                    &self.workspace,
                    &path,
                    &params["range"],
                ))
            }
            "textDocument/signatureHelp" => Ok(crate::features::signature(
                &self.workspace,
                &self.document_path(params)?,
                cursor(params)?,
            )),
            "textDocument/references" => {
                let path = self.document_path(params)?;
                let Some(target) = self.resolve(params, &path)? else {
                    return Ok(json!([]));
                };
                let include = params["context"]["includeDeclaration"]
                    .as_bool()
                    .unwrap_or(false);
                let mut locations = Vec::new();
                for item in symbols::occurrences(&self.workspace, &target, include) {
                    let path = Path::new(&item.path);
                    locations.push(
                        json!({"uri":self.uri(path)?,"range":self.range(path,span(&item.token))?}),
                    );
                }
                Ok(json!(locations))
            }
            "textDocument/prepareRename" => {
                let path = self.document_path(params)?;
                let Some(target) = self.resolve(params, &path)? else {
                    return Ok(Value::Null);
                };
                Ok(
                    json!({"range":self.range(&path,span(&target.token))?,"placeholder":target.token["text"]}),
                )
            }
            "textDocument/rename" => {
                let path = self.document_path(params)?;
                let name = required(params, "newName")?;
                let Some(target) = self.resolve(params, &path)? else {
                    return Ok(Value::Null);
                };
                if symbols::validate_rename(&mut self.workspace, &target, name)?.is_some() {
                    return Ok(Value::Null);
                }
                let mut changes: BTreeMap<String, Vec<Value>> = BTreeMap::new();
                for item in symbols::occurrences(&self.workspace, &target, true) {
                    let path = Path::new(&item.path);
                    let range = self.range(path, span(&item.token))?;
                    changes
                        .entry(self.uri(path)?)
                        .or_default()
                        .push(json!({"range":range,"newText":name}));
                }
                if changes.is_empty() {
                    Ok(Value::Null)
                } else {
                    Ok(json!({"changes":changes}))
                }
            }
            _ => Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "Method not found",
            )),
        }
    }

    fn uri(&self, path: &Path) -> io::Result<String> {
        self.uris
            .get(&key(&path.to_string_lossy()))
            .cloned()
            .map_or_else(|| paths::to_uri(path), Ok)
    }

    fn range(&self, path: &Path, (start, length): (usize, usize)) -> io::Result<Value> {
        Ok(match self.workspace.document(path) {
            Some(doc) => doc.lines.range(start, length),
            None => paths::range(&std::fs::read_to_string(path)?, start, length),
        })
    }

    fn publish(&self, output: &mut Vec<Value>) -> io::Result<()> {
        for doc in self.workspace.open_documents() {
            let diagnostics: Vec<_> = self
                .workspace
                .diagnostics(Path::new(&doc.path))
                .iter()
                .map(|d| diagnostic(&doc.lines, d))
                .collect();
            output.push(json!({"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":self.uri(Path::new(&doc.path))?,"diagnostics":diagnostics}}));
        }
        Ok(())
    }

    fn resolve(&self, params: &Value, path: &Path) -> io::Result<Option<symbols::Resolved>> {
        let position = cursor(params)?;
        Ok(self
            .workspace
            .document(path)
            .and_then(|doc| symbols::resolve_at(&self.workspace, path, doc.lines.offset(position))))
    }

    fn hover(&mut self, params: &Value) -> io::Result<Value> {
        let path = self.document_path(params)?;
        let position = cursor(params)?;
        let Some(doc) = self.workspace.document(&path) else {
            return Ok(Value::Null);
        };
        let offset = doc.lines.offset(position);
        let Some(token) = symbols::token_at(&doc.syntax, offset).cloned() else {
            return Ok(Value::Null);
        };
        let (start, length) = span(&token);
        let range = doc.lines.range(start, length);
        let content = if let Some(target) = symbols::resolve_at(&self.workspace, &path, offset) {
            let symbol = target.symbol;
            let name = symbol["name"].as_str().unwrap_or("");
            let kind = symbol["kind"].as_str().unwrap_or("");
            let value = symbol["value"].as_i64();
            let mut text = format!(
                "**{name}** ({kind})\n- Value: `${}` ({})\n",
                value.map_or_else(String::new, |v| format!("{v:04X}")),
                value.map_or_else(String::new, |v| v.to_string())
            );
            if let Some(section) = symbol["section"].as_str() {
                text.push_str(&format!("- Section: `{section}`\n"));
            }
            Some(text)
        } else if token["kind"] == "NumberLiteral" {
            let expression = token["text"].as_str().unwrap_or("");
            let response=self.workspace.request(&json!({"version":1,"op":"analyze","sources":[{"path":"<hover>","text":format!("DEF __hover_value EQU {expression}")}],"roots":["<hover>"]}))?;
            response["symbols"]
                .as_array()
                .into_iter()
                .flatten()
                .find(|s| s["name"] == "__hover_value")
                .and_then(|symbol| symbol["value"].as_i64())
                .map(|value| format!("`${value:04X}` = {value} = `%{value:b}`"))
        } else if token["kind"]
            .as_str()
            .is_some_and(|kind| kind.ends_with("Keyword"))
        {
            let mnemonic = token["text"].as_str().unwrap_or("").to_ascii_uppercase();
            crate::instructions::HOVERS
                .iter()
                .find(|(name, _)| *name == mnemonic)
                .map(|(_, text)| text.to_string())
        } else {
            None
        };
        Ok(content.map_or(
            Value::Null,
            |content| json!({"contents":{"kind":"markdown","value":content},"range":range}),
        ))
    }

    fn definition(&self, params: &Value) -> io::Result<Value> {
        let path = self.document_path(params)?;
        let Some(target) = self.resolve(params, &path)? else {
            return Ok(Value::Null);
        };
        let location = &target.symbol["definition"];
        let Some(defpath) = location["path"].as_str() else {
            return Ok(Value::Null);
        };
        let syntax = self
            .workspace
            .project_for(&path)
            .and_then(|project| project.analysis["documents"].as_array())
            .and_then(|docs| {
                docs.iter()
                    .find(|doc| doc["path"].as_str().is_some_and(|p| same_file(p, defpath)))
            });
        let site = syntax
            .and_then(|syntax| {
                symbols::token_at(syntax, span(location).0)
                    .and_then(|token| symbols::parent(syntax, token))
            })
            .unwrap_or(location);
        let defpath = Path::new(defpath);
        Ok(json!({"uri":self.uri(defpath)?,"range":self.range(defpath,span(site))?}))
    }
}

pub fn diagnostic(lines: &paths::Lines, diagnostic: &Value) -> Value {
    let (start, length) = span(diagnostic);
    let severity = match diagnostic["severity"].as_str() {
        Some("Error") => 1,
        Some("Warning") => 2,
        _ => 3,
    };
    json!({"range":lines.range(start,length),"severity":severity,"source":"koh","message":diagnostic["message"]})
}

fn required<'a>(value: &'a Value, field: &str) -> io::Result<&'a str> {
    value[field]
        .as_str()
        .ok_or_else(|| invalid(&format!("missing or invalid {field}")))
}

fn cursor(params: &Value) -> io::Result<&Value> {
    let position = &params["position"];
    if position["line"].as_u64().is_none() || position["character"].as_u64().is_none() {
        return Err(invalid("missing or invalid position"));
    }
    Ok(position)
}
