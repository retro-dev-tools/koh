use koh_lsp::transport::{read_message, write_message};
use serde_json::json;
use std::io;

fn run() -> io::Result<i32> {
    let mut input = io::stdin().lock();
    let mut output = io::stdout().lock();
    let mut shutdown = false;
    while let Some(message) = read_message(&mut input)? {
        let method = message["method"].as_str().unwrap_or("");
        if method == "exit" {
            return Ok(if shutdown { 0 } else { 1 });
        }
        let Some(id) = message.get("id") else {
            continue;
        };
        let response = match method {
            "initialize" => json!({"jsonrpc":"2.0","id":id,"result":{"capabilities":{}}}),
            "shutdown" => {
                shutdown = true;
                json!({"jsonrpc":"2.0","id":id,"result":null})
            }
            _ => {
                json!({"jsonrpc":"2.0","id":id,"error":{"code":-32601,"message":"Method not found"}})
            }
        };
        write_message(&mut output, &response)?;
    }
    Ok(if shutdown { 0 } else { 1 })
}

fn main() {
    let code = match run() {
        Ok(code) => code,
        Err(error) => {
            eprintln!("koh-lsp: {error}");
            1
        }
    };
    std::process::exit(code);
}
