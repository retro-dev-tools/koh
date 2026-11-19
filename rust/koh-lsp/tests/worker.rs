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

#[test]
fn unread_large_request_times_out_and_a_fresh_worker_can_start() {
    for _ in 0..2 {
        let mut command = if cfg!(windows) {
            let mut command = Command::new("powershell.exe");
            command.args([
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "Start-Sleep -Seconds 30",
            ]);
            command
        } else {
            let mut command = Command::new("sh");
            command.args(["-c", "exec sleep 30"]);
            command
        };
        command.stdin(std::process::Stdio::piped());
        let mut worker = Worker::start(command, Duration::from_millis(250)).unwrap();
        let started = std::time::Instant::now();
        let error = worker
            .request(&json!({"version":1,"text":" ".repeat(4*1024*1024)}))
            .unwrap_err();
        assert_eq!(error.kind(), std::io::ErrorKind::TimedOut);
        assert!(started.elapsed() < Duration::from_secs(15));
    }
}

#[cfg(unix)]
#[test]
fn worker_eof_bad_version_and_timeout_fail_without_hanging() {
    for (script, kind) in [
        ("read line; exit 0", std::io::ErrorKind::UnexpectedEof),
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
        let timeout = if kind == std::io::ErrorKind::TimedOut {
            Duration::from_millis(200)
        } else {
            Duration::from_secs(5)
        };
        let mut worker = Worker::start(command, timeout).unwrap();
        assert_eq!(
            worker.request(&json!({"version":1})).unwrap_err().kind(),
            kind
        );
    }
}

#[cfg(unix)]
#[test]
fn worker_exit_before_request_reports_a_transport_error() {
    let mut command = Command::new("sh");
    command.args(["-c", "exit 0"]);
    let mut worker = Worker::start(command, Duration::from_secs(2)).unwrap();
    let error = worker.request(&json!({"version":1})).unwrap_err();
    assert!(matches!(
        error.kind(),
        std::io::ErrorKind::BrokenPipe | std::io::ErrorKind::UnexpectedEof
    ));
}
