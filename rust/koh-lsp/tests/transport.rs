use koh_lsp::transport::{read_message, write_message};
use serde_json::json;
use std::io::Cursor;

#[test]
fn unicode_frames_round_trip_without_consuming_the_next_message() {
    let first = json!({"jsonrpc":"2.0","id":1,"result":"😀 zażółć"});
    let second = json!({"jsonrpc":"2.0","id":"two","result":null});
    let mut bytes = Vec::new();
    write_message(&mut bytes, &first).unwrap();
    write_message(&mut bytes, &second).unwrap();
    let mut input = Cursor::new(bytes);
    assert_eq!(read_message(&mut input).unwrap(), Some(first));
    assert_eq!(read_message(&mut input).unwrap(), Some(second));
    assert_eq!(read_message(&mut input).unwrap(), None);
}

#[test]
fn headers_are_case_insensitive_and_allow_content_type() {
    let mut input = Cursor::new(
        b"content-length: 2\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n{}",
    );
    assert_eq!(read_message(&mut input).unwrap(), Some(json!({})));
}

#[test]
fn invalid_or_truncated_frames_fail() {
    for bytes in [
        &b"\r\n{}"[..],
        b"Content-Length: -1\r\n\r\n",
        b"Content-Length: 999999999999\r\n\r\n",
        b"Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}",
        b"Content-Length: 3\r\n\r\n{}",
        b"Content-Length: 1\r\n\r\nx",
        b"Content-Length: 2\r\n",
    ] {
        assert!(read_message(&mut Cursor::new(bytes)).is_err(), "{bytes:?}");
    }
    assert!(read_message(&mut Cursor::new(vec![b'x'; 9000])).is_err());
}
