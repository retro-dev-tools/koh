mod audio;
mod dap;
mod headless;
mod options;
mod persistence;
mod platform;
mod protocol;
mod snapshot;
#[cfg(test)]
mod tests;

use audio::Audio;
use koh_debugger::{Action, Debugger};
use koh_emulator::machine::Machine;
use options::Options;
use serde_json::{Value, json};
use std::{
    io::{self, BufReader, Write},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, SyncSender},
    },
    thread,
    time::{Duration, Instant},
};

enum Message {
    Session(String),
    InputEnd(bool),
    Dap(protocol::Frame),
    DapConnected(Box<dyn Write + Send>),
    DapClosed,
}

struct Host {
    options: Options,
    machine: Option<Machine>,
    debugger: Debugger,
    path: Option<String>,
    frame: u64,
    paused: bool,
    fast_forward: bool,
    audio: Option<Audio>,
    dap_writer: Option<Box<dyn Write + Send>>,
}
impl Host {
    fn new(options: Options) -> Self {
        let audio = if options.headless {
            None
        } else {
            match Audio::new() {
                Ok(a) => Some(a),
                Err(e) => {
                    eprintln!("[koh-engine] audio unavailable: {e}");
                    None
                }
            }
        };
        Self {
            options,
            machine: None,
            debugger: Debugger::default(),
            path: None,
            frame: 0,
            paused: true,
            fast_forward: false,
            audio,
            dap_writer: None,
        }
    }
    fn load(&mut self, path: &str, paused: bool) -> Result<(), String> {
        let path = options::absolute(path)?;
        let mut machine = self.options.load_machine(&path)?;
        self.flush().map_err(|e| e.to_string())?;
        persistence::load_sram(&mut machine, &path).map_err(|e| e.to_string())?;
        self.machine = Some(machine);
        self.path = Some(path);
        self.frame = 0;
        self.fast_forward = false;
        self.paused = paused;
        if let Some(audio) = &mut self.audio {
            audio.reset();
        }
        Ok(())
    }
    fn flush(&self) -> io::Result<()> {
        if let (Some(machine), Some(path)) = (&self.machine, &self.path) {
            persistence::save_sram(machine, path)?;
        }
        Ok(())
    }
    fn require_rom(&self) -> Result<&str, String> {
        self.path
            .as_deref()
            .ok_or_else(|| "Open a ROM first".into())
    }
    fn boolean(request: &Value, name: &str, default: Option<bool>) -> Result<bool, String> {
        match request.get(name) {
            Some(v) => v
                .as_bool()
                .ok_or_else(|| format!("Expected boolean {name}")),
            None => default.ok_or_else(|| format!("Missing {name}")),
        }
    }
    fn command(&mut self, request: &Value) -> Result<(Value, bool), String> {
        let debug = Self::boolean(request, "debug", Some(false))?;
        let address = match request.get("address") {
            None => 0,
            Some(v) => v
                .as_u64()
                .and_then(|v| u16::try_from(v).ok())
                .ok_or("Address must be an unsigned 16-bit integer")?,
        };
        let command = request["command"].as_str().ok_or("Missing command")?;
        match command {
            "snapshot" => {}
            "load" => {
                let path = request["path"].as_str().ok_or("Missing path")?;
                let paused = Self::boolean(request, "paused", Some(false))?;
                self.load(path, paused)?;
            }
            "reset" => {
                let path = self.require_rom()?.to_owned();
                self.load(&path, self.paused)?;
            }
            "pause" => self.paused = true,
            "resume" => {
                self.require_rom()?;
                self.paused = false;
            }
            "step" => {
                self.require_rom()?;
                if !self.paused {
                    return Err("Pause before stepping".into());
                }
                let machine = self.machine.as_mut().unwrap();
                if self.dap_writer.is_some() {
                    for event in self.debugger.step(machine, Action::StepIn) {
                        dap::send(&mut self.dap_writer, &event);
                    }
                } else {
                    machine.step_instruction();
                }
            }
            "fastForward" => self.fast_forward = Self::boolean(request, "enabled", None)?,
            "joypad" => {
                self.require_rom()?;
                let name = request["button"].as_str().ok_or("Unknown joypad button")?;
                let button = Machine::button(name).ok_or("Unknown joypad button")?;
                let down = Self::boolean(request, "down", None)?;
                let m = self.machine.as_mut().unwrap();
                if down {
                    m.joypad_press(button)
                } else {
                    m.joypad_release(button)
                }
            }
            "releaseInput" => {
                self.fast_forward = false;
                if let Some(m) = &mut self.machine {
                    m.joypad_release(255);
                }
            }
            "save" => {
                let path = format!("{}.state", self.require_rom()?);
                persistence::save_state(self.machine.as_ref().unwrap(), &path)
                    .map_err(|e| e.to_string())?;
            }
            "restore" => {
                let path = format!("{}.state", self.require_rom()?);
                persistence::restore_state(self.machine.as_mut().unwrap(), &path)
                    .map_err(|e| e.to_string())?;
                if let Some(audio) = &mut self.audio {
                    audio.reset();
                }
            }
            "quit" => self.paused = true,
            _ => return Err("Unknown engine command".into()),
        }
        Ok((
            snapshot::snapshot(
                self.machine.as_ref(),
                self.path.as_deref(),
                self.frame,
                self.paused,
                self.fast_forward,
                debug,
                address,
            ),
            command == "quit",
        ))
    }
    fn session(&mut self, line: &str) -> (Value, bool) {
        let mut id = 0i64;
        let result = (|| {
            let request = protocol::parse_request(line)?;
            id = request["id"]
                .as_i64()
                .ok_or("Command id must be a signed 64-bit integer")?;
            if id < 0 {
                return Err("Command id must be nonnegative".into());
            }
            self.command(&request)
        })();
        match result {
            Ok((snapshot, quit)) => (
                json!({"id":id,"ok":true,"error":null,"snapshot":snapshot}),
                quit,
            ),
            Err(error) => (
                json!({"id":id,"ok":false,"error":error,"snapshot":null}),
                false,
            ),
        }
    }
    fn dap(&mut self, frame: &protocol::Frame) -> bool {
        let request = match frame {
            protocol::Frame::Request(value) => value,
            protocol::Frame::InvalidJson(error) => {
                let mut response = if let Some(machine) = &mut self.machine {
                    self.debugger.handle(&Value::Null, machine).response
                } else {
                    self.debugger.handle_empty(&Value::Null).response
                };
                response["message"] = format!("invalid JSON: {error}").into();
                dap::send(&mut self.dap_writer, &response);
                return false;
            }
        };
        let reply = if let Some(machine) = &mut self.machine {
            self.debugger.handle(request, machine)
        } else {
            self.debugger.handle_empty(request)
        };
        dap::send(&mut self.dap_writer, &reply.response);
        for event in reply.events {
            dap::send(&mut self.dap_writer, &event);
        }
        match reply.action {
            Action::None => {}
            Action::Continue => self.paused = self.machine.is_none(),
            Action::Pause => self.paused = true,
            Action::Disconnect => {
                self.paused = true;
                return true;
            }
            Action::StepIn | Action::Next | Action::StepOut => {
                if let Some(machine) = &mut self.machine {
                    let events = self.debugger.step(machine, reply.action);
                    self.paused = !events.is_empty();
                    for event in events {
                        dap::send(&mut self.dap_writer, &event);
                    }
                }
            }
        }
        false
    }
    fn run(&mut self, receiver: Receiver<Message>) -> io::Result<i32> {
        let mut next_frame = Instant::now();
        let mut log = Instant::now();
        let mut logged_frames = 0;
        let mut samples = [0i16; 8192];
        let mut stdout = io::stdout().lock();
        loop {
            if platform::interrupted() {
                return Ok(0);
            }
            let wait = if self.paused || self.machine.is_none() {
                Duration::from_millis(20)
            } else {
                next_frame
                    .saturating_duration_since(Instant::now())
                    .min(Duration::from_millis(1))
            };
            match receiver.recv_timeout(wait) {
                Ok(Message::Session(line)) => {
                    let (reply, quit) = self.session(&line);
                    serde_json::to_writer(&mut stdout, &reply)?;
                    stdout.write_all(b"\n")?;
                    stdout.flush()?;
                    if quit {
                        return Ok(0);
                    }
                }
                Ok(Message::InputEnd(ok)) => return Ok(if ok { 0 } else { 1 }),
                Ok(Message::DapConnected(writer)) => self.dap_writer = Some(writer),
                Ok(Message::Dap(request)) => {
                    if self.dap(&request) {
                        return Ok(0);
                    }
                }
                Ok(Message::DapClosed) => return Ok(0),
                Err(mpsc::RecvTimeoutError::Disconnected) => return Ok(0),
                Err(mpsc::RecvTimeoutError::Timeout) => {}
            }
            if self.paused || self.machine.is_none() {
                next_frame = Instant::now();
                continue;
            }
            if !self.fast_forward && Instant::now() < next_frame {
                continue;
            }
            let started = Instant::now();
            let machine = self.machine.as_mut().unwrap();
            if let Some(event) = self.debugger.run_frame(machine) {
                self.paused = true;
                dap::send(&mut self.dap_writer, &event);
            }
            self.frame = self.frame.wrapping_add(1);
            let available = machine.apu.sample_buffer.drain(&mut samples);
            let mut buffered = 0;
            if available > 0
                && let Some(audio) = &mut self.audio
            {
                buffered = audio.push(&samples[..available]);
            }
            let delay = if self.audio.is_none() {
                Duration::from_secs_f64(70224.0 / 4194304.0)
            } else if buffered > 3072 {
                Duration::from_secs_f64((buffered - 2048) as f64 / 44100.0)
            } else {
                Duration::ZERO
            };
            next_frame = if self.audio.is_none() {
                started
            } else {
                Instant::now()
            } + delay;
            if log.elapsed() > Duration::from_secs(1) {
                if let Some(audio) = &self.audio {
                    eprintln!(
                        "[pace] fps={:.1} buf[last={buffered}] pushes={} samples={} underruns={}",
                        (self.frame as i128 - logged_frames as i128) as f64
                            / log.elapsed().as_secs_f64(),
                        audio.pushes,
                        audio.samples,
                        audio.underruns
                    );
                }
                log = Instant::now();
                logged_frames = self.frame;
            }
        }
    }
}

fn stdin_thread(sender: SyncSender<Message>) {
    thread::spawn(move || {
        let mut input = BufReader::new(io::stdin());
        loop {
            match protocol::command_line(&mut input) {
                Ok(Some(line)) => {
                    if sender.send(Message::Session(line)).is_err() {
                        return;
                    }
                }
                Ok(None) => {
                    let _ = sender.send(Message::InputEnd(true));
                    return;
                }
                Err(e) => {
                    eprintln!("[koh-engine] {e}");
                    let _ = sender.send(Message::InputEnd(false));
                    return;
                }
            }
        }
    });
}

fn execute() -> Result<i32, String> {
    let _runtime = platform::Runtime::new();
    let options = Options::parse()?;
    if options.session
        && (options.positional.len() > 1
            || options.screenshot.is_some()
            || options.report
            || options.input.is_some())
    {
        return Err(
            "--session accepts one ROM and cannot use screenshot, report or input-script modes"
                .into(),
        );
    }
    if !options.session {
        if options.positional.len() != 1 {
            return Err("Use koh-emulator for the desktop, or pass one ROM with --session, --dap, --screenshot or --mode3-report.".into());
        }
        if options.screenshot.is_some() || options.report {
            return headless::run(&options).map(|_| 0);
        }
        if options.dap.is_none() {
            return Err("Use koh-emulator for the desktop, or select --session, --dap, --screenshot or --mode3-report.".into());
        }
    }
    let mut host = Host::new(options);
    if let Some(path) = host.options.positional.first().cloned() {
        host.load(&path, host.options.dap.is_some())?;
    }
    let (sender, receiver) = mpsc::sync_channel(32);
    let cancel = Arc::new(AtomicBool::new(false));
    let _listener = if let Some(name) = host.options.dap.clone() {
        Some(dap::listen(name, sender.clone(), cancel.clone()).map_err(|e| e.to_string())?)
    } else {
        None
    };
    if host.options.session {
        stdin_thread(sender.clone());
    }
    drop(sender);
    let result = host.run(receiver);
    cancel.store(true, Ordering::Relaxed);
    let flush = host.flush();
    result
        .and_then(|code| flush.map(|_| code))
        .map_err(|e| e.to_string())
}
fn main() {
    let code = match execute() {
        Ok(code) => code,
        Err(e) => {
            eprintln!("[koh-engine] {e}");
            1
        }
    };
    std::process::exit(code);
}
