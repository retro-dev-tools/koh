use serde_json::Value;
use std::io::{self, BufRead, Read, Write};

const MAX_MESSAGE: u64 = 64 * 1024 * 1024;
const MAX_HEADER: u64 = 8192;

pub fn read_message(input: &mut impl BufRead) -> io::Result<Option<Value>> {
    let mut length = None;
    let mut remaining = MAX_HEADER;
    loop {
        let mut line = Vec::new();
        let read = input.take(remaining + 1).read_until(b'\n', &mut line)?;
        if read == 0 {
            return if remaining == MAX_HEADER {
                Ok(None)
            } else {
                Err(io::Error::new(
                    io::ErrorKind::UnexpectedEof,
                    "truncated LSP header",
                ))
            };
        }
        remaining = remaining
            .checked_sub(read as u64)
            .ok_or_else(|| invalid("LSP header too large"))?;
        if line == b"\r\n" || line == b"\n" {
            break;
        }
        let line = std::str::from_utf8(&line).map_err(|_| invalid("invalid LSP header"))?;
        let (name, value) = line
            .split_once(':')
            .ok_or_else(|| invalid("invalid LSP header"))?;
        if name.eq_ignore_ascii_case("Content-Length") {
            if length.is_some() {
                return Err(invalid("duplicate Content-Length"));
            }
            let count = value
                .trim()
                .parse::<u64>()
                .map_err(|_| invalid("invalid Content-Length"))?;
            if count > MAX_MESSAGE {
                return Err(invalid("LSP message too large"));
            }
            length = Some(count);
        }
    }
    let length = length.ok_or_else(|| invalid("missing Content-Length"))?;
    let mut body = vec![0; length as usize];
    input.read_exact(&mut body)?;
    serde_json::from_slice(&body)
        .map(Some)
        .map_err(|e| invalid(e.to_string()))
}

pub fn write_message(output: &mut impl Write, message: &Value) -> io::Result<()> {
    let body = serde_json::to_vec(message)?;
    write!(output, "Content-Length: {}\r\n\r\n", body.len())?;
    output.write_all(&body)?;
    output.flush()
}

fn invalid(message: impl Into<String>) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message.into())
}
