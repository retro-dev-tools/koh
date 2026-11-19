mod common;
use common::Fixture;
use koh_lsp::{
    paths::to_uri,
    server::{Server, diagnostic},
};
use serde_json::{Value, json};

fn request(server: &mut Server, method: &str, params: Value) -> Value {
    let messages = server.handle(&json!({"jsonrpc":"2.0","id":1,"method":method,"params":params}));
    let response = messages.iter().find(|m| m["id"] == 1).unwrap();
    assert!(response.get("error").is_none(), "{response}");
    response["result"].clone()
}

#[test]
fn native_server_sync_navigation_and_rename_use_worker_analysis() {
    let f = Fixture::new();
    let path = f.write("main # 😀.asm", "");
    let uri = to_uri(&path).unwrap();
    let mut server = Server::default();
    let init = request(&mut server, "initialize", json!({"capabilities":{}}));
    assert_eq!(init["capabilities"]["textDocumentSync"]["change"], 1);
    assert_eq!(
        init["capabilities"]["renameProvider"]["prepareProvider"],
        true
    );
    let text = "SECTION \"Main\",ROM0\nTarget: nop\njp Target\ncall Target\n";
    let messages=server.handle(&json!({"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":uri,"text":text,"version":1}}}));
    assert!(
        messages
            .iter()
            .any(|m| m["method"] == "textDocument/publishDiagnostics"
                && m["params"]["diagnostics"] == json!([]))
    );
    let params = json!({"textDocument":{"uri":uri},"position":{"line":2,"character":4}});
    let hover = request(&mut server, "textDocument/hover", params.clone());
    assert_eq!(
        hover["contents"]["value"],
        "**Target** (Label)\n- Value: `$0000` (0)\n- Section: `Main`\n"
    );
    let definition = request(&mut server, "textDocument/definition", params.clone());
    assert_eq!(definition["uri"], uri);
    assert_eq!(
        definition["range"],
        json!({"start":{"line":1,"character":0},"end":{"line":1,"character":7}})
    );
    let mut refs = params.clone();
    refs["context"] = json!({"includeDeclaration":false});
    assert_eq!(
        request(&mut server, "textDocument/references", refs)
            .as_array()
            .unwrap()
            .len(),
        2
    );
    assert_eq!(
        request(&mut server, "textDocument/prepareRename", params.clone())["placeholder"],
        "Target"
    );
    let mut rename = params;
    rename["newName"] = json!("Renamed");
    let edits = request(&mut server, "textDocument/rename", rename.clone());
    assert_eq!(edits["changes"][&uri].as_array().unwrap().len(), 3);
    rename["newName"] = json!("nop");
    assert_eq!(
        request(&mut server, "textDocument/rename", rename),
        Value::Null
    );
    let messages=server.handle(&json!({"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":uri,"version":2},"contentChanges":[{"text":"ld a,\n"}]}}));
    assert!(
        !messages[0]["params"]["diagnostics"]
            .as_array()
            .unwrap()
            .is_empty()
    );
    let messages=server.handle(&json!({"jsonrpc":"2.0","method":"textDocument/didClose","params":{"textDocument":{"uri":uri}}}));
    assert_eq!(messages[0]["params"]["diagnostics"], json!([]));
}

#[test]
fn diagnostics_keep_severity_source_message_and_ranges() {
    for (severity, number) in [("Error", 1), ("Warning", 2), ("Information", 3)] {
        let result = diagnostic(
            "nop\nhalt",
            &json!({"start":4,"length":4,"severity":severity,"message":"test"}),
        );
        assert_eq!(result["severity"], number);
        assert_eq!(result["source"], "koh");
        assert_eq!(result["message"], "test");
        assert_eq!(
            result["range"],
            json!({"start":{"line":1,"character":0},"end":{"line":1,"character":4}})
        );
    }
    assert_eq!(
        diagnostic(
            "nop",
            &json!({"start":0,"length":0,"severity":"Error","message":"global"})
        )["range"],
        json!({"start":{"line":0,"character":0},"end":{"line":0,"character":0}})
    );
}

#[test]
fn hover_preserves_instruction_and_numeric_formats() {
    let fixture = Fixture::new();
    let path = fixture.write("main.asm", "");
    let uri = to_uri(&path).unwrap();
    let mut server = Server::default();
    server.handle(&json!({"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":uri,"text":"SECTION \"Main\",ROM0\nnop\nld a,$2A\n"}}}));
    let hover = request(
        &mut server,
        "textDocument/hover",
        json!({"textDocument":{"uri":uri},"position":{"line":1,"character":1}}),
    );
    assert_eq!(
        hover["contents"]["value"],
        "**NOP** — No operation (1 byte, 4 cycles)"
    );
    let hover = request(
        &mut server,
        "textDocument/hover",
        json!({"textDocument":{"uri":uri},"position":{"line":2,"character":6}}),
    );
    assert_eq!(hover["contents"]["value"], "`$002A` = 42 = `%101010`");
}

#[test]
fn invalid_requests_return_errors_and_notifications_have_no_rpc_response() {
    let mut server = Server::default();
    assert_eq!(
        server.handle(&json!({"jsonrpc":"2.0","id":1,"method":"textDocument/hover","params":{}}))
            [0]["error"]["code"],
        -32602
    );
    assert_eq!(
        server.handle(&json!({"jsonrpc":"2.0","id":2,"method":"unknown"}))[0]["error"]["code"],
        -32601
    );
    assert!(
        server
            .handle(&json!({"jsonrpc":"2.0","method":"initialized"}))
            .is_empty()
    );
    assert_eq!(
        server.handle(&json!({"id":3,"method":"initialize"}))[0]["error"]["code"],
        -32600
    );
}

#[test]
fn inlay_hints_preserve_values_ranges_and_declaration_exclusions() {
    let fixture = Fixture::new();
    let path = fixture.write("hints.asm", "");
    let uri = to_uri(&path).unwrap();
    for (text, expected) in [
        (
            "SECTION \"Main\",ROM0\nMY_CONST EQU 42\nld a,MY_CONST\nld b,MY_CONST",
            vec![" = $002A (42)", " = $002A (42)"],
        ),
        (
            "SECTION \"Main\",ROM0\nMY_CONST EQU 255\nld a,MY_CONST",
            vec![" = $00FF (255)"],
        ),
        (
            "SECTION \"Main\",ROM0\nLabel: nop\njp Label",
            vec![" = $0000"],
        ),
        ("SECTION \"Main\",ROM0\nMY_CONST EQU 42", vec![]),
        ("SECTION \"Main\",ROM0\nLabel: nop", vec![]),
        ("SECTION \"Main\",ROM0\nhalt", vec![]),
        ("SECTION \"Main\",ROM0\nld a,Unknown", vec![]),
        ("MY_STR EQUS \"hello\"", vec![]),
        ("MyMacro: MACRO\nnop\nENDM", vec![]),
        (
            "SECTION \"Main\",ROM0\nMyMacro: MACRO\nnop\nENDM\nMyMacro",
            vec![],
        ),
    ] {
        let mut server = Server::default();
        server.handle(&json!({"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":uri,"text":text}}}));
        let hints = request(
            &mut server,
            "textDocument/inlayHint",
            json!({"textDocument":{"uri":uri},"range":{"start":{"line":0,"character":0},"end":{"line":999,"character":0}}}),
        );
        let labels: Vec<_> = hints
            .as_array()
            .unwrap()
            .iter()
            .map(|hint| hint["label"].as_str().unwrap())
            .collect();
        assert_eq!(labels, expected, "{text}");
        if let Some(hint) = hints.as_array().unwrap().first() {
            assert_eq!(hint["kind"], 1);
            assert_eq!(hint["paddingLeft"], true);
        }
        let excluded = request(
            &mut server,
            "textDocument/inlayHint",
            json!({"textDocument":{"uri":uri},"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":0}}}),
        );
        assert_eq!(excluded, json!([]));
    }
}

#[test]
fn macro_signatures_preserve_arity_active_parameter_and_null_cases() {
    let fixture = Fixture::new();
    let path = fixture.write("macros.asm", "");
    let uri = to_uri(&path).unwrap();
    for (text, line, character, arity, active) in [
        (
            "MyMacro: MACRO\nld a,\\1\nld b,\\2\nENDM\nMyMacro $42,$FF",
            4,
            8,
            Some(2),
            0,
        ),
        (
            "MyMacro: MACRO\nld a,\\1\nld b,\\2\nENDM\nMyMacro $42,$FF",
            4,
            12,
            Some(2),
            1,
        ),
        ("MyMacro: MACRO\nnop\nENDM\nMyMacro", 3, 0, Some(0), 0),
        ("SECTION \"Main\",ROM0\nUndefinedMacro $42", 1, 0, None, 0),
        ("SECTION \"Main\",ROM0\nnop", 1, 0, None, 0),
    ] {
        let mut server = Server::default();
        server.handle(&json!({"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":uri,"text":text}}}));
        let help = request(
            &mut server,
            "textDocument/signatureHelp",
            json!({"textDocument":{"uri":uri},"position":{"line":line,"character":character}}),
        );
        if let Some(arity) = arity {
            assert_eq!(
                help["signatures"][0]["parameters"]
                    .as_array()
                    .unwrap()
                    .len(),
                arity
            );
            assert_eq!(help["activeParameter"], active);
            assert_eq!(help["activeSignature"], 0);
            assert_eq!(help["signatures"][0]["activeParameter"], active);
        } else {
            assert_eq!(help, Value::Null);
        }
    }
}

#[test]
fn document_symbols_completion_and_semantic_tokens_are_served() {
    let fixture = Fixture::new();
    let path = fixture.write("outline.asm", "");
    let uri = to_uri(&path).unwrap();
    let mut server = Server::default();
    server.handle(&json!({"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":uri,"text":"SECTION \"Main\",ROM0\nMY_CONST EQU 42\nLabel: nop\nld a,MY_CONST\n"}}}));
    let params = json!({"textDocument":{"uri":uri}});
    let outline = request(&mut server, "textDocument/documentSymbol", params.clone());
    let names: Vec<_> = outline
        .as_array()
        .unwrap()
        .iter()
        .map(|s| (s["name"].as_str().unwrap(), s["kind"].as_u64().unwrap()))
        .collect();
    assert_eq!(names, vec![("Main", 2), ("MY_CONST", 14), ("Label", 12)]);
    let completions = request(&mut server, "textDocument/completion", params.clone());
    for name in ["nop", "SECTION", "hl", "MY_CONST", "Label"] {
        assert!(
            completions
                .as_array()
                .unwrap()
                .iter()
                .any(|s| s["label"] == name),
            "{name}"
        );
    }
    let tokens = request(&mut server, "textDocument/semanticTokens/full", params);
    assert!(!tokens["data"].as_array().unwrap().is_empty());
    let capabilities =
        request(&mut server, "initialize", json!({"capabilities":{}}))["capabilities"].clone();
    assert_eq!(capabilities["semanticTokensProvider"]["full"], true);
    assert_eq!(
        capabilities["semanticTokensProvider"]["legend"]["tokenTypes"]
            .as_array()
            .unwrap()
            .len(),
        13
    );
    assert_eq!(capabilities["inlayHintProvider"], true);
    assert_eq!(
        capabilities["signatureHelpProvider"]["triggerCharacters"],
        json!([","])
    );
}

#[test]
fn nested_macro_argument_commas_do_not_advance_the_active_parameter() {
    let fixture = Fixture::new();
    let path = fixture.write("nested.asm", "");
    let uri = to_uri(&path).unwrap();
    let source = "MyMacro: MACRO\ndb \\1,\\2\nENDM\nMyMacro STRCAT(\"A\",\"B\"),2,3\n";
    let mut server = Server::default();
    server.handle(&json!({"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":uri,"text":source}}}));
    for (marker, active) in [("\"B\"", 0), ("2,3", 1), ("3\n", 1)] {
        let offset = source.find(marker).unwrap();
        let position = koh_lsp::paths::position(source, source[..offset].encode_utf16().count());
        let help = request(
            &mut server,
            "textDocument/signatureHelp",
            json!({"textDocument":{"uri":uri},"position":position}),
        );
        assert_eq!(help["activeParameter"], active, "{marker}");
    }
}

#[test]
fn untitled_documents_preserve_client_uris_without_creating_files() {
    let fixture = Fixture::new();
    let root = to_uri(&fixture.0).unwrap();
    let uri = "untitled:Untitled-1";
    let mut server = Server::default();
    request(
        &mut server,
        "initialize",
        json!({"rootUri":root,"capabilities":{}}),
    );
    let notifications=server.handle(&json!({"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":uri,"text":"SECTION \"Main\",ROM0\nTarget: nop\njp Target\n"}}}));
    assert!(notifications.iter().any(|message| message["method"]
        == "textDocument/publishDiagnostics"
        && message["params"]["uri"] == uri));
    let params = json!({"textDocument":{"uri":uri},"position":{"line":2,"character":4}});
    assert_eq!(
        request(&mut server, "textDocument/definition", params.clone())["uri"],
        uri
    );
    let mut rename = params;
    rename["newName"] = json!("Renamed");
    assert_eq!(
        request(&mut server, "textDocument/rename", rename)["changes"][uri]
            .as_array()
            .unwrap()
            .len(),
        2
    );
    let notifications=server.handle(&json!({"jsonrpc":"2.0","method":"textDocument/didClose","params":{"textDocument":{"uri":uri}}}));
    assert_eq!(notifications[0]["params"]["uri"], uri);
    assert_eq!(std::fs::read_dir(&fixture.0).unwrap().count(), 0);
}
