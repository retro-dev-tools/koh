use koh_lsp::worker::Worker;
use serde_json::json;
use std::{process::Command, time::Duration};

#[test]
fn real_worker_handles_multiple_requests_and_utf16_spans() {
    let mut worker = Worker::assembler().unwrap();
    let request = json!({"version":1,"op":"parse","sources":[{"path":"/test.asm","text":"; 😀\nTarget: nop\n"}],"roots":["/test.asm"]});
    for _ in 0..2 {
        let response = worker.request(&request).unwrap();
        assert_eq!(response["version"], 1);
        assert!(
            response["documents"][0]["tokens"]
                .as_array()
                .unwrap()
                .iter()
                .any(|t| t["text"] == "Target" && t["start"] == 5)
        );
    }
}

#[cfg(unix)]
#[test]
fn worker_eof_bad_version_and_timeout_fail_without_hanging() {
    for (script, kind) in [
        ("exit 0", std::io::ErrorKind::UnexpectedEof),
        (
            "read line; printf '%s\\n' '{\"version\":2}'",
            std::io::ErrorKind::InvalidData,
        ),
        (
            "read line; printf '%s\\n' 'invalid'",
            std::io::ErrorKind::InvalidData,
        ),
        (
            "read line; printf '%s\\n' '{\"version\":1}'",
            std::io::ErrorKind::InvalidData,
        ),
        ("read line; exec sleep 30", std::io::ErrorKind::TimedOut),
    ] {
        let mut command = Command::new("sh");
        command.args(["-c", script]);
        let mut worker = Worker::start(command, Duration::from_millis(200)).unwrap();
        assert_eq!(
            worker.request(&json!({"version":1})).unwrap_err().kind(),
            kind
        );
    }
}
