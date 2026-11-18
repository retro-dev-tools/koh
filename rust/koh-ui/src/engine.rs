use base64::{Engine as _, engine::general_purpose::STANDARD};
use serde::{Deserialize, Deserializer};
use serde_json::{Value, json};
use std::{
    ffi::OsString,
    io::{self, BufRead, BufReader, Read, Write},
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread,
    time::{Duration, Instant},
};

#[derive(Debug, Deserialize, PartialEq, Eq)]
pub enum Mode {
    Dmg,
    Cgb,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Cpu {
    pub pc: u16,
    pub sp: u16,
    pub a: u8,
    pub f: u8,
    pub bc: u16,
    pub de: u16,
    pub hl: u16,
    pub total_t_cycles: u64,
    pub flag_z: bool,
    pub flag_n: bool,
    pub flag_h: bool,
    pub flag_c: bool,
}

#[derive(Debug, Deserialize)]
pub struct Color {
    pub r: u8,
    pub g: u8,
    pub b: u8,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Palettes {
    pub is_cgb: bool,
    pub bgp: u8,
    pub obp0: u8,
    pub obp1: u8,
    pub bg_colors: Vec<Color>,
    pub obj_colors: Vec<Color>,
}

#[derive(Debug, Deserialize, PartialEq, Eq)]
pub struct Vram {
    #[serde(deserialize_with = "base64_bytes")]
    pub rgba: Vec<u8>,
    pub width: u32,
    pub height: u32,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Memory {
    pub base_address: u16,
    #[serde(deserialize_with = "base64_bytes")]
    pub bytes: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Snapshot {
    protocol_version: u32,
    pub rom_path: Option<String>,
    pub frame: u64,
    pub paused: bool,
    pub fast_forward: bool,
    pub mode: Option<Mode>,
    pub cpu: Option<Cpu>,
    #[serde(deserialize_with = "base64_bytes")]
    pub framebuffer: Vec<u8>,
    pub palettes: Option<Palettes>,
    pub vram: Option<Vram>,
    pub memory: Option<Memory>,
}

fn base64_bytes<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Vec<u8>, D::Error> {
    let text = String::deserialize(deserializer)?;
    STANDARD.decode(text).map_err(serde::de::Error::custom)
}

impl Snapshot {
    fn validate(&self) -> bool {
        let loaded = self.rom_path.is_some();
        let cgb = self.mode == Some(Mode::Cgb);
        self.protocol_version == 1
            && loaded == self.mode.is_some()
            && loaded == self.cpu.is_some()
            && self.framebuffer.len() == 160 * 144 * 4
            && self
                .framebuffer
                .as_chunks::<4>()
                .0
                .iter()
                .all(|pixel| pixel[3] == 255)
            && self
                .memory
                .as_ref()
                .is_none_or(|m| loaded && m.bytes.len() == 256)
            && self.vram.as_ref().is_none_or(|v| {
                loaded
                    && v.width == 128
                    && v.height == if cgb { 384 } else { 192 }
                    && v.rgba.len() == v.width as usize * v.height as usize * 4
                    && v.rgba
                        .as_chunks::<4>()
                        .0
                        .iter()
                        .all(|pixel| pixel[3] == 255)
            })
            && self.palettes.as_ref().is_none_or(|p| {
                loaded
                    && p.is_cgb == cgb
                    && p.bg_colors.len() == if cgb { 32 } else { 4 }
                    && p.obj_colors.len() == if cgb { 32 } else { 8 }
            })
    }
}

#[derive(Debug, Deserialize)]
struct Reply {
    id: u64,
    ok: bool,
    error: Option<String>,
    snapshot: Option<Snapshot>,
}

fn read_reply(reader: &mut impl BufRead, expected_id: u64) -> io::Result<Reply> {
    let mut bytes = Vec::new();
    reader.take(524289).read_until(b'\n', &mut bytes)?;
    let reply: Reply = serde_json::from_slice(&bytes).map_err(io::Error::other)?;
    if bytes.len() > 524288
        || bytes.last() != Some(&b'\n')
        || reply.id != expected_id
        || if reply.ok {
            reply.error.is_some() || !reply.snapshot.as_ref().is_some_and(Snapshot::validate)
        } else {
            reply.error.is_none() || reply.snapshot.is_some()
        }
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Invalid engine reply",
        ));
    }
    Ok(reply)
}

#[derive(Default)]
pub struct Update {
    pub snapshot: Option<Snapshot>,
    pub message: Option<Result<String, String>>,
}

enum Input {
    Request(Value),
    Inspect(bool, u16),
}

pub struct Engine {
    sender: mpsc::Sender<Input>,
    latest: Arc<Mutex<Update>>,
    alive: Arc<AtomicBool>,
    child: Arc<Mutex<Child>>,
    done: mpsc::Receiver<bool>,
}

impl Engine {
    pub fn spawn(path: &Path, arguments: &[OsString]) -> io::Result<Self> {
        let mut child = Command::new(path)
            .arg("--session")
            .args(arguments)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()?;
        let mut input = child.stdin.take().expect("piped engine stdin");
        let mut output = BufReader::new(child.stdout.take().expect("piped engine stdout"));
        let child = Arc::new(Mutex::new(child));
        let worker_child = child.clone();
        let latest = Arc::new(Mutex::new(Update::default()));
        let worker_latest = latest.clone();
        let alive = Arc::new(AtomicBool::new(true));
        let worker_alive = alive.clone();
        let (sender, receiver) = mpsc::channel();
        let (done_sender, done) = mpsc::channel();
        thread::spawn(move || {
            let mut sequence = 0u64;
            let mut debug = false;
            let mut address = 0u16;
            let mut next_poll = Instant::now();
            loop {
                let mut request = match receiver
                    .recv_timeout(next_poll.saturating_duration_since(Instant::now()))
                {
                    Ok(Input::Request(value)) => value,
                    Ok(Input::Inspect(show, base)) => {
                        debug = show;
                        address = base;
                        json!({"command": "snapshot"})
                    }
                    Err(mpsc::RecvTimeoutError::Timeout) => json!({"command": "snapshot"}),
                    Err(mpsc::RecvTimeoutError::Disconnected) => json!({"command": "quit"}),
                };
                next_poll = Instant::now() + Duration::from_millis(16);
                sequence += 1;
                request["id"] = json!(sequence);
                request["debug"] = json!(debug);
                request["address"] = json!(address);
                let command = request["command"].as_str().unwrap_or("").to_owned();
                let encoded = serde_json::to_string(&request).expect("JSON command");
                if encoded.encode_utf16().count() > 16384 {
                    worker_latest.lock().unwrap().message = Some(Err(
                        "The ROM path is too long. Move the ROM to a shorter path and try again."
                            .into(),
                    ));
                    continue;
                }
                let result = (|| -> io::Result<Reply> {
                    input.write_all(encoded.as_bytes())?;
                    input.write_all(b"\n")?;
                    input.flush()?;
                    read_reply(&mut output, sequence)
                })();
                let mut update = worker_latest.lock().unwrap();
                match result {
                    Ok(reply) => {
                        if reply.ok {
                            update.snapshot = reply.snapshot;
                            if !matches!(
                                command.as_str(),
                                "snapshot" | "joypad" | "fastForward" | "releaseInput"
                            ) {
                                update.message = Some(Ok(command.clone()));
                            }
                        } else {
                            update.message = Some(Err(reply.error.unwrap_or_default()));
                        }
                    }
                    Err(error) => {
                        eprintln!("[koh-ui] engine connection closed: {error}");
                        update.message = Some(Err(
                            "The emulator connection closed. Restart Koh to reconnect.".into(),
                        ));
                        break;
                    }
                }
                if command == "quit" {
                    break;
                }
            }
            drop(input);
            worker_alive.store(false, Ordering::Release);
            loop {
                match worker_child.lock().unwrap().try_wait() {
                    Ok(Some(status)) => {
                        let _ = done_sender.send(status.success());
                        break;
                    }
                    Ok(None) => {}
                    Err(_) => {
                        let _ = done_sender.send(false);
                        break;
                    }
                }
                thread::sleep(Duration::from_millis(5));
            }
        });
        Ok(Self {
            sender,
            latest,
            alive,
            child,
            done,
        })
    }

    pub fn send(&self, request: Value) -> bool {
        self.sender.send(Input::Request(request)).is_ok()
    }
    pub fn inspect(&self, show: bool, address: u16) {
        let _ = self.sender.send(Input::Inspect(show, address));
    }
    pub fn take_update(&self) -> Update {
        std::mem::take(&mut *self.latest.lock().unwrap())
    }
    pub fn is_alive(&self) -> bool {
        self.alive.load(Ordering::Acquire)
    }
}

impl Drop for Engine {
    fn drop(&mut self) {
        self.send(json!({"command": "quit"}));
        if !self.done.recv().unwrap_or(false) {
            let _ = self.child.lock().unwrap().wait();
            eprintln!("[koh-ui] engine exited unsuccessfully");
        }
    }
}

pub fn executable() -> io::Result<PathBuf> {
    if let Some(path) = std::env::var_os("KOH_ENGINE") {
        return Ok(path.into());
    }
    Ok(std::env::current_exe()?
        .with_file_name(format!("Koh.Emulator.App{}", std::env::consts::EXE_SUFFIX)))
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn reply() -> serde_json::Value {
        json!({
            "id": 3, "ok": true, "error": null,
            "snapshot": {
                "protocolVersion": 1, "romPath": null, "frame": 0,
                "paused": true, "fastForward": false, "mode": null,
                "cpu": null, "framebuffer": STANDARD.encode([0, 0, 0, 255].repeat(160 * 144)),
                "palettes": null, "vram": null, "memory": null
            }
        })
    }

    fn decode(value: serde_json::Value) -> io::Result<Reply> {
        read_reply(&mut io::Cursor::new(format!("{value}\n")), 3)
    }

    #[test]
    fn accepts_a_complete_engine_snapshot_and_a_recoverable_error() {
        assert!(decode(reply()).is_ok());
        assert!(
            decode(json!({"id": 3, "ok": false, "error": "Missing ROM", "snapshot": null})).is_ok()
        );
    }

    #[test]
    fn rejects_wrong_ids_versions_modes_and_image_shapes() {
        let mut value = reply();
        value["id"] = json!(4);
        assert!(decode(value).is_err());
        let mut value = reply();
        value["snapshot"]["protocolVersion"] = json!(2);
        assert!(decode(value).is_err());
        let mut value = reply();
        value["snapshot"]["framebuffer"] = json!("AA==");
        assert!(decode(value).is_err());
        let mut value = reply();
        value["snapshot"]["mode"] = json!("Unknown");
        assert!(decode(value).is_err());
    }

    #[test]
    fn rejects_unterminated_and_oversized_replies() {
        assert!(read_reply(&mut io::Cursor::new(reply().to_string()), 3).is_err());
        assert!(read_reply(&mut io::Cursor::new(vec![b' '; 524289]), 3).is_err());
    }

    #[test]
    #[ignore = "requires KOH_TEST_ENGINE pointing to a published engine"]
    fn published_engine_runs_through_the_rust_client_and_shuts_down_cleanly() {
        let executable = std::env::var_os("KOH_TEST_ENGINE").expect("set KOH_TEST_ENGINE");
        let directory = std::env::temp_dir().join(format!("koh-ui-engine-{}", std::process::id()));
        std::fs::create_dir_all(&directory).unwrap();
        let path = directory.join("game.gb");
        std::fs::write(&path, [0u8; 32768]).unwrap();
        let engine = Engine::spawn(Path::new(&executable), &["--headless".into()]).unwrap();
        let child = engine.child.clone();
        engine.send(json!({"command": "load", "path": path, "paused": true}));
        engine.inspect(true, 0xfff0);
        let deadline = std::time::Instant::now() + Duration::from_secs(10);
        loop {
            let update = engine.take_update();
            if let Some(Err(error)) = update.message {
                panic!("{error}");
            }
            if let Some(snapshot) = update.snapshot
                && let Some(memory) = snapshot.memory
            {
                assert_eq!(snapshot.rom_path.as_deref(), path.to_str());
                assert_eq!(snapshot.frame, 0);
                assert!(snapshot.paused);
                assert_eq!(memory.base_address, 0xfff0);
                assert_eq!(memory.bytes.len(), 256);
                assert_eq!(snapshot.vram.unwrap().height, 192);
                break;
            }
            assert!(std::time::Instant::now() < deadline, "snapshot timeout");
            thread::sleep(Duration::from_millis(5));
        }
        engine.send(json!({"command": "load", "path": "x".repeat(20000)}));
        loop {
            let update = engine.take_update();
            if let Some(Err(error)) = update.message {
                assert!(error.contains("shorter"), "{error}");
                break;
            }
            assert!(
                std::time::Instant::now() < deadline,
                "long path response timeout"
            );
            thread::sleep(Duration::from_millis(5));
        }
        assert!(engine.is_alive());
        engine.send(json!({"command": "save"}));
        loop {
            let update = engine.take_update();
            if let Some(message) = update.message {
                let command = message.unwrap();
                if command == "save" {
                    break;
                }
            }
            assert!(std::time::Instant::now() < deadline, "save timeout");
            thread::sleep(Duration::from_millis(5));
        }
        assert!(path.with_file_name("game.gb.state").exists());
        drop(engine);
        assert!(child.lock().unwrap().try_wait().unwrap().unwrap().success());
        std::fs::remove_dir_all(directory).unwrap();
    }
}
