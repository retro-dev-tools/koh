use koh_emulator::{
    HardwareMode,
    machine::{MemoryContext, MemoryHook},
};
use koh_emulator::{machine::Machine, ppu::PpuMode};
use std::sync::{Arc, Mutex};
use std::{io::BufWriter, path::Path};

pub type Result<T = ()> = std::result::Result<T, String>;
pub fn hex(bytes: &[u8]) -> String {
    use std::fmt::Write;
    let mut out = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        write!(out, "{b:02X}").unwrap();
    }
    out
}
pub fn unhex(value: &str) -> Result<Vec<u8>> {
    if !value.len().is_multiple_of(2) {
        return Err("Odd hex length".into());
    }
    value
        .as_bytes()
        .as_chunks::<2>()
        .0
        .iter()
        .map(|pair| {
            u8::from_str_radix(std::str::from_utf8(pair).map_err(|e| e.to_string())?, 16)
                .map_err(|e| e.to_string())
        })
        .collect()
}
pub fn number(value: &str) -> Result<u16> {
    u16::from_str_radix(value, 16).map_err(|e| e.to_string())
}
pub fn load(path: &str) -> Result<Machine> {
    Machine::load_rom(&std::fs::read(path).map_err(|e| e.to_string())?)
}
pub fn read(m: &Machine, address: u16, count: usize) -> Vec<u8> {
    (0..count)
        .map(|i| m.read_debug(address.wrapping_add(i as u16)))
        .collect()
}
pub fn png(m: &Machine, path: impl AsRef<Path>) -> Result {
    let mut e = png::Encoder::new(
        BufWriter::new(std::fs::File::create(path).map_err(|e| e.to_string())?),
        160,
        144,
    );
    e.set_color(png::ColorType::Rgba);
    e.set_depth(png::BitDepth::Eight);
    let mut w = e.write_header().map_err(|e| e.to_string())?;
    w.write_image_data(&m.ppu.framebuffer)
        .map_err(|e| e.to_string())?;
    w.finish().map_err(|e| e.to_string())
}
pub fn write(path: impl AsRef<Path>, bytes: impl AsRef<[u8]>) -> Result {
    std::fs::write(path, bytes).map_err(|e| e.to_string())
}
pub fn button(name: &str) -> Result<u8> {
    match name {
        "Right" => Ok(1),
        "Left" => Ok(2),
        "Up" => Ok(4),
        "Down" => Ok(8),
        "A" => Ok(16),
        "B" => Ok(32),
        "Select" => Ok(64),
        "Start" => Ok(128),
        _ => Err(format!("Unknown button {name}")),
    }
}
pub fn mode_name(mode: PpuMode) -> &'static str {
    match mode {
        PpuMode::HBlank => "HBlank",
        PpuMode::VBlank => "VBlank",
        PpuMode::OamScan => "OamScan",
        PpuMode::Drawing => "Drawing",
    }
}
pub fn yes(value: bool) -> u8 {
    value as u8
}
#[derive(Clone, Default)]
pub struct Trace {
    pub context: MemoryContext,
    pub violations: Vec<(u16, u8, u8, u16)>,
    pub reads: Vec<String>,
    pub writes: Vec<String>,
    pub cells: Vec<(u16, u8, u8, i32)>,
    pub source_reads: usize,
    pub ly_reads: usize,
    pub last_blank: u64,
    pub unsafe_dma: Option<String>,
    pub oam_starts: usize,
    pub gdma_starts: usize,
    pub video_writes: Vec<String>,
    pub active: bool,
    pub final_ime: Option<bool>,
    pub loop_count: usize,
    pub loop_start: u64,
    pub loop_end: u64,
    pub initialized: Option<u64>,
    pub dma_starts: Vec<(u8, u16, u8)>,
    pub scroll_lines: Vec<(u64, u8, u8)>,
    pub scroll_writes: Vec<(u64, u8, u16, u8, u8)>,
    pub last_line: u8,
    pub last_read: u64,
}
#[derive(Clone, Copy, Default)]
pub struct TraceOptions {
    pub video: bool,
    pub guard_oam: bool,
    pub guard_palettes: bool,
    pub guard_palette_ime: bool,
    pub wrap: bool,
    pub wrap_reads: bool,
    pub raster: bool,
    pub hal: bool,
    pub observe: bool,
    pub loop_timing: bool,
    pub vblank: bool,
    pub cube: bool,
    pub scroll: bool,
    pub violation_trace: bool,
}
pub struct Hook {
    pub trace: Arc<Mutex<Trace>>,
    pub options: TraceOptions,
    is_cgb: bool,
}
impl Hook {
    pub fn attach(m: &mut Machine, options: TraceOptions) -> Arc<Mutex<Trace>> {
        let trace = Arc::new(Mutex::new(Trace::default()));
        if options.video
            || options.wrap
            || options.raster
            || options.hal
            || options.observe
            || options.loop_timing
            || options.vblank
            || options.cube
            || options.scroll
            || options.violation_trace
        {
            m.hook = Some(Box::new(Self {
                trace: trace.clone(),
                options,
                is_cgb: m.mode == HardwareMode::Cgb,
            }));
        }
        trace
    }
}
impl MemoryHook for Hook {
    fn set_context(&mut self, context: MemoryContext) {
        self.trace.lock().unwrap().context = context;
    }
    fn on_read(&mut self, address: u16, value: u8, _pc: u16, _ly: u8, _dot: i32, _mode: PpuMode) {
        let mut t = self.trace.lock().unwrap();
        let c = t.context;
        if self.options.violation_trace && address == 0xff44 {
            t.last_line = value;
            t.last_read = c.system_ticks;
        }
        if self.options.video && address == 0xff44 && value == 144 {
            t.last_blank = c.system_ticks;
        }
        if self.options.vblank {
            if address == 0xff44 {
                t.ly_reads += 1;
            }
            if (0x2000..0x2200).contains(&address) {
                t.source_reads += 1;
            }
        }
        if self.options.wrap
            && self.options.wrap_reads
            && t.active
            && (address >= 0xfffe || address <= 2)
        {
            t.reads.push(format!("{address:04X}:{value:02X}"));
        }
        if self.options.raster && t.active && (0xd100..0xd190).contains(&address) {
            t.reads.push(format!("{address:04X}:{value:02X}"));
        }
        if self.options.observe
            && t.reads.len() < 256
            && ((0xc020..=0xc4ff).contains(&address) || matches!(address, 0xff80 | 0x9800 | 0xff44))
        {
            t.reads.push(format!("R{address:04X}"));
        }
    }
    fn on_write(&mut self, address: u16, value: u8, pc: u16, ly: u8, dot: i32, mode: PpuMode) {
        let mut t = self.trace.lock().unwrap();
        let c = t.context;
        if self.options.video {
            if (0x8000..0xa000).contains(&address) && c.lcdc & 128 != 0 && mode == PpuMode::Drawing
            {
                t.violations.push((address, value, ly, pc));
                if self.options.violation_trace && t.violations.len() <= 16 {
                    eprintln!(
                        "mode3 {} {pc:04X} {address:04X} {ly} check={} elapsed={}",
                        c.rom_bank,
                        t.last_line,
                        c.system_ticks - t.last_read
                    );
                }
            }
            assert!(
                !(self.options.guard_palette_ime && matches!(address, 0xff69 | 0xff6b) && c.ime),
                "Palette write with IME enabled"
            );
            assert!(
                !(self.options.guard_palettes
                    && matches!(address, 0xff69 | 0xff6b)
                    && c.lcdc & 128 != 0
                    && mode == PpuMode::Drawing),
                "Mode-3 palette write"
            );
            if address == 0xff46 {
                assert!(
                    !self.options.guard_oam || (!c.ime && (0xff80..=0xfffe).contains(&pc)),
                    "OAM DMA trigger outside HRAM or with IME enabled"
                );
                t.oam_starts += 1;
            }
            if address == 0xff55 && value & 128 == 0 {
                t.gdma_starts += 1;
                if c.lcdc & 128 != 0 && !self.options.hal {
                    let duration = ((value & 127) as i32 + 1) * 32;
                    let remaining = (154 - ly as i32) * 456 - dot;
                    if ly < 144 || duration > remaining {
                        let elapsed = c.system_ticks - t.last_blank;
                        t.unsafe_dma.get_or_insert_with(||format!("GDMA outside safe vblank: LY={ly},dot={dot},duration={duration},after144={}",elapsed));
                    }
                }
            }
            if (0x8000..0xa000).contains(&address) && c.lcdc & 128 != 0 && t.video_writes.len() < 64
            {
                let elapsed = c.system_ticks - t.last_blank;
                t.video_writes.push(format!(
                    "{address:04X}@LY{ly}/mode{}/after144={elapsed}",
                    mode_name(mode)
                ));
            }
        }
        if self.options.vblank && (0x8000..0xa000).contains(&address) {
            t.cells.push((address, value, ly, dot));
        }
        if self.options.hal && matches!(address, 0xff4f | 0xff68 | 0xff69) {
            t.writes.push(format!(
                "{address:04X}:{value:02X}:{}:{}",
                yes(c.lcdc & 128 != 0),
                mode as u8
            ));
        }
        if self.options.wrap {
            if address == 0xff47 {
                if value == 0x42 {
                    t.active = true;
                }
                if value == 0x43 {
                    t.active = false;
                }
            }
            if t.active && (address >= 0xfffe || address <= 2) {
                t.writes.push(format!("{address:04X}:{value:02X}"));
            }
        }
        if self.options.raster {
            if address == 0xff47 {
                if t.active && value == 0x43 {
                    t.final_ime = Some(c.ime);
                }
                t.active = value == 0x42;
            }
            if t.active && address == 0xff43 {
                t.writes.push(format!("{ly}:{dot}:{}:{value}", mode as u8));
            }
        }
        if self.options.observe
            && t.reads.len() < 256
            && ((0xc020..=0xc4ff).contains(&address) || matches!(address, 0xff80 | 0x9800))
        {
            t.reads.push(format!("W{address:04X}:{value:02X}"));
        }
        if self.options.loop_timing {
            if t.loop_count == 1 && address == 0xc11f && value == 31 && t.initialized.is_none() {
                t.initialized = Some(c.tcycles);
            }
            if address == 0xff81 {
                assert!(
                    t.loop_count < 2 && value as usize == t.loop_count + 1,
                    "Invalid loop timing marker order"
                );
                if t.loop_count == 0 {
                    t.loop_start = c.tcycles;
                } else {
                    t.loop_end = c.tcycles;
                }
                t.loop_count += 1;
            }
        }
        if self.options.cube
            && self.is_cgb
            && address == 0xff55
            && !c.boot_mapped
            && c.lcdc & 128 != 0
        {
            t.dma_starts.push((ly, dot as u16, value));
        }
        if self.options.scroll && address == 0xff43 && !c.boot_mapped && c.lcdc & 128 != 0 {
            t.scroll_writes
                .push((c.system_ticks, ly, dot as u16, mode as u8, value));
        }
    }
    fn on_hblank(&mut self, context: MemoryContext, ly: u8, _dot: i32) {
        if self.options.scroll && !context.boot_mapped {
            self.trace
                .lock()
                .unwrap()
                .scroll_lines
                .push((context.system_ticks, ly, context.scx));
        }
    }
}
pub fn boot(m: &mut Machine) -> Result {
    m.load_boot_rom(if m.mode == HardwareMode::Cgb {
        koh_emulator::boot::CGB_BOOT.to_vec()
    } else {
        koh_emulator::boot::DMG_BOOT.to_vec()
    })
}
pub fn frame(m: &mut Machine) -> Result {
    if m.run_frame().reason != koh_emulator::machine::StopReason::FrameComplete {
        return Err("Cannot complete a hardware frame".into());
    }
    Ok(())
}
pub fn word(m: &Machine, address: u16) -> u16 {
    u16::from_le_bytes([m.read_debug(address), m.read_debug(address.wrapping_add(1))])
}
pub fn joined(values: &[String]) -> String {
    if values.is_empty() {
        "-".into()
    } else {
        values.join(",")
    }
}
