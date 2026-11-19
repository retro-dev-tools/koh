use serde_json::{Value, json};
use std::{
    fs,
    io::Write,
    process::{Command, Stdio},
};

#[test]
fn failed_state_and_sram_writes_preserve_files_and_session() {
    let directory = std::env::temp_dir().join(format!("koh-failed-save-{}", std::process::id()));
    fs::create_dir(&directory).unwrap();
    let rom = directory.join("game.gb");
    let state = directory.join("game.gb.state");
    let sram = directory.join("game.gb.sav");
    let mut bytes = vec![0; 32768];
    bytes[0x100..0x103].copy_from_slice(&[0, 0x18, 0xfd]);
    bytes[0x147] = 3;
    bytes[0x149] = 2;
    fs::write(&rom, bytes).unwrap();
    fs::write(&state, b"previous state").unwrap();
    fs::write(&sram, vec![0x42; 8192]).unwrap();
    fs::create_dir(directory.join("game.gb.state.tmp")).unwrap();
    fs::create_dir(directory.join("game.gb.sav.tmp")).unwrap();
    let mut child = Command::new(env!("CARGO_BIN_EXE_koh-engine"))
        .args(["--session", "--headless"])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let requests = [
        json!({"id":1,"command":"load","path":rom,"paused":true,"debug":true}),
        json!({"id":2,"command":"save"}),
        json!({"id":3,"command":"snapshot","debug":true}),
        json!({"id":4,"command":"step","debug":true}),
        json!({"id":5,"command":"quit"}),
    ];
    let mut input = child.stdin.take().unwrap();
    for request in requests {
        writeln!(input, "{request}").unwrap();
    }
    drop(input);
    let output = child.wait_with_output().unwrap();
    assert_eq!(output.status.code(), Some(1));
    let replies: Vec<Value> = String::from_utf8(output.stdout)
        .unwrap()
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect();
    assert_eq!(replies.len(), 5);
    assert_eq!(replies[0]["ok"], true);
    assert_eq!(replies[1]["ok"], false);
    assert!(
        replies[1]["error"]
            .as_str()
            .unwrap()
            .contains("Save state failed")
    );
    assert_eq!(replies[2]["snapshot"], replies[0]["snapshot"]);
    assert_eq!(replies[3]["ok"], true);
    assert_ne!(replies[3]["snapshot"]["cpu"], replies[2]["snapshot"]["cpu"]);
    assert_eq!(fs::read(state).unwrap(), b"previous state");
    assert_eq!(fs::read(sram).unwrap(), vec![0x42; 8192]);
    assert!(String::from_utf8_lossy(&output.stderr).contains("SRAM save failed"));
    fs::remove_dir_all(directory).unwrap();
}
