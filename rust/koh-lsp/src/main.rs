use koh_lsp::server::Server;
use koh_lsp::transport::{read_message, write_message};
use std::io;

fn run() -> io::Result<i32> {
    let mut input = io::stdin().lock();
    let mut output = io::stdout().lock();
    let mut server = Server::default();
    while let Some(message) = read_message(&mut input)? {
        for response in server.handle(&message) {
            write_message(&mut output, &response)?;
        }
        if let Some(code) = server.exit_code {
            return Ok(code);
        }
    }
    Ok(server.completion_code())
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
