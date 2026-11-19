use koh_lsp::transport::{read_message, write_message};
use serde_json::{Value, json};
use std::{
    io::BufReader,
    process::{Command, Stdio},
};

#[test]
fn initialize_shutdown_and_exit_use_framed_stdio() {
    let mut child = Command::new(env!("CARGO_BIN_EXE_koh-lsp"))
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
    let mut child = Command::new(env!("CARGO_BIN_EXE_koh-lsp"))
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
