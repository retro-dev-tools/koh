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
        analysis
            .diagnostics
            .retain(|diagnostic| !diagnostic.provisional);
        let mut canonical = Vec::new();
        for (path, text) in units {
            let report = assembler::assemble_with_overlays_report(
                &text,
                &path,
                allow || (op == "analyze" && roots.len() > 1),
                &overlays,
                &binary_overlays,
            );
            for warning in report.warnings {
                append_diagnostic(&mut analysis, &sources, warning, "Warning");
            }
            match report.result {
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
                                provisional: false,
                                path: path.clone(),
                                start: 0,
                                length: 0,
                                severity: "Error".into(),
                                message,
                            }),
                        }
                    }
                    canonical.push((path.clone(), object));
                }
                Err(errors) => {
                    for diagnostic in errors {
                        append_diagnostic(&mut analysis, &sources, diagnostic, "Error");
                    }
                }
            }
        }
        if !allow {
            let exports: std::collections::HashSet<_> = canonical
                .iter()
                .flat_map(|(_, object)| &object.symbols)
                .filter(|symbol| {
                    symbol.binding == SymbolBinding::Exported
                        && symbol.definition != SymbolDefinition::Import
                })
                .map(|symbol| symbol.name.as_str())
                .collect();
            for (path, object) in &canonical {
                for (id, symbol) in object.symbols.iter().enumerate() {
                    if symbol.definition != SymbolDefinition::Import
                        || exports.contains(symbol.name.as_str())
                    {
                        continue;
                    }
                    let source = symbol.source.as_ref().or_else(|| {
                        object
                            .sections
                            .iter()
                            .flat_map(|section| &section.patches)
                            .find(|patch| {
                                references_import(&patch.expression, crate::model::SymbolId(id))
                            })
                            .map(|patch| &patch.source)
                    });
                    append_diagnostic(
                        &mut analysis,
                        &sources,
                        assembler::Diagnostic {
                            file: source
                                .map_or(path.as_str(), |source| source.file.as_str())
                                .into(),
                            line: source.map_or(1, |source| source.line),
                            message: format!("undefined symbol '{}'", symbol.name),
                        },
                        "Error",
                    );
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

fn append_diagnostic(
    analysis: &mut analysis::Analysis,
    sources: &[Source],
    diagnostic: assembler::Diagnostic,
    severity: &str,
) {
    let source = sources.iter().find(|source| {
        assembler::source_path(&source.path) == assembler::source_path(&diagnostic.file)
    });
    let text = source
        .map(|source| source.text.clone())
        .or_else(|| std::fs::read_to_string(assembler::source_path(&diagnostic.file)).ok())
        .unwrap_or_default();
    let start = text
        .split_inclusive('\n')
        .take(diagnostic.line.saturating_sub(1) as usize)
        .map(|line| line.encode_utf16().count())
        .sum();
    let path = source
        .map(|source| source.path.clone())
        .unwrap_or(diagnostic.file);
    if !analysis.diagnostics.iter().any(|existing| {
        existing.path == path
            && existing.start == start
            && existing.severity == severity
            && existing.message == diagnostic.message
    }) {
        analysis.diagnostics.push(analysis::Diagnostic {
            provisional: false,
            path,
            start,
            length: 0,
            severity: severity.into(),
            message: diagnostic.message,
        });
    }
}

fn references_import(
    expression: &crate::model::RelocationExpr,
    symbol: crate::model::SymbolId,
) -> bool {
    use crate::model::RelocationExpr as R;
    match expression {
        R::Symbol(id) | R::Bank(id) => *id == symbol,
        R::Unary(_, value) => references_import(value, symbol),
        R::Binary(_, left, right) => {
            references_import(left, symbol) || references_import(right, symbol)
        }
        _ => false,
    }
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
    fn canonical_assembly_controls_inactive_and_macro_body_diagnostics() {
        for (source, expected) in [
            ("SECTION \"code\",ROM0\nIF 0\nld a,\nENDC\nnop\n", vec![0]),
            (
                "MACRO unused\nld a,\nENDM\nSECTION \"code\",ROM0\nnop\n",
                vec![0],
            ),
            (
                "MACRO unused\ndb \"unclosed\nENDM\nSECTION \"code\",ROM0\nnop\n",
                vec![0],
            ),
            (
                "SECTION \"code\",ROM0\ndb \"hello\\ ; comment\nworld\"\n",
                b"helloworld".to_vec(),
            ),
        ] {
            let canonical = assembler::assemble(source, "main.asm", false).unwrap();
            assert_eq!(canonical.sections.len(), 1);
            assert_eq!(canonical.sections[0].data, expected);
            let bytes = crate::object::write_kobj(&canonical).unwrap();
            for op in ["analyze", "assemble"] {
                let response = process(
                    &json!({"version":1,"op":op,"sources":[{"path":"main.asm","text":source}]})
                        .to_string(),
                );
                assert_eq!(response["ok"], true, "{source}\n{response}");
                assert_eq!(response["diagnostics"], json!([]), "{response}");
                if op == "assemble" {
                    assert_eq!(response["data"], json!(bytes));
                }
            }
        }
        for op in ["parse", "analyze", "assemble"] {
            let response=process(&json!({"version":1,"op":op,"sources":[{"path":"bad.asm","text":"SECTION \"code\",ROM0\nld a,"}]}).to_string());
            assert_eq!(response["ok"], false, "{response}");
        }
    }

    #[test]
    fn multi_root_analysis_resolves_only_canonical_exports() {
        let reference = "SECTION \"a\",ROM0\njp Target\n";
        for (definition, expected) in [
            ("SECTION \"b\",ROM0\nTarget:: nop", true),
            ("SECTION \"b\",ROM0\nTarget: nop", false),
            ("SECTION \"b\",ROM0\nnop", false),
            ("SECTION \"b\",ROM0\nIF 0\nTarget:: nop\nENDC\nnop", false),
        ] {
            for allow in [false, true] {
                let response=process(&json!({"version":1,"op":"analyze","allowUndefined":allow,"sources":[{"path":"a.asm","text":reference},{"path":"b.asm","text":definition}],"roots":["a.asm","b.asm"]}).to_string());
                assert_eq!(
                    response["ok"],
                    expected || allow,
                    "{definition}\n{response}"
                );
                if !expected && !allow {
                    let diagnostics = response["diagnostics"].as_array().unwrap();
                    assert!(
                        diagnostics.iter().any(|diagnostic| diagnostic["message"]
                            == "undefined symbol 'Target'"
                            && diagnostic["path"] == "a.asm"
                            && diagnostic["start"] == reference.find("jp").unwrap()),
                        "{response}"
                    );
                }
            }
        }
        let response=process(&json!({"version":1,"op":"analyze","sources":[{"path":"a.asm","text":"SECTION \"a\",ROM0\nTarget: nop\njp Target"},{"path":"b.asm","text":"SECTION \"b\",ROM0\nTarget:: nop"}],"roots":["a.asm","b.asm"]}).to_string());
        assert_eq!(response["ok"], true, "{response}");
    }
    #[test]
    fn warnings_preserve_messages_and_macro_source_without_failing_requests() {
        let included = "MACRO report\nWARN \"macro \" ++ \"warning\"\nENDM\nreport\nASSERT WARN,0,\"optional\"\nASSERT WARN,1,\"not emitted\"\nASSERT WARN,0\nASSERT WARN,0,\"\"\n";
        for op in ["analyze", "assemble"] {
            let request = json!({"version":1,"op":op,"sources":[{"path":"/virtual/main.asm","text":"INCLUDE \"warn.inc\"\nSECTION \"code\",ROM0\nnop"},{"path":"/virtual/warn.inc","text":included}],"roots":["/virtual/main.asm"]});
            let response = process(&request.to_string());
            assert_eq!(response["ok"], true, "{response}");
            let warnings: Vec<_> = response["diagnostics"]
                .as_array()
                .unwrap()
                .iter()
                .filter(|diagnostic| diagnostic["severity"] == "Warning")
                .collect();
            assert_eq!(warnings.len(), 4, "{response}");
            assert_eq!(warnings[0]["message"], "macro warning");
            assert_eq!(warnings[0]["path"], "/virtual/warn.inc");
            assert_eq!(warnings[0]["start"], included.find("WARN").unwrap());
            assert_eq!(warnings[1]["message"], "Assertion failed: optional");
            assert_eq!(warnings[2]["message"], "Assertion failed");
            assert_eq!(warnings[3]["message"], "Assertion failed");
        }
    }

    #[test]
    fn warning_diagnostics_survive_assembly_errors() {
        let request = json!({"version":1,"op":"assemble","sources":[{"path":"main.asm","text":"WARN \"retained\"\nSECTION \"code\",ROM0\nld a,256"}]});
        let response = process(&request.to_string());
        assert_eq!(response["ok"], false, "{response}");
        let diagnostics = response["diagnostics"].as_array().unwrap();
        assert!(
            diagnostics
                .iter()
                .any(|diagnostic| diagnostic["severity"] == "Warning"
                    && diagnostic["message"] == "retained")
        );
        assert!(
            diagnostics
                .iter()
                .any(|diagnostic| diagnostic["severity"] == "Error")
        );
        assert_eq!(response["data"], json!([]));
    }
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
