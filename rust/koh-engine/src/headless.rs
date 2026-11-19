use crate::options::Options;
use koh_debugger::metadata::DebugInfo;
use koh_emulator::{
    machine::{Machine, MemoryHook},
    ppu::PpuMode,
};
use std::{
    collections::BTreeMap,
    fs,
    io::BufWriter,
    path::Path,
    sync::{Arc, Mutex},
};

#[derive(Clone, Copy)]
struct Violation {
    address: u16,
    pc: u16,
}
struct Guard {
    events: Arc<Mutex<Vec<Violation>>>,
    lcd_on: bool,
}
impl MemoryHook for Guard {
    fn on_read(&mut self, _: u16, _: u8, _: u16, _: u8, _: i32, _: PpuMode) {}
    fn on_write(&mut self, address: u16, value: u8, pc: u16, _: u8, _: i32, mode: PpuMode) {
        if self.lcd_on && mode == PpuMode::Drawing && (0x8000..0xa000).contains(&address) {
            self.events.lock().unwrap().push(Violation { address, pc });
        }
        if address == 0xff40 {
            self.lcd_on = value & 0x80 != 0;
        }
    }
}
struct Input {
    frame: i32,
    button: u8,
    press: bool,
}
fn input_script(path: Option<&str>) -> Result<Vec<Input>, String> {
    let mut actions = vec![];
    if let Some(path) = path {
        for raw in fs::read_to_string(path).map_err(|e| e.to_string())?.lines() {
            let line = raw.trim();
            if line.is_empty() || line.starts_with('#') {
                continue;
            }
            let parts: Vec<&str> = line.split(':').collect();
            if parts.len() != 3 {
                return Err(format!("Invalid input script line: '{raw}'"));
            }
            let frame = parts[0]
                .trim()
                .parse()
                .map_err(|_| format!("Invalid input frame: '{raw}'"))?;
            let button = if let Ok(v) = parts[1].trim().parse::<u8>() {
                v
            } else {
                let mut value = 0;
                for token in parts[1].split(',') {
                    let name = token.trim();
                    if name.eq_ignore_ascii_case("None") {
                        continue;
                    }
                    value |= Machine::BUTTONS
                        .iter()
                        .find(|(v, _)| v.eq_ignore_ascii_case(name))
                        .ok_or_else(|| format!("Unknown joypad button: {name}"))?
                        .1;
                }
                value
            };
            let press = parts[2].trim().eq_ignore_ascii_case("press");
            if !press && !parts[2].trim().eq_ignore_ascii_case("release") {
                return Err(format!("Invalid press/release token: '{raw}'"));
            }
            actions.push(Input {
                frame,
                button,
                press,
            });
        }
    }
    actions.sort_by_key(|a| a.frame);
    Ok(actions)
}
fn location(info: &DebugInfo, machine: &Machine, pc: u16) -> String {
    let bank = koh_debugger::bank(machine, pc);
    let symbol = |bank| {
        info.symbols
            .iter()
            .filter(|s| s.kind != 1 && s.bank.unwrap_or(0) == bank as u32 && s.value <= pc as i32)
            .max_by_key(|s| s.value)
            .map(|s| s.name.as_str())
    };
    let name = symbol(bank).or_else(|| if bank != 0 { symbol(0) } else { None });
    let line = info
        .line(bank, pc)
        .or_else(|| if bank != 0 { info.line(0, pc) } else { None });
    let file = |path: &str| {
        Path::new(path)
            .file_name()
            .unwrap_or_default()
            .to_string_lossy()
            .into_owned()
    };
    match (name, line) {
        (Some(name), Some(line)) => format!("{name} ({}:{})", file(&line.file), line.line),
        (None, Some(line)) => format!("{}:{}", file(&line.file), line.line),
        (Some(name), None) => format!("{name} (no line info)"),
        _ => format!("${pc:04X}"),
    }
}
fn report(machine: &Machine, events: &[Violation], path: &str) -> Result<String, String> {
    let regions = [
        "$8000-$97FF (tile data)",
        "$9800-$9BFF (BG tilemap)",
        "$9C00-$9FFF (window tilemap)",
    ];
    let mut counts = [0; 3];
    for event in events {
        counts[if event.address < 0x9800 {
            0
        } else if event.address < 0x9c00 {
            1
        } else {
            2
        }] += 1;
    }
    let mut text = format!(
        "Mode-3 write violations: {} total\n\nBy region:\n",
        events.len()
    );
    for (region, count) in regions.into_iter().zip(counts) {
        text.push_str(&format!("  {region}: {count}\n"));
    }
    text.push_str("\nBy source location:\n");
    let mut info = DebugInfo::default();
    let elf = Path::new(path).with_extension("elf");
    if elf.exists() {
        info.load_path(&elf)?;
    }
    let mut groups: Vec<(String, usize)> = vec![];
    let mut indices = BTreeMap::new();
    for event in events {
        let key = location(&info, machine, event.pc);
        let index = *indices.entry(key.clone()).or_insert_with(|| {
            groups.push((key, 0));
            groups.len() - 1
        });
        groups[index].1 += 1;
    }
    groups.sort_by_key(|g| std::cmp::Reverse(g.1));
    for (name, count) in groups {
        text.push_str(&format!("  {count}  {name}\n"));
    }
    Ok(text)
}
pub fn run(options: &Options) -> Result<(), String> {
    if let Some(path) = &options.screenshot
        && !Path::new(path)
            .extension()
            .is_some_and(|e| e.eq_ignore_ascii_case("png"))
    {
        return Err("Screenshots require a .png output path.".into());
    }
    let path = &options.positional[0];
    let mut machine = options.load_machine(path)?;
    let events = Arc::new(Mutex::new(vec![]));
    if options.report {
        machine.hook = Some(Box::new(Guard {
            events: events.clone(),
            lcd_on: machine.ppu.lcdc & 0x80 != 0,
        }));
    }
    let actions = input_script(options.input.as_deref())?;
    let mut index = 0;
    for frame in 0..options.frames {
        while index < actions.len() && actions[index].frame == frame {
            let action = &actions[index];
            if action.press {
                machine.joypad_press(action.button)
            } else {
                machine.joypad_release(action.button)
            }
            index += 1;
        }
        machine.run_frame();
    }
    if let Some(path) = &options.screenshot {
        let output = BufWriter::new(fs::File::create(path).map_err(|e| e.to_string())?);
        let mut encoder = png::Encoder::new(output, 160, 144);
        encoder.set_color(png::ColorType::Rgba);
        encoder.set_depth(png::BitDepth::Eight);
        let mut writer = encoder.write_header().map_err(|e| e.to_string())?;
        writer
            .write_image_data(&machine.ppu.framebuffer)
            .map_err(|e| e.to_string())?;
        writer.finish().map_err(|e| e.to_string())?;
    }
    if options.report {
        let text = report(&machine, &events.lock().unwrap(), path)?;
        if let Some(path) = &options.report_path {
            fs::write(path, text).map_err(|e| e.to_string())?;
        } else {
            println!("{text}");
        }
    }
    Ok(())
}
