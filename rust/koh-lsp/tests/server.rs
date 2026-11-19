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
