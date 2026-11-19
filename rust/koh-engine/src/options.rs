use koh_emulator::{HardwareMode, machine::Machine};
use std::{fs, path::Path};

#[derive(Default)]
pub struct Options {
    pub session: bool,
    pub headless: bool,
    pub dap: Option<String>,
    pub screenshot: Option<String>,
    pub frames: i32,
    pub input: Option<String>,
    pub report: bool,
    pub report_path: Option<String>,
    pub boot: Option<String>,
    pub boot_dmg: Option<String>,
    pub boot_cgb: Option<String>,
    pub positional: Vec<String>,
}
impl Options {
    pub fn parse() -> Result<Self, String> {
        let mut o = Self {
            frames: 120,
            ..Self::default()
        };
        for arg in std::env::args().skip(1) {
            if arg == "--session" {
                o.session = true;
            } else if arg == "--headless" {
                o.headless = true;
            } else if arg == "--mode3-report" {
                o.report = true;
            } else if let Some(v) = arg.strip_prefix("--boot-rom=") {
                o.boot = Some(v.into());
            } else if let Some(v) = arg.strip_prefix("--boot-rom-dmg=") {
                o.boot_dmg = Some(v.into());
            } else if let Some(v) = arg.strip_prefix("--boot-rom-cgb=") {
                o.boot_cgb = Some(v.into());
            } else if let Some(v) = arg.strip_prefix("--dap=") {
                o.dap = Some(v.into());
            } else if let Some(v) = arg.strip_prefix("--screenshot=") {
                o.screenshot = Some(v.into());
            } else if let Some(v) = arg.strip_prefix("--frames=") {
                o.frames = v.parse().map_err(|_| "Invalid frame count")?;
            } else if let Some(v) = arg.strip_prefix("--input=") {
                o.input = Some(v.into());
            } else if let Some(v) = arg.strip_prefix("--mode3-report=") {
                o.report = true;
                o.report_path = Some(v.into());
            } else {
                o.positional.push(arg);
            }
        }
        Ok(o)
    }
    pub fn boot_rom(&self, mode: HardwareMode) -> Result<Vec<u8>, String> {
        let path = self.boot.as_ref().or(if mode == HardwareMode::Cgb {
            self.boot_cgb.as_ref()
        } else {
            self.boot_dmg.as_ref()
        });
        if let Some(path) = path {
            fs::read(path).map_err(|e| format!("Boot ROM '{path}': {e}"))
        } else {
            Ok(if mode == HardwareMode::Cgb {
                koh_emulator::boot::CGB_BOOT.to_vec()
            } else {
                koh_emulator::boot::DMG_BOOT.to_vec()
            })
        }
    }
    pub fn load_machine(&self, path: &str) -> Result<Machine, String> {
        let length = fs::metadata(path).map_err(|e| e.to_string())?.len();
        if !(0x150..=8 * 1024 * 1024).contains(&length) {
            return Err("ROM size must be between 336 bytes and 8 MiB".into());
        }
        let bytes = fs::read(path).map_err(|e| e.to_string())?;
        if !(0x150..=8 * 1024 * 1024).contains(&bytes.len()) {
            return Err("ROM size must be between 336 bytes and 8 MiB".into());
        }
        let mut m = Machine::load_rom(&bytes)?;
        m.load_boot_rom(self.boot_rom(m.mode)?)?;
        Ok(m)
    }
}
pub fn absolute(path: &str) -> Result<String, String> {
    let path = Path::new(path);
    let path = if path.is_absolute() {
        path.to_path_buf()
    } else {
        std::env::current_dir()
            .map_err(|e| e.to_string())?
            .join(path)
    };
    Ok(path
        .components()
        .fold(std::path::PathBuf::new(), |mut result, component| {
            match component {
                std::path::Component::CurDir => {}
                std::path::Component::ParentDir => {
                    result.pop();
                }
                other => result.push(other.as_os_str()),
            }
            result
        })
        .to_string_lossy()
        .into_owned())
}
pub fn button(name: &str) -> Option<u8> {
    match name {
        "Right" => Some(1),
        "Left" => Some(2),
        "Up" => Some(4),
        "Down" => Some(8),
        "A" => Some(16),
        "B" => Some(32),
        "Select" => Some(64),
        "Start" => Some(128),
        _ => None,
    }
}
