use serde_json::Value;
use std::{
    io::{self, BufRead, BufReader, Read, Write},
    path::PathBuf,
    process::{Child, Command, Stdio},
    sync::mpsc::{self, Receiver, Sender},
    thread::{self, JoinHandle},
    time::Duration,
};

pub struct Worker {
    child: Child,
    requests: Option<Sender<Value>>,
    responses: Receiver<io::Result<Value>>,
    exchange: Option<JoinHandle<()>>,
    timeout: Duration,
    stopped: bool,
}

impl Worker {
    pub fn assembler() -> io::Result<Self> {
        let mut command = Command::new(executable()?);
        command.arg("--serve-json");
        Self::start(command, Duration::from_secs(60))
    }

    pub fn start(mut command: Command, timeout: Duration) -> io::Result<Self> {
        let mut child = command
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::inherit())
            .spawn()?;
        let mut input = child.stdin.take().unwrap();
        let mut output = BufReader::new(child.stdout.take().unwrap());
        let (requests, incoming) = mpsc::channel::<Value>();
        let (replies, responses) = mpsc::channel();
        let exchange = thread::spawn(move || {
            for request in incoming {
                let result = (|| {
                    serde_json::to_writer(&mut input, &request)?;
                    input.write_all(b"\n")?;
                    input.flush()?;
                    let mut line = Vec::new();
                    let size = (&mut output)
                        .take(64 * 1024 * 1024 + 1)
                        .read_until(b'\n', &mut line)?;
                    if size == 0 {
                        return Err(io::Error::new(
                            io::ErrorKind::UnexpectedEof,
                            "assembler closed its response stream",
                        ));
                    }
                    if size > 64 * 1024 * 1024 || line.last() != Some(&b'\n') {
                        return Err(io::Error::new(
                            io::ErrorKind::InvalidData,
                            "invalid assembler response framing",
                        ));
                    }
                    let response: Value = serde_json::from_slice(&line)?;
                    if response["version"] != 1 {
                        return Err(io::Error::new(
                            io::ErrorKind::InvalidData,
                            "unsupported assembler response version",
                        ));
                    }
                    if !response["ok"].is_boolean()
                        || ["documents", "symbols", "diagnostics"]
                            .iter()
                            .any(|field| !response[field].is_array())
                    {
                        return Err(io::Error::new(
                            io::ErrorKind::InvalidData,
                            "invalid assembler response fields",
                        ));
                    }
                    Ok(response)
                })();
                let failed = result.is_err();
                if replies.send(result).is_err() || failed {
                    break;
                }
            }
        });
        Ok(Self {
            child,
            requests: Some(requests),
            responses,
            exchange: Some(exchange),
            timeout,
            stopped: false,
        })
    }

    pub fn request(&mut self, request: &Value) -> io::Result<Value> {
        let result = (|| {
            self.requests
                .as_ref()
                .ok_or_else(|| io::Error::new(io::ErrorKind::BrokenPipe, "assembler stopped"))?
                .send(request.clone())
                .map_err(|_| {
                    io::Error::new(io::ErrorKind::BrokenPipe, "assembler exchange stopped")
                })?;
            self.responses
                .recv_timeout(self.timeout)
                .map_err(|error| match error {
                    mpsc::RecvTimeoutError::Timeout => io::Error::new(
                        io::ErrorKind::TimedOut,
                        "assembler request exceeded its deadline",
                    ),
                    mpsc::RecvTimeoutError::Disconnected => {
                        io::Error::new(io::ErrorKind::BrokenPipe, "assembler exchange stopped")
                    }
                })?
        })();
        if result.is_err() {
            self.stop();
        }
        result
    }

    fn stop(&mut self) {
        if self.stopped {
            return;
        }
        self.stopped = true;
        self.requests.take();
        let _ = self.child.kill();
        let _ = self.child.wait();
        if let Some(exchange) = self.exchange.take() {
            let _ = exchange.join();
        }
    }
}

impl Drop for Worker {
    fn drop(&mut self) {
        self.stop();
    }
}

fn executable() -> io::Result<PathBuf> {
    if let Some(path) = std::env::var_os("KOH_ASM").filter(|p| !p.is_empty()) {
        return Ok(path.into());
    }
    let name = if cfg!(windows) {
        "koh-asm.exe"
    } else {
        "koh-asm"
    };
    let current = std::env::current_exe()?;
    if let Some(parent) = current.parent() {
        let sibling = parent.join(name);
        if sibling.is_file() {
            return Ok(sibling);
        }
        for ancestor in parent.ancestors() {
            if !ancestor.join("Cargo.toml").is_file() {
                continue;
            }
            for profile in ["debug", "release"] {
                let candidate = ancestor.join("target").join(profile).join(name);
                if candidate.is_file() {
                    return Ok(candidate);
                }
            }
        }
    }
    Ok(name.into())
}
