use koh_lsp::transport::{read_message, write_message};
use serde_json::{Value, json};
use std::{
    io::BufReader,
    process::{Command, Stdio},
};
mod common;

fn server() -> Command {
    Command::new(
        std::env::var_os("KOH_LSP_TEST_BINARY")
            .unwrap_or_else(|| env!("CARGO_BIN_EXE_koh-lsp").into()),
    )
}

#[test]
fn real_stdio_publishes_diagnostics_and_serves_symbol_hover() {
    let fixture = common::Fixture::new();
    let path = fixture.write("main.asm", "");
    let uri = koh_lsp::paths::to_uri(&path).unwrap();
    let mut child = server()
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .spawn()
        .unwrap();
    let mut input = child.stdin.take().unwrap();
    for message in [
        json!({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}),
        json!({"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":uri,"text":"SECTION \"Main\",ROM0\nTarget: nop\njp Target\n","version":1}}}),
        json!({"jsonrpc":"2.0","id":2,"method":"textDocument/hover","params":{"textDocument":{"uri":uri},"position":{"line":2,"character":4}}}),
        json!({"jsonrpc":"2.0","id":3,"method":"shutdown"}),
        json!({"jsonrpc":"2.0","method":"exit"}),
    ] {
        write_message(&mut input, &message).unwrap();
    }
    drop(input);
    let output = child.wait_with_output().unwrap();
    assert!(output.status.success());
    let mut reader = std::io::Cursor::new(output.stdout);
    let mut messages = Vec::new();
    while let Some(message) = read_message(&mut reader).unwrap() {
        messages.push(message);
    }
    assert_eq!(
        messages.iter().find(|m| m["id"] == 1).unwrap()["result"]["capabilities"]["hoverProvider"],
        true
    );
    assert!(
        messages
            .iter()
            .any(|m| m["method"] == "textDocument/publishDiagnostics")
    );
    assert!(
        messages.iter().find(|m| m["id"] == 2).unwrap()["result"]["contents"]["value"]
            .as_str()
            .unwrap()
            .contains("**Target**")
    );
}

#[test]
fn initialize_shutdown_and_exit_use_framed_stdio() {
    let mut child = server()
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .spawn()
        .unwrap();
    let mut input = child.stdin.take().unwrap();
    let mut output = BufReader::new(child.stdout.take().unwrap());
    write_message(
        &mut input,
        &json!({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}),
    )
    .unwrap();
    let response = read_message(&mut output).unwrap().unwrap();
    assert_eq!(response["id"], 1);
    assert!(response["result"]["capabilities"].is_object());
    write_message(
        &mut input,
        &json!({"jsonrpc":"2.0","id":2,"method":"shutdown"}),
    )
    .unwrap();
    assert_eq!(
        read_message(&mut output).unwrap().unwrap()["result"],
        Value::Null
    );
    write_message(&mut input, &json!({"jsonrpc":"2.0","method":"exit"})).unwrap();
    assert!(child.wait().unwrap().success());
}

#[test]
fn exit_without_shutdown_is_unsuccessful() {
    let mut child = server()
        .stdin(Stdio::piped())
        .stdout(Stdio::null())
        .spawn()
        .unwrap();
    write_message(
        child.stdin.as_mut().unwrap(),
        &json!({"jsonrpc":"2.0","method":"exit"}),
    )
    .unwrap();
    assert_eq!(child.wait().unwrap().code(), Some(1));
}

#[test]
fn eof_after_shutdown_preserves_successful_exit() {
    let mut child = server()
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .spawn()
        .unwrap();
    let mut input = child.stdin.take().unwrap();
    write_message(
        &mut input,
        &json!({"jsonrpc":"2.0","id":1,"method":"shutdown"}),
    )
    .unwrap();
    drop(input);
    let output = child.wait_with_output().unwrap();
    assert!(output.status.success());
    let response = read_message(&mut std::io::Cursor::new(output.stdout))
        .unwrap()
        .unwrap();
    assert_eq!(response["id"], 1);
    assert_eq!(response["result"], Value::Null);
}
