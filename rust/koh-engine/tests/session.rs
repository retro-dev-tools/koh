use std::io::Write;
use std::process::{Command, Stdio};
#[path = "support/dap_transport.rs"]
mod dap_transport;
use dap_transport::{DapStream, endpoint};

fn run(input: &str) -> std::process::Output {
    let mut child = Command::new(env!("CARGO_BIN_EXE_koh-engine"))
        .args(["--session", "--headless"])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child
        .stdin
        .take()
        .unwrap()
        .write_all(input.as_bytes())
        .unwrap();
    child.wait_with_output().unwrap()
}
#[test]
fn empty_session_snapshot_and_quit() {
    let output = run("{\"id\":1,\"command\":\"snapshot\"}\n{\"id\":2,\"command\":\"quit\"}\n");
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let replies: Vec<serde_json::Value> = String::from_utf8(output.stdout)
        .unwrap()
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect();
    assert_eq!(replies.len(), 2);
    let s = &replies[0]["snapshot"];
    assert_eq!(s["protocolVersion"], 1);
    assert_eq!(s["paused"], true);
    assert!(s["romPath"].is_null());
    assert_eq!(replies[1]["id"], 2);
}
#[test]
fn invalid_commands_remain_recoverable_and_validate_before_mutation() {
    let output = run(
        "{\"id\":1,\"command\":\"snapshot\",\"id\":2}\n{\"id\":3,\"command\":\"fastForward\",\"enabled\":true,\"address\":65536}\n{\"id\":4,\"command\":\"snapshot\"}\n",
    );
    assert!(output.status.success());
    let replies: Vec<serde_json::Value> = String::from_utf8(output.stdout)
        .unwrap()
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect();
    assert_eq!(replies.len(), 3);
    assert_eq!(replies[0]["ok"], false);
    assert_eq!(replies[1]["ok"], false);
    assert_eq!(replies[2]["snapshot"]["fastForward"], false);
}
#[test]
fn incomplete_and_oversized_input_exit_one() {
    assert_eq!(run("{\"id\":1").status.code(), Some(1));
    assert_eq!(run(&"x".repeat(16385)).status.code(), Some(1));
}

use std::{
    fs,
    io::{BufRead, BufReader},
    path::{Path, PathBuf},
    sync::atomic::{AtomicU64, Ordering},
};
static NEXT: AtomicU64 = AtomicU64::new(0);
struct Temp(PathBuf);
impl Temp {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!(
            "koh-engine-process-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        fs::create_dir_all(&path).unwrap();
        Self(path)
    }
    fn path(&self, name: &str) -> PathBuf {
        self.0.join(name)
    }
}
impl Drop for Temp {
    fn drop(&mut self) {
        let _ = fs::remove_dir_all(&self.0);
    }
}
fn rom(path: &Path, cgb: bool, battery: bool) {
    let mut bytes = vec![0; 32768];
    bytes[0x100..0x110].copy_from_slice(&[
        0x3e, 0x0a, 0xea, 0, 0, 0x3e, 0x42, 0xea, 0, 0xa0, 0x3e, 0x10, 0xe0, 0, 0x18, 0xfe,
    ]);
    bytes[0x143] = if cgb { 0x80 } else { 0 };
    if battery {
        bytes[0x147] = 3;
        bytes[0x149] = 2;
    }
    fs::write(path, bytes).unwrap();
}
struct Client {
    child: std::process::Child,
    input: Option<std::process::ChildStdin>,
    output: BufReader<std::process::ChildStdout>,
    id: u64,
}
impl Client {
    fn start(binary: &Path, args: &[String]) -> Self {
        Self::start_mode(binary, args, true)
    }
    fn start_mode(binary: &Path, args: &[String], headless: bool) -> Self {
        Self::spawn(binary, args, headless, true)
    }
    fn spawn(binary: &Path, args: &[String], headless: bool, session: bool) -> Self {
        let mut child = Command::new(binary)
            .env("ALSOFT_DRIVERS", "null")
            .args(if session { Some("--session") } else { None })
            .args(if headless { Some("--headless") } else { None })
            .args(args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap();
        let input = child.stdin.take();
        let output = BufReader::new(child.stdout.take().unwrap());
        Self {
            child,
            input,
            output,
            id: 0,
        }
    }
    fn request(&mut self, mut value: serde_json::Value) -> serde_json::Value {
        self.id += 1;
        value["id"] = self.id.into();
        let input = self.input.as_mut().unwrap();
        serde_json::to_writer(&mut *input, &value).unwrap();
        input.write_all(b"\n").unwrap();
        input.flush().unwrap();
        let mut line = String::new();
        self.output.read_line(&mut line).unwrap();
        assert!(!line.is_empty(), "engine closed before reply");
        let reply: serde_json::Value = serde_json::from_str(&line).unwrap();
        assert_eq!(reply["id"], self.id);
        reply
    }
    fn finish(&mut self) -> i32 {
        drop(self.input.take());
        let end = std::time::Instant::now() + std::time::Duration::from_secs(10);
        loop {
            if let Some(status) = self.child.try_wait().unwrap() {
                return status.code().unwrap_or(-1);
            }
            assert!(std::time::Instant::now() < end, "engine failed to exit");
            std::thread::sleep(std::time::Duration::from_millis(5));
        }
    }
}
impl Drop for Client {
    fn drop(&mut self) {
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}
fn binary() -> &'static Path {
    Path::new(env!("CARGO_BIN_EXE_koh-engine"))
}

#[test]
fn paused_load_step_debug_state_rollback_and_failed_load() {
    use base64::Engine;
    use serde_json::json;
    let temp = Temp::new();
    let path = temp.path("test.gb");
    rom(&path, false, false);
    let mut client = Client::start(binary(), &[]);
    let loaded = client
        .request(json!({"command":"load","path":path,"paused":true,"debug":true,"address":65535}));
    assert_eq!(loaded["ok"], true);
    let s = &loaded["snapshot"];
    assert_eq!(s["cpu"]["pc"], 0);
    assert_eq!(s["memory"]["baseAddress"], 65535);
    assert_eq!(s["vram"]["height"], 192);
    assert_eq!(s["palettes"]["bgColors"].as_array().unwrap().len(), 4);
    let image = base64::engine::general_purpose::STANDARD
        .decode(s["framebuffer"].as_str().unwrap())
        .unwrap();
    assert_eq!(image.len(), 160 * 144 * 4);
    assert!(image.as_chunks::<4>().0.iter().all(|p| p[3] == 255));
    let step = client.request(json!({"command":"step"}));
    assert!(step["snapshot"]["cpu"]["totalTCycles"].as_u64().unwrap() > 0);
    assert_eq!(client.request(json!({"command":"save"}))["ok"], true);
    let before = client.request(json!({"command":"snapshot","debug":true}))["snapshot"].clone();
    client.request(json!({"command":"step"}));
    assert_eq!(
        client.request(json!({"command":"restore"}))["snapshot"]["cpu"],
        before["cpu"]
    );
    let state = PathBuf::from(format!("{}.state", path.display()));
    let saved = fs::read(&state).unwrap();
    assert!(!saved.starts_with(b"STES"));
    fs::OpenOptions::new()
        .write(true)
        .open(&state)
        .unwrap()
        .set_len(saved.len() as u64 + 16 * 1024 * 1024)
        .unwrap();
    assert_eq!(client.request(json!({"command":"restore"}))["ok"], true);
    fs::write(&state, &saved[..saved.len() - 100]).unwrap();
    let failed = client.request(json!({"command":"restore"}));
    assert_eq!(failed["ok"], false);
    assert_eq!(
        client.request(json!({"command":"snapshot","debug":true}))["snapshot"],
        before
    );
    assert_eq!(
        client.request(json!({"command":"load","path":temp.path("missing.gb")}))["ok"],
        false
    );
    assert_eq!(
        client.request(json!({"command":"snapshot"}))["snapshot"]["cpu"],
        before["cpu"]
    );
    assert_eq!(client.request(json!({"command":"quit"}))["ok"], true);
    assert_eq!(client.finish(), 0);
}

#[test]
fn sram_persists_on_quit_eof_and_oversized_input() {
    use serde_json::json;
    for (cgb, ending) in [
        (false, "quit"),
        (false, "eof"),
        (false, "oversize"),
        (false, "incomplete"),
        (true, "quit"),
    ] {
        let temp = Temp::new();
        let path = temp.path("battery.gb");
        rom(&path, cgb, true);
        let boot = temp.path("boot.bin");
        let mut bytes = vec![0; if cgb { 2304 } else { 256 }];
        bytes[..3].copy_from_slice(&[0xc3, 0xfc, 0]);
        bytes[252..256].copy_from_slice(&[0x3e, 1, 0xe0, 0x50]);
        fs::write(&boot, bytes).unwrap();
        let mut client = Client::start(binary(), &[format!("--boot-rom={}", boot.display())]);
        assert_eq!(
            client.request(json!({"command":"load","path":path,"paused":true}))["ok"],
            true
        );
        for _ in 0..9 {
            assert_eq!(client.request(json!({"command":"step"}))["ok"], true);
        }
        use base64::Engine;
        let pressed = client.request(
            json!({"command":"joypad","button":"A","down":true,"debug":true,"address":0xff00}),
        );
        let memory = base64::engine::general_purpose::STANDARD
            .decode(pressed["snapshot"]["memory"]["bytes"].as_str().unwrap())
            .unwrap();
        assert_eq!(memory[0] & 1, 0);
        client.request(json!({"command":"fastForward","enabled":true}));
        let released =
            client.request(json!({"command":"releaseInput","debug":true,"address":0xff00}));
        let memory = base64::engine::general_purpose::STANDARD
            .decode(released["snapshot"]["memory"]["bytes"].as_str().unwrap())
            .unwrap();
        assert_eq!(memory[0] & 1, 1);
        assert_eq!(released["snapshot"]["fastForward"], false);
        match ending {
            "quit" => {
                client.request(json!({"command":"quit"}));
            }
            "oversize" => client
                .input
                .as_mut()
                .unwrap()
                .write_all(&vec![b'x'; 16385])
                .unwrap(),
            "incomplete" => client.input.as_mut().unwrap().write_all(b"{").unwrap(),
            _ => {}
        }
        assert_eq!(
            client.finish(),
            if matches!(ending, "oversize" | "incomplete") {
                1
            } else {
                0
            }
        );
        let saved = fs::read(format!("{}.sav", path.display())).unwrap();
        assert_eq!(saved.len(), 8192);
        assert_eq!(saved[0], 0x42);
        assert!(!Path::new(&format!("{}.sav.tmp", path.display())).exists());
    }
}

#[test]
#[ignore = "requires KOH_REFERENCE_ENGINE pointing to the immutable C# engine"]
fn independent_native_reference_snapshots_states_and_headless_pixels() {
    use serde_json::json;
    let reference = std::env::var_os("KOH_REFERENCE_ENGINE").expect("KOH_REFERENCE_ENGINE");
    let temp = Temp::new();
    for cgb in [false, true] {
        let path = temp.path(if cgb { "cgb.gb" } else { "dmg.gb" });
        rom(&path, cgb, false);
        let mut results = vec![];
        for engine in [Path::new(&reference), binary()] {
            let mut client = Client::start(engine, &[]);
            let mut snapshots = vec![];
            snapshots.push(client.request(json!({"command":"load","path":path,"paused":true,"debug":true,"address":65530}))["snapshot"].clone());
            for _ in 0..16 {
                snapshots.push(client.request(json!({"command":"step","debug":true,"address":0xc000}))["snapshot"].clone());
            }
            client.request(json!({"command":"save"}));
            let state = fs::read(format!("{}.state", path.display())).unwrap();
            client.request(json!({"command":"quit"}));
            assert_eq!(client.finish(), 0);
            results.push((snapshots, state));
        }
        assert_eq!(results[0].0, results[1].0, "snapshot parity cgb={cgb}");
        assert_eq!(results[0].1, results[1].1, "raw state parity cgb={cgb}");
        let mut continued = vec![];
        for (index, engine) in [Path::new(&reference), binary()].into_iter().enumerate() {
            fs::write(format!("{}.state", path.display()), &results[1 - index].1).unwrap();
            let mut client = Client::start(engine, &[]);
            client.request(json!({"command":"load","path":path,"paused":true}));
            assert_eq!(client.request(json!({"command":"restore"}))["ok"], true);
            let mut snapshots = vec![];
            for _ in 0..16 {
                snapshots.push(client.request(json!({"command":"step","debug":true,"address":0xff00}))["snapshot"].clone());
            }
            client.request(json!({"command":"quit"}));
            assert_eq!(client.finish(), 0);
            continued.push(snapshots);
        }
        assert_eq!(
            continued[0], continued[1],
            "bidirectional foreign-state continuation cgb={cgb}"
        );
        let mut images = vec![];
        for (index, engine) in [Path::new(&reference), binary()].into_iter().enumerate() {
            let png = temp.path(&format!("frame-{cgb}-{index}.png"));
            let output = Command::new(engine)
                .arg(&path)
                .args(["--headless", "--frames=4"])
                .arg(format!("--screenshot={}", png.display()))
                .output()
                .unwrap();
            assert!(
                output.status.success(),
                "{}",
                String::from_utf8_lossy(&output.stderr)
            );
            let mut reader = png::Decoder::new(BufReader::new(fs::File::open(png).unwrap()))
                .read_info()
                .unwrap();
            let mut bytes = vec![0; reader.output_buffer_size().unwrap()];
            let info = reader.next_frame(&mut bytes).unwrap();
            bytes.truncate(info.buffer_size());
            assert_eq!((info.width, info.height), (160, 144));
            images.push(bytes);
        }
        assert_eq!(images[0], images[1], "headless pixels cgb={cgb}");
    }
}

#[test]
fn dap_eof_disconnect_with_stdin_held_open_and_socket_cleanup() {
    use serde_json::json;
    use std::io::Read;
    let temp = Temp::new();
    let path = temp.path("dap.gb");
    rom(&path, false, false);
    for disconnect in [false, true] {
        let name = format!(
            "koh-rust-dap-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        );
        let socket = endpoint(&name);
        let mut client = Client::start(
            binary(),
            &[path.to_string_lossy().into_owned(), format!("--dap={name}")],
        );
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(5);
        let mut stream = loop {
            match DapStream::connect(&socket) {
                Ok(stream) => break stream,
                Err(_) => {
                    assert!(
                        std::time::Instant::now() < deadline,
                        "DAP socket unavailable"
                    );
                    std::thread::sleep(std::time::Duration::from_millis(5));
                }
            }
        };
        let request=serde_json::to_vec(&json!({"seq":1,"type":"request","command":if disconnect {"disconnect"}else{"threads"}})).unwrap();
        write!(stream, "Content-Length: {}\r\n\r\n", request.len()).unwrap();
        stream.write_all(&request).unwrap();
        let mut header = vec![];
        while !header.ends_with(b"\r\n\r\n") {
            let mut byte = [0];
            stream.read_exact(&mut byte).unwrap();
            header.push(byte[0]);
        }
        let header = String::from_utf8(header).unwrap();
        let length: usize = header.split(':').nth(1).unwrap().trim().parse().unwrap();
        let mut response = vec![0; length];
        stream.read_exact(&mut response).unwrap();
        let response: serde_json::Value = serde_json::from_slice(&response).unwrap();
        assert_eq!(response["success"], true);
        drop(stream);
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(5);
        loop {
            if let Some(status) = client.child.try_wait().unwrap() {
                assert!(status.success());
                break;
            }
            assert!(
                std::time::Instant::now() < deadline,
                "DAP close did not stop engine with stdin open"
            );
            std::thread::sleep(std::time::Duration::from_millis(5));
        }
        #[cfg(unix)]
        assert!(!socket.exists());
    }
}

struct Dap {
    stream: DapStream,
    sequence: i64,
}
impl Dap {
    fn connect(path: &Path) -> Self {
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(5);
        let stream = loop {
            match DapStream::connect(path) {
                Ok(stream) => break stream,
                Err(_) => {
                    assert!(std::time::Instant::now() < deadline);
                    std::thread::sleep(std::time::Duration::from_millis(5));
                }
            }
        };
        stream
            .set_read_timeout(Some(std::time::Duration::from_secs(15)))
            .unwrap();
        Self {
            stream,
            sequence: 0,
        }
    }
    fn raw(&mut self, bytes: &[u8]) {
        write!(self.stream, "Content-Length: {}\r\n\r\n", bytes.len()).unwrap();
        self.stream.write_all(bytes).unwrap();
    }
    fn read(&mut self) -> serde_json::Value {
        use std::io::Read;
        let mut header = vec![];
        while !header.ends_with(b"\r\n\r\n") {
            let mut byte = [0];
            self.stream.read_exact(&mut byte).unwrap();
            header.push(byte[0]);
        }
        let length: usize = String::from_utf8(header)
            .unwrap()
            .split(':')
            .nth(1)
            .unwrap()
            .trim()
            .parse()
            .unwrap();
        let mut bytes = vec![0; length];
        self.stream.read_exact(&mut bytes).unwrap();
        serde_json::from_slice(&bytes).unwrap()
    }
    fn request(&mut self, command: &str, arguments: serde_json::Value) -> serde_json::Value {
        self.sequence += 1;
        self.raw(&serde_json::to_vec(&serde_json::json!({"seq":self.sequence,"type":"request","command":command,"arguments":arguments})).unwrap());
        loop {
            let value = self.read();
            if value["type"] == "response" && value["request_seq"] == self.sequence {
                return value;
            }
        }
    }
    fn stopped(&mut self) -> serde_json::Value {
        loop {
            let value = self.read();
            if value["event"] == "stopped" {
                return value;
            }
        }
    }
}

#[test]
fn dap_invalid_json_is_recoverable() {
    let temp = Temp::new();
    let path = temp.path("dap.gb");
    rom(&path, false, false);
    let name = format!(
        "koh-dap-invalid-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    );
    let mut client = Client::start(
        binary(),
        &[path.to_string_lossy().into_owned(), format!("--dap={name}")],
    );
    let mut dap = Dap::connect(&endpoint(&name));
    dap.raw(b"{");
    assert_eq!(dap.read()["success"], false);
    assert_eq!(
        dap.request("threads", serde_json::Value::Null)["success"],
        true
    );
    dap.request("disconnect", serde_json::Value::Null);
    assert_eq!(client.finish(), 0);
}

#[test]
fn dap_replies_and_async_events_arrive_with_idle_input() {
    use serde_json::json;
    let temp = Temp::new();
    let path = temp.path("idle-dap.gb");
    rom(&path, false, false);
    let name = format!(
        "koh-dap-idle-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    );
    let mut client = Client::start(
        binary(),
        &[path.to_string_lossy().into_owned(), format!("--dap={name}")],
    );
    let mut dap = Dap::connect(&endpoint(&name));
    assert_eq!(dap.request("initialize", json!({}))["success"], true);
    assert_eq!(dap.read()["event"], "initialized");
    std::thread::sleep(std::time::Duration::from_millis(50));
    assert_eq!(
        dap.request("threads", json!({}))["body"]["threads"][0]["name"],
        "CPU"
    );
    dap.request("launch", json!({"stopOnEntry":true}));
    std::thread::sleep(std::time::Duration::from_millis(50));
    dap.request("configurationDone", json!({}));
    assert_eq!(dap.stopped()["body"]["reason"], "entry");
    std::thread::sleep(std::time::Duration::from_millis(50));
    dap.request("pause", json!({}));
    assert_eq!(dap.stopped()["body"]["reason"], "pause");
    dap.sequence += 1;
    let pending =
        serde_json::to_vec(&json!({"seq":dap.sequence,"type":"request","command":"threads"}))
            .unwrap();
    write!(dap.stream, "Content-Length: {}\r\n\r\n", pending.len()).unwrap();
    dap.stream.write_all(&pending[..1]).unwrap();
    client.id += 1;
    serde_json::to_writer(
        client.input.as_mut().unwrap(),
        &json!({"id":client.id,"command":"step"}),
    )
    .unwrap();
    client.input.as_mut().unwrap().write_all(b"\n").unwrap();
    client.input.as_mut().unwrap().flush().unwrap();
    assert_eq!(dap.stopped()["body"]["reason"], "step");
    let mut line = String::new();
    client.output.read_line(&mut line).unwrap();
    let reply: serde_json::Value = serde_json::from_str(&line).unwrap();
    assert_eq!(reply["id"], client.id);
    assert_eq!(reply["ok"], true);
    dap.stream.write_all(&pending[1..]).unwrap();
    let response = dap.read();
    assert_eq!(response["request_seq"], dap.sequence);
    assert_eq!(response["success"], true);
    dap.request("disconnect", json!({}));
    assert_eq!(client.finish(), 0);
}

#[cfg(unix)]
#[test]
fn dap_uses_vscode_explicit_unix_socket_path() {
    let temp = Temp::new();
    let path = temp.path("dap.gb");
    rom(&path, false, false);
    let socket = temp.path("vscode.sock");
    let mut client = Client::start(
        binary(),
        &[
            path.to_string_lossy().into_owned(),
            format!("--dap={}", socket.display()),
        ],
    );
    let mut dap = Dap::connect(&socket);
    assert_eq!(
        dap.request("initialize", serde_json::json!({}))["success"],
        true
    );
    dap.request("disconnect", serde_json::json!({}));
    assert_eq!(client.finish(), 0);
    assert!(!socket.exists());
}

#[test]
fn dap_empty_session_initializes_and_can_load_rom() {
    use serde_json::json;
    let temp = Temp::new();
    let path = temp.path("empty-dap.gb");
    rom(&path, false, false);
    let name = format!(
        "koh-dap-empty-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    );
    let mut client = Client::start(binary(), &[format!("--dap={name}")]);
    let mut dap = Dap::connect(&endpoint(&name));
    assert_eq!(dap.request("initialize", json!({}))["success"], true);
    assert_eq!(
        dap.request("threads", json!({}))["body"]["threads"][0]["name"],
        "CPU"
    );
    assert_eq!(dap.request("continue", json!({}))["success"], true);
    assert_eq!(
        client.request(json!({"command":"snapshot"}))["snapshot"]["paused"],
        true
    );
    assert_eq!(
        client.request(json!({"command":"load","path":path,"paused":true}))["ok"],
        true
    );
    assert_eq!(
        dap.request("evaluate", json!({"expression":"PC"}))["success"],
        true
    );
    client.request(json!({"command":"step"}));
    dap.stream
        .set_read_timeout(Some(std::time::Duration::from_millis(500)))
        .unwrap();
    assert_eq!(dap.stopped()["body"]["reason"], "step");
    dap.request("disconnect", json!({}));
    assert_eq!(client.finish(), 0);
}

#[test]
fn dap_real_dwarf_breakpoint_memory_and_call_stepping() {
    use serde_json::json;
    let temp = Temp::new();
    let source = "SECTION \"Entry\", ROM0[$100]\n jp Main\nSECTION \"main\", ROM0[$150]\nMain::\n call Sub\n ld a,$42\nLoop:: jr Loop\nSECTION \"sub\", ROM0[$200]\nSub:: nop\n ret\n";
    let object = koh_asm::assemble(source, "fixture.asm", false).unwrap();
    let linked = koh_link::link(&[object], &Default::default()).unwrap();
    let path = temp.path("fixture.gb");
    let elf = temp.path("fixture.elf");
    fs::write(&path, &linked.rom).unwrap();
    fs::write(&elf, linked.dwarf().unwrap()).unwrap();
    let name = format!(
        "koh-dap-debug-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    );
    let mut client = Client::start(
        binary(),
        &[path.to_string_lossy().into_owned(), format!("--dap={name}")],
    );
    client.request(json!({"command":"fastForward","enabled":true}));
    let mut dap = Dap::connect(&endpoint(&name));
    assert_eq!(dap.request("initialize", json!({}))["success"], true);
    assert_eq!(
        dap.request("launch", json!({"debugInfo":elf}))["success"],
        true
    );
    let bp = dap.request(
        "setBreakpoints",
        json!({"source":{"path":"fixture.asm"},"breakpoints":[{"line":5}]}),
    );
    assert_eq!(bp["body"]["breakpoints"][0]["verified"], true);
    dap.request("configurationDone", json!({}));
    assert_eq!(dap.stopped()["body"]["reason"], "breakpoint");
    let stack = dap.request("stackTrace", json!({"threadId":1}));
    assert_eq!(stack["body"]["stackFrames"][0]["line"], 5);
    assert_eq!(
        stack["body"]["stackFrames"][0]["source"]["path"],
        "fixture.asm"
    );
    dap.request(
        "writeMemory",
        json!({"memoryReference":"0xC000","data":"Ql8="}),
    );
    let memory = dap.request("readMemory", json!({"memoryReference":"0xC000","count":2}));
    assert_eq!(memory["body"]["data"], "Ql8=");
    dap.request("stepIn", json!({"threadId":1}));
    assert_eq!(dap.stopped()["body"]["reason"], "step");
    let stack = dap.request("stackTrace", json!({"threadId":1}));
    assert_eq!(stack["body"]["stackFrames"][0]["line"], 9);
    dap.request("stepOut", json!({"threadId":1}));
    assert_eq!(dap.stopped()["body"]["reason"], "step");
    let stack = dap.request("stackTrace", json!({"threadId":1}));
    assert_eq!(stack["body"]["stackFrames"][0]["line"], 6);
    dap.request("next", json!({"threadId":1}));
    dap.stopped();
    assert_eq!(
        dap.request("evaluate", json!({"expression":"A"}))["body"]["result"],
        "$42 (66)"
    );
    dap.request("disconnect", json!({}));
    assert_eq!(client.finish(), 0);
}

#[test]
fn legacy_headless_dap_parameter_matrix() {
    use serde_json::json;
    let temp = Temp::new();
    let source = "SECTION \"Entry\", ROM0[$100]\n jp Main\nSECTION \"main\", ROM0[$150]\nMain::\n ld a,$42\n jr Main\n";
    let object = koh_asm::assemble(source, "fixture.asm", false).unwrap();
    let linked = koh_link::link(&[object], &Default::default()).unwrap();
    let path = temp.path("matrix.gb");
    let elf = temp.path("matrix.elf");
    fs::write(&path, &linked.rom).unwrap();
    fs::write(&elf, linked.dwarf().unwrap()).unwrap();
    for (ending, headless, session) in [
        ("disconnect", true, false),
        ("terminate", true, false),
        ("eof", true, false),
        ("disconnect", false, false),
        ("disconnect", true, true),
        ("eof", true, true),
    ] {
        let name = format!(
            "koh-dap-matrix-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        );
        let mut client = Client::spawn(
            binary(),
            &[path.to_string_lossy().into_owned(), format!("--dap={name}")],
            headless,
            session,
        );
        let mut dap = Dap::connect(&endpoint(&name));
        assert_eq!(
            dap.request("initialize", json!({"adapterID":"koh"}))["success"],
            true
        );
        assert_eq!(
            dap.request("launch", json!({"stopOnEntry":true,"debugInfo":elf}))["success"],
            true
        );
        dap.request("configurationDone", json!({}));
        assert!(
            !dap.request("stackTrace", json!({"threadId":1}))["body"]["stackFrames"]
                .as_array()
                .unwrap()
                .is_empty()
        );
        let bp = dap.request(
            "setBreakpoints",
            json!({"source":{"path":"fixture.asm"},"breakpoints":[{"line":5}]}),
        );
        assert_eq!(bp["body"]["breakpoints"][0]["verified"], true);
        dap.request("continue", json!({"threadId":1}));
        assert_eq!(dap.stopped()["body"]["reason"], "breakpoint");
        dap.request("stepIn", json!({"threadId":1}));
        dap.stopped();
        assert_eq!(
            dap.request("stackTrace", json!({"threadId":1}))["body"]["stackFrames"][0]["instructionPointerReference"],
            "0x0152"
        );
        assert_eq!(
            dap.request("readMemory", json!({"memoryReference":"0x0150","count":2}))["body"]["data"],
            "PkI="
        );
        if ending != "eof" {
            assert_eq!(dap.request(ending, json!({}))["success"], true);
        }
        drop(dap);
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(5);
        loop {
            if let Some(status) = client.child.try_wait().unwrap() {
                assert!(status.success(), "{ending}/{headless}/{session}");
                break;
            }
            assert!(
                std::time::Instant::now() < deadline,
                "{ending}/{headless}/{session} did not exit with stdin held open"
            );
            std::thread::sleep(std::time::Duration::from_millis(5));
        }
        let mut errors = String::new();
        std::io::Read::read_to_string(client.child.stderr.as_mut().unwrap(), &mut errors).unwrap();
        assert!(!errors.contains("graphics unavailable"));
        if headless {
            assert!(!errors.contains("audio unavailable"));
        }
    }
}

#[test]
fn legacy_windowless_session_parameter_matrix() {
    use base64::Engine;
    use serde_json::json;
    for (cgb, ending) in [(false, "eof"), (true, "quit"), (false, "oversized")] {
        let temp = Temp::new();
        let path = temp.path("windowless.gb");
        let source = "SECTION \"Entry\", ROM0[$100]\n jp Main\nSECTION \"main\", ROM0[$150]\nMain::\n xor a\n ldh [$ff00],a\n ld a,$0a\n ld [$0000],a\n ld a,$42\n ld [$a000],a\nLoop:: jr Loop\n";
        let object = koh_asm::assemble(source, "windowless.asm", false).unwrap();
        let mut linked = koh_link::link(&[object], &Default::default()).unwrap();
        linked.rom[0x143] = if cgb { 0x80 } else { 0 };
        linked.rom[0x147] = 3;
        linked.rom[0x149] = 2;
        linked.rom[0x14d] = linked.rom[0x134..=0x14c]
            .iter()
            .fold(0u8, |value, byte| value.wrapping_sub(*byte).wrapping_sub(1));
        fs::write(&path, linked.rom).unwrap();
        let mut client = Client::start(binary(), &[]);
        let empty = client.request(json!({"command":"snapshot"}));
        assert_eq!(empty["ok"], true);
        assert!(empty["snapshot"]["romPath"].is_null());
        let loaded = client.request(json!({"command":"load","path":path,"paused":true}));
        assert_eq!(loaded["ok"], true);
        assert_eq!(loaded["snapshot"]["paused"], true);
        assert_eq!(
            base64::engine::general_purpose::STANDARD
                .decode(loaded["snapshot"]["framebuffer"].as_str().unwrap())
                .unwrap()
                .len(),
            160 * 144 * 4
        );
        assert_eq!(client.request(json!({"command":"save"}))["ok"], true);
        let state = PathBuf::from(format!("{}.state", path.display()));
        assert!(state.exists());
        assert_eq!(client.request(json!({"command":"restore"}))["ok"], true);
        assert_eq!(
            client.request(json!({"command":"load","path":format!("{}.missing",path.display())}))["ok"],
            false
        );
        let unsupported = temp.path("unsupported-mbc2.gb");
        let mut bytes = fs::read(&path).unwrap();
        bytes[0x147] = 5;
        bytes[0x14d] = bytes[0x134..=0x14c]
            .iter()
            .fold(0u8, |value, byte| value.wrapping_sub(*byte).wrapping_sub(1));
        fs::write(&unsupported, bytes).unwrap();
        assert_eq!(
            client.request(json!({"command":"load","path":unsupported}))["ok"],
            false
        );
        assert_eq!(
            client.request(json!({"command":"reset","debug":"invalid"}))["ok"],
            false
        );
        assert_eq!(
            client.request(json!({"command":"snapshot","address":65536}))["ok"],
            false
        );
        let inspected = client.request(json!({"command":"snapshot","debug":true,"address":0xfff0}));
        let snapshot = &inspected["snapshot"];
        assert_eq!(snapshot["romPath"], path.to_string_lossy().as_ref());
        assert_eq!(snapshot["frame"], 0);
        assert_eq!(snapshot["memory"]["baseAddress"], 0xfff0);
        assert_eq!(
            base64::engine::general_purpose::STANDARD
                .decode(snapshot["memory"]["bytes"].as_str().unwrap())
                .unwrap()
                .len(),
            256
        );
        assert_eq!(snapshot["vram"]["height"], if cgb { 384 } else { 192 });
        assert_eq!(
            client.request(json!({"command":"joypad","button":"invalid","down":true}))["ok"],
            false
        );
        let stepped = client.request(json!({"command":"step"}));
        assert!(stepped["snapshot"]["cpu"]["totalTCycles"].as_u64().unwrap() > 0);
        let bytes = fs::read(&state).unwrap();
        fs::write(&state, &bytes[..100]).unwrap();
        assert_eq!(client.request(json!({"command":"restore"}))["ok"], false);
        assert_eq!(
            client.request(json!({"command":"snapshot"}))["snapshot"]["cpu"],
            stepped["snapshot"]["cpu"]
        );
        client.request(json!({"command":"fastForward","enabled":true}));
        client.request(json!({"command":"resume"}));
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(30);
        loop {
            let snapshot =
                client.request(json!({"command":"snapshot","debug":true,"address":0xa000}));
            let ram = base64::engine::general_purpose::STANDARD
                .decode(snapshot["snapshot"]["memory"]["bytes"].as_str().unwrap())
                .unwrap();
            if ram[0] == 0x42 {
                break;
            }
            assert!(
                std::time::Instant::now() < deadline,
                "ROM did not publish SRAM marker"
            );
        }
        let stopped = client.request(json!({"command":"pause"}));
        let frames = stopped["snapshot"]["frame"].as_u64().unwrap();
        assert!(frames > 0);
        let pressed = client.request(
            json!({"command":"joypad","button":"A","down":true,"debug":true,"address":0xff00}),
        );
        let bytes = base64::engine::general_purpose::STANDARD
            .decode(pressed["snapshot"]["memory"]["bytes"].as_str().unwrap())
            .unwrap();
        assert_eq!(bytes[0] & 1, 0);
        let released =
            client.request(json!({"command":"releaseInput","debug":true,"address":0xff00}));
        let bytes = base64::engine::general_purpose::STANDARD
            .decode(released["snapshot"]["memory"]["bytes"].as_str().unwrap())
            .unwrap();
        assert_eq!(bytes[0] & 1, 1);
        let final_snapshot = client.request(json!({"command":"snapshot"}));
        assert_eq!(final_snapshot["snapshot"]["frame"], frames);
        assert_eq!(final_snapshot["snapshot"]["fastForward"], false);
        if ending == "quit" {
            client.request(json!({"command":"quit"}));
        } else if ending == "oversized" {
            client
                .input
                .as_mut()
                .unwrap()
                .write_all(&vec![b' '; 16385])
                .unwrap();
            client.input.as_mut().unwrap().write_all(b"\n").unwrap();
        }
        assert_eq!(client.finish(), if ending == "oversized" { 1 } else { 0 });
        let mut errors = String::new();
        std::io::Read::read_to_string(client.child.stderr.as_mut().unwrap(), &mut errors).unwrap();
        assert!(!errors.to_lowercase().contains("graphics"), "{errors}");
        let mut trailing = String::new();
        std::io::Read::read_to_string(&mut client.output, &mut trailing).unwrap();
        assert!(trailing.is_empty());
        assert_eq!(
            fs::read(format!("{}.sav", path.display())).unwrap()[0],
            0x42
        );
    }
}

#[test]
#[ignore = "requires KOH_REFERENCE_ENGINE pointing to the immutable C# engine"]
fn native_reference_mode3_source_report_and_boot_override() {
    let reference = std::env::var_os("KOH_REFERENCE_ENGINE").expect("KOH_REFERENCE_ENGINE");
    let temp = Temp::new();
    let source = "SECTION \"Entry\", ROM0[$100]\n jp Main\nSECTION \"main\", ROM0[$150]\nMain::\n ld a,$80\n ldh [$ff40],a\nLoop::\n ld a,$42\n ld [$8000],a\n jr Loop\n";
    let object = koh_asm::assemble(source, "mode3.asm", false).unwrap();
    let linked = koh_link::link(&[object], &Default::default()).unwrap();
    let path = temp.path("mode3.gb");
    fs::write(&path, &linked.rom).unwrap();
    fs::write(path.with_extension("elf"), linked.dwarf().unwrap()).unwrap();
    let boot = temp.path("boot.bin");
    let mut bytes = vec![0; 256];
    bytes[..3].copy_from_slice(&[0xc3, 0xfc, 0]);
    bytes[252..].copy_from_slice(&[0x3e, 1, 0xe0, 0x50]);
    fs::write(&boot, bytes).unwrap();
    let input = temp.path("input.txt");
    fs::write(&input, "# input edges\n0:A:press\n1:A:release\n").unwrap();
    let mut reports = vec![];
    for (index, engine) in [Path::new(&reference), binary()].into_iter().enumerate() {
        let report = temp.path(&format!("report-{index}.txt"));
        let output = Command::new(engine)
            .arg(&path)
            .args(["--headless", "--frames=2"])
            .arg(format!("--boot-rom={}", boot.display()))
            .arg(format!("--input={}", input.display()))
            .arg(format!("--mode3-report={}", report.display()))
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "{}",
            String::from_utf8_lossy(&output.stderr)
        );
        let text = fs::read_to_string(report).unwrap();
        assert!(text.contains("mode3.asm:"));
        assert!(!text.starts_with("Mode-3 write violations: 0 "));
        reports.push(text);
    }
    assert_eq!(reports[0], reports[1]);
}

#[test]
fn boot_family_override_and_command_ranges_reject_without_mutation() {
    use serde_json::json;
    let temp = Temp::new();
    let path = temp.path("test.gb");
    rom(&path, false, false);
    let boot = temp.path("cgb.bin");
    fs::write(&boot, vec![0; 2304]).unwrap();
    let mut client = Client::start(binary(), &[format!("--boot-rom={}", boot.display())]);
    assert_eq!(
        client.request(json!({"command":"load","path":path,"paused":true}))["ok"],
        false
    );
    assert!(client.request(json!({"command":"snapshot"}))["snapshot"]["romPath"].is_null());
    client.request(json!({"command":"quit"}));
    assert_eq!(client.finish(), 0);
    let output = run(
        "{\"id\":9223372036854775808,\"command\":\"snapshot\"}\n{\"id\":-1,\"command\":\"snapshot\"}\n{\"id\":1,\"command\":\"fastForward\",\"enabled\":true,\"debug\":null}\n{\"id\":2,\"command\":\"snapshot\"}\n",
    );
    assert!(output.status.success());
    let replies: Vec<serde_json::Value> = String::from_utf8(output.stdout)
        .unwrap()
        .lines()
        .map(|s| serde_json::from_str(s).unwrap())
        .collect();
    assert!(replies[..3].iter().all(|r| r["ok"] == false));
    assert_eq!(replies[3]["snapshot"]["fastForward"], false);
}

#[test]
fn missing_audio_endpoint_keeps_session_operational() {
    let mut child = Command::new(binary())
        .arg("--session")
        .env("ALSOFT_DRIVERS", "koh_missing_backend")
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    child
        .stdin
        .take()
        .unwrap()
        .write_all(b"{\"id\":1,\"command\":\"snapshot\"}\n{\"id\":2,\"command\":\"quit\"}\n")
        .unwrap();
    let output = child.wait_with_output().unwrap();
    assert!(output.status.success());
    let error = String::from_utf8_lossy(&output.stderr);
    assert!(error.contains("audio unavailable"), "{error}");
    assert_eq!(String::from_utf8(output.stdout).unwrap().lines().count(), 2);
}

#[test]
#[ignore = "requires ALSOFT_DRIVERS=null and an OpenAL library"]
fn audio_resume_reset_and_restore_remain_operational() {
    use serde_json::json;
    assert_eq!(std::env::var("ALSOFT_DRIVERS").as_deref(), Ok("null"));
    let temp = Temp::new();
    let path = temp.path("audio.gb");
    rom(&path, false, false);
    let mut client = Client::start_mode(binary(), &[], false);
    assert_eq!(
        client.request(json!({"command":"load","path":path,"paused":true}))["ok"],
        true
    );
    for _ in 0..3 {
        assert_eq!(client.request(json!({"command":"reset"}))["ok"], true);
        client.request(json!({"command":"resume"}));
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(10);
        loop {
            let reply = client.request(json!({"command":"snapshot"}));
            if reply["snapshot"]["frame"].as_u64().unwrap() >= 10 {
                break;
            }
            assert!(std::time::Instant::now() < deadline);
            std::thread::sleep(std::time::Duration::from_millis(5));
        }
        client.request(json!({"command":"pause"}));
        assert_eq!(client.request(json!({"command":"save"}))["ok"], true);
        assert_eq!(client.request(json!({"command":"restore"}))["ok"], true);
    }
    client.request(json!({"command":"quit"}));
    assert_eq!(client.finish(), 0);
    let mut error = String::new();
    std::io::Read::read_to_string(client.child.stderr.as_mut().unwrap(), &mut error).unwrap();
    assert!(!error.contains("audio unavailable"), "{error}");
}
