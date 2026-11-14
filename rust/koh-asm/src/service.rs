use crate::{
    analysis::{self, Source},
    assembler,
    model::{SymbolBinding, SymbolDefinition},
};
use serde_json::{Value, json};
use std::io::{self, BufRead, Write};

fn error(path: &str, message: impl Into<String>) -> Value {
    json!({"version":1,"ok":false,"documents":[],"symbols":[],"data":[],"diagnostics":[{"path":path,"start":0,"length":0,"severity":"Error","message":message.into()}]})
}

pub fn process(line: &str) -> Value {
    let request: Value = match serde_json::from_str(line) {
        Ok(value) => value,
        Err(e) => return error("", format!("invalid native request: {e}")),
    };
    if request.get("version").and_then(Value::as_u64) != Some(1) {
        return error("", "unsupported native protocol version");
    }
    let op = match request.get("op").and_then(Value::as_str) {
        Some(op @ ("parse" | "analyze" | "assemble")) => op,
        _ => return error("", "unsupported native operation"),
    };
    let allow = match request.get("allowUndefined") {
        None => false,
        Some(Value::Bool(value)) => *value,
        Some(_) => return error("", "allowUndefined must be a boolean"),
    };
    let format = match request.get("format") {
        None => "kobj",
        Some(Value::String(value)) if matches!(value.as_str(), "kobj" | "rgbds" | "o") => value,
        Some(_) => return error("", "format must be kobj, rgbds, or o"),
    };
    let binary_files: Vec<BinarySource> = match request.get("binaryFiles") {
        None => Vec::new(),
        Some(files) => match serde_json::from_value(files.clone()) {
            Ok(files) => files,
            Err(e) => return error("", format!("invalid binary overlay: {e}")),
        },
    };
    let sources: Vec<Source> =
        match serde_json::from_value(request.get("sources").cloned().unwrap_or(Value::Null)) {
            Ok(sources) => sources,
            Err(e) => return error("", format!("invalid source overlay: {e}")),
        };
    if sources.is_empty() {
        return error("", "at least one source is required");
    }
    let roots: Vec<String> = match request.get("roots") {
        Some(value) => match serde_json::from_value(value.clone()) {
            Ok(roots) => roots,
            Err(e) => return error("", format!("invalid roots: {e}")),
        },
        None => vec![sources[0].path.clone()],
    };
    if roots.is_empty() {
        return error("", "at least one root is required");
    }
    for root in &roots {
        if !sources.iter().any(|source| source.path == *root) {
            return error(root, "root is absent from source overlay");
        }
    }
    let mut analysis = if op == "parse" {
        analysis::parse(&sources)
    } else {
        analysis::analyze(&sources, &roots)
    };
    let mut data = Vec::new();
    if op != "parse" {
        let overlays: Vec<_> = sources
            .iter()
            .map(|source| (source.path.clone(), source.text.clone()))
            .collect();
        let binary_overlays: Vec<_> = binary_files
            .into_iter()
            .map(|file| (file.path, file.data))
            .collect();
        let units: Vec<_> = if op == "assemble" && roots.len() > 1 {
            let text = roots
                .iter()
                .map(|root| format!("INCLUDE {root:?}\n"))
                .collect();
            vec![("<compilation>".to_string(), text)]
        } else {
            roots
                .iter()
                .map(|root| {
                    let source = sources.iter().find(|source| source.path == *root).unwrap();
                    (root.clone(), source.text.clone())
                })
                .collect()
        };
        for (path, text) in units {
            match assembler::assemble_with_overlays(
                &text,
                &path,
                allow || (op == "analyze" && roots.len() > 1),
                &overlays,
                &binary_overlays,
            ) {
                Ok(object) => {
                    for symbol in &object.symbols {
                        for candidate in &mut analysis.symbols {
                            if candidate.name != symbol.name {
                                continue;
                            }
                            if roots.len() > 1 && candidate.owner != path {
                                continue;
                            }
                            candidate.visibility = if symbol.binding == SymbolBinding::Exported {
                                "Exported"
                            } else {
                                "Local"
                            }
                            .into();
                            match symbol.definition {
                                SymbolDefinition::Absolute(value) => {
                                    candidate.value = Some(value);
                                    candidate.section = None;
                                }
                                SymbolDefinition::Label { section, offset } => {
                                    candidate.value = Some(offset as i32);
                                    candidate.section =
                                        Some(object.sections[section.0].name.clone());
                                }
                                SymbolDefinition::Import => {
                                    candidate.value = None;
                                    candidate.visibility = "Imported".into();
                                }
                            }
                        }
                    }
                    if op == "assemble" {
                        let result = match format {
                            "kobj" => crate::object::write_kobj(&object),
                            "rgbds" | "o" => crate::object::write_rgbds(&object),
                            format => Err(format!("unsupported object format '{format}'")),
                        };
                        match result {
                            Ok(bytes) => data = bytes,
                            Err(message) => analysis.diagnostics.push(analysis::Diagnostic {
                                path: path.clone(),
                                start: 0,
                                length: 0,
                                severity: "Error".into(),
                                message,
                            }),
                        }
                    }
                }
                Err(errors) => {
                    for diagnostic in errors {
                        let source = sources.iter().find(|source| {
                            assembler::source_path(&source.path)
                                == assembler::source_path(&diagnostic.file)
                        });
                        let text = source
                            .map(|source| source.text.clone())
                            .or_else(|| {
                                std::fs::read_to_string(assembler::source_path(&diagnostic.file))
                                    .ok()
                            })
                            .unwrap_or_default();
                        let start = text
                            .split_inclusive('\n')
                            .take(diagnostic.line.saturating_sub(1) as usize)
                            .map(|line| line.encode_utf16().count())
                            .sum();
                        let path = source
                            .map(|source| source.path.clone())
                            .unwrap_or(diagnostic.file);
                        if !analysis.diagnostics.iter().any(|d| {
                            d.path == path && d.start == start && d.message == diagnostic.message
                        }) {
                            analysis.diagnostics.push(analysis::Diagnostic {
                                path,
                                start,
                                length: 0,
                                severity: "Error".into(),
                                message: diagnostic.message,
                            });
                        }
                    }
                }
            }
        }
    }
    let ok = !analysis
        .diagnostics
        .iter()
        .any(|diagnostic| diagnostic.severity == "Error");
    let mut response =
        serde_json::to_value(analysis).expect("analysis contains serializable values");
    response["version"] = json!(1);
    response["ok"] = json!(ok);
    response["data"] = json!(data);
    response
}

#[derive(serde::Deserialize)]
struct BinarySource {
    path: String,
    data: Vec<u8>,
}

pub fn serve(reader: impl BufRead, mut writer: impl Write) -> io::Result<()> {
    for line in reader.lines() {
        let line = line?;
        let response = process(&line);
        serde_json::to_writer(&mut writer, &response)?;
        writer.write_all(b"\n")?;
        writer.flush()?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn rejects_unknown_protocol_without_losing_framing() {
        let mut out = Vec::new();
        serve(io::Cursor::new("{}\nnot-json\n"), &mut out).unwrap();
        let replies: Vec<Value> = String::from_utf8(out)
            .unwrap()
            .lines()
            .map(|line| serde_json::from_str(line).unwrap())
            .collect();
        assert_eq!(replies.len(), 2);
        assert!(replies.iter().all(|reply| reply["ok"] == false));
    }
    #[test]
    fn malformed_optional_fields_are_diagnostics() {
        for (field, value) in [
            ("allowUndefined", json!("true")),
            ("format", json!(42)),
            ("format", json!("unknown")),
            ("binaryFiles", json!(false)),
        ] {
            let mut request = json!({"version":1,"op":"assemble","sources":[{"path":"main.asm","text":"SECTION \"code\",ROM0\nnop"}]});
            request[field] = value;
            let response = process(&request.to_string());
            assert_eq!(response["ok"], false, "{response}");
            assert_eq!(response["data"], json!([]));
        }
    }
    #[test]
    fn assembles_virtual_include_and_binary_overlay() {
        let request = json!({"version":1,"op":"assemble","sources":[{"path":"/virtual/main.asm","text":"SECTION \"code\",ROM0[0]\nINCLUDE \"data.inc\"\nINCBIN \"asset.bin\""},{"path":"/virtual/data.inc","text":"db 7"}],"roots":["/virtual/main.asm"],"binaryFiles":[{"path":"/virtual/asset.bin","data":[8,9]}]});
        let response = process(&request.to_string());
        assert_eq!(response["ok"], true, "{response}");
        assert_eq!(
            &response["data"].as_array().unwrap()[..4],
            json!([75, 79, 72, 0]).as_array().unwrap()
        );
    }
    #[test]
    fn text_and_binary_snapshots_are_distinct() {
        let request = json!({"version":1,"op":"assemble","sources":[{"path":"/virtual/main.asm","text":"SECTION \"code\",ROM0[0]\nINCLUDE \"shared.inc\"\nINCBIN \"shared.inc\""},{"path":"/virtual/shared.inc","text":"db 7"}],"roots":["/virtual/main.asm"],"binaryFiles":[{"path":"/virtual/shared.inc","data":[8,9]}]});
        let response = process(&request.to_string());
        assert_eq!(response["ok"], true, "{response}");
        let bytes: Vec<u8> = serde_json::from_value(response["data"].clone()).unwrap();
        assert!(bytes.windows(3).any(|bytes| bytes == [7, 8, 9]));
    }
}
