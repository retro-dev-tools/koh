use crate::{
    HardwareMode, Interrupts,
    apu::Apu,
    boot::BootRom,
    cartridge::Cartridge,
    cpu::{Cpu, CpuBus},
    dma::{Hdma, OamDma},
    ppu::{Ppu, PpuMode},
    serial::Serial,
    state::{StateReader, StateWriter},
    timer::Timer,
};
use std::io;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum StopReason {
    FrameComplete,
    InstructionComplete,
    TCycleComplete,
    Breakpoint,
    Watchpoint,
    HaltedBySystem,
    StopRequested,
    BudgetExceeded,
    Spinning,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct StepResult {
    pub reason: StopReason,
    pub tcycles_ran: u64,
    pub final_pc: u16,
}
#[derive(Clone, Copy, Default)]
pub struct StopCondition {
    pub kind: u32,
    pub pc_equals: u16,
    pub pc_range_start: u16,
    pub pc_range_end: u16,
    pub max_tcycles: u64,
    pub bank_filter: u8,
    pub spin_pc_set_size: usize,
    pub spin_stable_instructions: usize,
}
impl StopCondition {
    pub const PC_EQUALS: u32 = 1;
    pub const PC_IN_RANGE: u32 = 2;
    pub const PC_LEAVES_RANGE: u32 = 4;
    pub const MAX_CYCLES: u32 = 8;
    pub const VBLANK: u32 = 16;
    pub const RETURN: u32 = 32;
    pub const SPINNING: u32 = 64;
    pub fn at_pc(pc: u16) -> Self {
        Self {
            kind: Self::PC_EQUALS,
            pc_equals: pc,
            bank_filter: 255,
            ..Self::default()
        }
    }
    pub fn while_pc_in_range(start: u16, end: u16) -> Self {
        Self {
            kind: Self::PC_LEAVES_RANGE,
            pc_range_start: start,
            pc_range_end: end,
            bank_filter: 255,
            ..Self::default()
        }
    }
    pub fn spinning_or_budget(max_tcycles: u64) -> Self {
        Self {
            kind: Self::SPINNING | Self::MAX_CYCLES,
            max_tcycles,
            ..Self::default()
        }
    }
}
pub trait MemoryHook: Send {
    fn set_context(&mut self, _context: MemoryContext) {}
    fn on_hblank(&mut self, _context: MemoryContext, _ly: u8, _dot: i32) {}
    fn on_read(&mut self, address: u16, value: u8, pc: u16, ly: u8, dot: i32, mode: PpuMode);
    fn on_write(&mut self, address: u16, value: u8, pc: u16, ly: u8, dot: i32, mode: PpuMode);
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct MemoryContext {
    pub ime: bool,
    pub tcycles: u64,
    pub system_ticks: u64,
    pub lcdc: u8,
    pub scx: u8,
    pub boot_mapped: bool,
    pub rom_bank: u8,
}
pub struct Machine {
    pub cpu: Cpu,
    pub mode: HardwareMode,
    pub cartridge: Cartridge,
    pub ppu: Ppu,
    pub apu: Apu,
    pub timer: Timer,
    pub serial: Serial,
    pub interrupts: Interrupts,
    pub joypad: u8,
    pub system_ticks: u64,
    pub frame_system_ticks: u64,
    pub double_speed: bool,
    pub switch_armed: bool,
    pub oam_dma: OamDma,
    pub hdma: Hdma,
    pub vram: Vec<u8>,
    pub wram: Vec<u8>,
    pub oam: Vec<u8>,
    pub hram: Vec<u8>,
    pub io: [u8; 128],
    pub vram_bank: u8,
    pub wram_bank: u8,
    pub hook: Option<Box<dyn MemoryHook>>,
    pub(crate) boot: Option<BootRom>,
    pub(crate) clock_double_speed: bool,
    pub(crate) active_pc: u16,
    pub(crate) cpu_active: bool,
    pub(crate) active_ime: bool,
    pub(crate) active_tcycles: u64,
    pub stop_requested: Option<StopReason>,
    pub breakpoint_checker: Option<Box<dyn FnMut(u16) -> bool + Send>>,
}
impl Machine {
    pub const SYSTEM_TICKS_PER_FRAME: u64 = 70224;
    pub fn new(cartridge: Cartridge, mode: Option<HardwareMode>) -> Self {
        let mode = mode.unwrap_or(if cartridge.header.cgb_flag {
            HardwareMode::Cgb
        } else {
            HardwareMode::Dmg
        });
        Self {
            cpu: Cpu::default(),
            mode,
            cartridge,
            ppu: Ppu::new(mode),
            apu: Apu::new(mode),
            timer: Timer::default(),
            serial: Serial::default(),
            interrupts: Interrupts::default(),
            joypad: 0,
            system_ticks: 0,
            frame_system_ticks: 0,
            double_speed: false,
            switch_armed: false,
            oam_dma: OamDma::default(),
            hdma: Hdma::default(),
            vram: vec![255; 0x4000],
            wram: vec![255; 0x8000],
            oam: vec![255; 0xa0],
            hram: vec![255; 0x7f],
            io: [0; 128],
            vram_bank: 0,
            wram_bank: 1,
            hook: None,
            boot: None,
            clock_double_speed: false,
            active_pc: 0,
            cpu_active: false,
            active_ime: false,
            active_tcycles: 0,
            stop_requested: None,
            breakpoint_checker: None,
        }
    }
    pub fn load_rom(rom: &[u8]) -> Result<Self, String> {
        Ok(Self::new(Cartridge::load(rom)?, None))
    }
    pub fn load_boot_rom(&mut self, bytes: Vec<u8>) -> Result<(), String> {
        let boot = BootRom::new(bytes)?;
        if boot.family != self.mode {
            return Err(format!(
                "A {:?} machine needs a {:?}-family boot ROM",
                self.mode, self.mode
            ));
        }
        if self.system_ticks != 0 {
            return Err("LoadBootRom must be called before execution begins".into());
        }
        self.boot = Some(boot);
        Ok(())
    }
    pub fn boot_rom_mapped(&self) -> bool {
        self.boot.is_some() && self.io[0x50] == 0
    }
    pub fn joypad_press(&mut self, buttons: u8) {
        if self.joypad & buttons != 0 {
            return;
        }
        self.joypad |= buttons;
        self.interrupts.raise(Interrupts::JOYPAD);
    }
    pub fn joypad_release(&mut self, buttons: u8) {
        self.joypad &= !buttons;
    }
    pub fn tick_mcycle(&mut self) {
        self.clock_double_speed = self.double_speed;
        self.tick_one_mcycle();
    }
    fn tick_one_mcycle(&mut self) {
        for t in 0..4 {
            if self
                .timer
                .tick_t(&mut self.interrupts, self.clock_double_speed)
            {
                self.apu.advance_frame_sequencer();
            }
            if let Some((address, index)) = self.oam_dma.tick_t() {
                self.oam[index] = self.read_debug(address);
            }
            if self.hdma.active {
                self.hdma.tick_t();
            }
            if !self.clock_double_speed || t & 1 == 0 {
                self.apu.tick_t();
            }
            self.serial.tick_t(&mut self.interrupts);
        }
        for _ in 0..if self.clock_double_speed { 2 } else { 4 } {
            if self
                .ppu
                .tick_dot(&self.vram, &self.oam, &mut self.interrupts)
            {
                self.transfer_hdma_block(true);
                let context = self.hook.as_ref().map(|_| self.hook_context());
                if let Some(hook) = &mut self.hook {
                    hook.on_hblank(context.unwrap(), self.ppu.ly, self.ppu.dot - 1);
                }
            }
            self.system_ticks = self.system_ticks.wrapping_add(1);
            self.frame_system_ticks = self.frame_system_ticks.wrapping_add(1);
        }
    }
    pub fn transfer_hdma_block(&mut self, hblank: bool) {
        let count = self.hdma.block_len(hblank);
        if count == 0 {
            return;
        }
        for _ in 0..count {
            let value = self.read_debug(self.hdma.current_source);
            self.write_hdma(self.hdma.current_dest, value);
            self.hdma.advance_byte();
        }
        self.hdma.finish_block(hblank);
    }
    fn step_cpu(&mut self) {
        let mut cpu = std::mem::take(&mut self.cpu);
        self.cpu_active = true;
        self.active_pc = cpu.registers.pc;
        cpu.step(self);
        self.cpu = cpu;
        self.cpu_active = false;
        self.active_pc = self.cpu.registers.pc;
        while self.hdma.cpu_halted_by_gp {
            self.transfer_hdma_block(false);
            for _ in 0..if self.clock_double_speed { 16 } else { 8 } {
                self.tick_one_mcycle();
            }
        }
    }
    pub fn step_instruction(&mut self) -> StepResult {
        let start = self.cpu.total_tcycles;
        self.step_cpu();
        StepResult {
            reason: StopReason::InstructionComplete,
            tcycles_ran: self.cpu.total_tcycles.wrapping_sub(start),
            final_pc: self.cpu.registers.pc,
        }
    }
    pub fn run_frame(&mut self) -> StepResult {
        self.stop_requested = None;
        self.frame_system_ticks = 0;
        while self.frame_system_ticks < Self::SYSTEM_TICKS_PER_FRAME {
            self.step_cpu();
            let reason = if let Some(reason) = self.stop_requested {
                Some(reason)
            } else if self
                .breakpoint_checker
                .as_mut()
                .is_some_and(|check| check(self.cpu.registers.pc))
            {
                Some(StopReason::Breakpoint)
            } else {
                None
            };
            if let Some(reason) = reason {
                return StepResult {
                    reason,
                    tcycles_ran: self.cpu.total_tcycles,
                    final_pc: self.cpu.registers.pc,
                };
            }
        }
        StepResult {
            reason: StopReason::FrameComplete,
            tcycles_ran: self.cpu.total_tcycles,
            final_pc: self.cpu.registers.pc,
        }
    }
    pub fn step_tcycle(&mut self) -> StepResult {
        let mut result = self.step_instruction();
        result.reason = StopReason::TCycleComplete;
        result
    }
    pub fn run_until(&mut self, condition: StopCondition) -> StepResult {
        self.stop_requested = None;
        self.frame_system_ticks = 0;
        let start = self.cpu.total_tcycles;
        let cross_frames =
            condition.kind & (StopCondition::MAX_CYCLES | StopCondition::SPINNING) != 0;
        let mut recent = std::collections::HashSet::new();
        let mut stable = 0;
        let size = if condition.spin_pc_set_size > 0 {
            condition.spin_pc_set_size
        } else {
            4
        };
        let threshold = if condition.spin_stable_instructions > 0 {
            condition.spin_stable_instructions
        } else {
            4096
        };
        loop {
            while self.frame_system_ticks < Self::SYSTEM_TICKS_PER_FRAME {
                self.step_cpu();
                let pc = self.cpu.registers.pc;
                let elapsed = self.cpu.total_tcycles.wrapping_sub(start);
                let mut reason = self.stop_requested;
                if reason.is_none()
                    && ((condition.kind & StopCondition::PC_EQUALS != 0
                        && pc == condition.pc_equals)
                        || (condition.kind & StopCondition::PC_IN_RANGE != 0
                            && pc >= condition.pc_range_start
                            && pc < condition.pc_range_end)
                        || (condition.kind & StopCondition::PC_LEAVES_RANGE != 0
                            && (pc < condition.pc_range_start || pc >= condition.pc_range_end)))
                {
                    reason = Some(StopReason::Breakpoint);
                }
                if reason.is_none()
                    && condition.kind & StopCondition::MAX_CYCLES != 0
                    && elapsed >= condition.max_tcycles
                {
                    reason = Some(StopReason::BudgetExceeded);
                }
                if reason.is_none() && condition.kind & StopCondition::SPINNING != 0 {
                    if recent.contains(&pc) {
                        stable += 1;
                    } else {
                        if recent.len() >= size {
                            recent.clear();
                        }
                        recent.insert(pc);
                        stable = 0;
                    }
                    if stable >= threshold {
                        reason = Some(StopReason::Spinning);
                    }
                }
                if let Some(reason) = reason {
                    return StepResult {
                        reason,
                        tcycles_ran: elapsed,
                        final_pc: pc,
                    };
                }
            }
            if !cross_frames {
                return StepResult {
                    reason: StopReason::FrameComplete,
                    tcycles_ran: self.cpu.total_tcycles.wrapping_sub(start),
                    final_pc: self.cpu.registers.pc,
                };
            }
            self.frame_system_ticks = 0;
        }
    }
    pub fn write_state(&self, w: &mut StateWriter) {
        w.write_u64(self.boot.as_ref().map_or(0, |b| b.fingerprint));
        w.write_u64(self.system_ticks);
        w.write_u64(self.frame_system_ticks);
        w.write_bool(self.clock_double_speed);
        self.cpu.write_state(w);
        self.timer.write_state(w);
        self.ppu.write_state(w);
        self.oam_dma.write_state(w);
        self.hdma.write_state(w);
        self.apu.write_state(w);
        w.write_bool(self.switch_armed);
        w.write_bool(self.double_speed);
        self.cartridge.write_state(w);
        w.write_bytes(&self.vram);
        w.write_bytes(&self.wram);
        w.write_bytes(&self.oam);
        w.write_bytes(&self.hram);
        w.write_bytes(&[self.vram_bank, self.wram_bank]);
        w.write_bytes(&self.io);
        w.write_bytes(&[self.interrupts.iflag, self.interrupts.ie]);
        self.serial.write_state(w);
    }
    pub fn read_state(&mut self, r: &mut StateReader<'_>) -> io::Result<()> {
        let fingerprint = r.read_u64()?;
        if fingerprint != self.boot.as_ref().map_or(0, |b| b.fingerprint) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "different boot ROM fingerprint",
            ));
        }
        self.system_ticks = r.read_u64()?;
        self.frame_system_ticks = r.read_u64()?;
        self.clock_double_speed = r.read_bool()?;
        self.cpu.read_state(r)?;
        self.timer.read_state(r)?;
        self.ppu.read_state(r)?;
        self.oam_dma.read_state(r)?;
        self.hdma.read_state(r)?;
        self.apu.read_state(r)?;
        self.switch_armed = r.read_bool()?;
        self.double_speed = r.read_bool()?;
        self.cartridge.read_state(r)?;
        self.vram.copy_from_slice(r.read_bytes(0x4000)?);
        self.wram.copy_from_slice(r.read_bytes(0x8000)?);
        self.oam.copy_from_slice(r.read_bytes(0xa0)?);
        self.hram.copy_from_slice(r.read_bytes(0x7f)?);
        self.vram_bank = r.read_byte()?;
        self.wram_bank = r.read_byte()?;
        if self.vram_bank > 1 || !(1..=7).contains(&self.wram_bank) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "invalid memory bank state",
            ));
        }
        self.io.copy_from_slice(r.read_bytes(128)?);
        self.interrupts.iflag = r.read_byte()?;
        self.interrupts.ie = r.read_byte()?;
        self.serial.read_state(r)?;
        self.active_pc = self.cpu.registers.pc;
        Ok(())
    }
}
impl CpuBus for Machine {
    fn set_cpu_state(&mut self, pc: u16, ime: bool, tcycles: u64) {
        self.active_pc = pc;
        self.active_ime = ime;
        self.active_tcycles = tcycles;
    }
    fn set_pc(&mut self, pc: u16) {
        self.active_pc = pc;
    }
    fn read(&mut self, address: u16) -> u8 {
        self.tick_mcycle();
        self.read_cpu(address)
    }
    fn write(&mut self, address: u16, value: u8) {
        self.tick_mcycle();
        self.write_cpu(address, value);
    }
    fn internal_cycle(&mut self) {
        self.tick_mcycle();
    }
    fn pending_interrupts(&self) -> u8 {
        self.interrupts.pending()
    }
    fn clear_interrupt(&mut self, mask: u8) {
        self.interrupts.clear(mask);
    }
    fn stop(&mut self) -> bool {
        if self.switch_armed {
            self.double_speed = !self.double_speed;
            self.switch_armed = false;
            if self.timer.write_div() {
                self.apu.advance_frame_sequencer();
            }
            true
        } else {
            false
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    type HookEvent = (u16, u8, u16, u8, i32, PpuMode);
    struct Mode3Trace(std::sync::Arc<std::sync::Mutex<Vec<HookEvent>>>);
    type OracleEvents = std::sync::Arc<std::sync::Mutex<Vec<(HookEvent, MemoryContext)>>>;
    type HblankEvents = std::sync::Arc<std::sync::Mutex<Vec<(u64, u8, i32, u8)>>>;
    struct OracleTrace {
        events: OracleEvents,
        context: MemoryContext,
        hblanks: HblankEvents,
    }
    impl MemoryHook for OracleTrace {
        fn on_hblank(&mut self, context: MemoryContext, ly: u8, dot: i32) {
            self.hblanks
                .lock()
                .unwrap()
                .push((context.system_ticks, ly, dot, context.scx));
        }
        fn set_context(&mut self, context: MemoryContext) {
            self.context = context;
        }
        fn on_read(&mut self, _a: u16, _v: u8, _pc: u16, _ly: u8, _dot: i32, _mode: PpuMode) {}
        fn on_write(&mut self, a: u16, v: u8, pc: u16, ly: u8, dot: i32, mode: PpuMode) {
            if (0x8000..0xa000).contains(&a) && mode == PpuMode::Drawing {
                self.events
                    .lock()
                    .unwrap()
                    .push(((a, v, pc, ly, dot, mode), self.context));
            }
        }
    }
    impl MemoryHook for Mode3Trace {
        fn on_read(
            &mut self,
            _address: u16,
            _value: u8,
            _pc: u16,
            _ly: u8,
            _dot: i32,
            _mode: PpuMode,
        ) {
        }
        fn on_write(&mut self, address: u16, value: u8, pc: u16, ly: u8, dot: i32, mode: PpuMode) {
            if (0x8000..0xa000).contains(&address) && mode == PpuMode::Drawing {
                self.0
                    .lock()
                    .unwrap()
                    .push((address, value, pc, ly, dot, mode));
            }
        }
    }
    fn machine(mode: HardwareMode) -> Machine {
        Machine::new(Cartridge::load(&vec![0; 0x8000]).unwrap(), Some(mode))
    }
    #[test]
    fn managed_full_machine_states_framebuffers_and_pcm_match() {
        let fixtures: &[(&str, &[u8], &[u8])] = &[
            (
                "DMG",
                include_bytes!("machine/Dmg-0.gb"),
                include_bytes!("machine/Dmg-0.bin"),
            ),
            (
                "CGB",
                include_bytes!("machine/Cgb-0.gb"),
                include_bytes!("machine/Cgb-0.bin"),
            ),
            (
                "CGB-double",
                include_bytes!("machine/Cgb-1.gb"),
                include_bytes!("machine/Cgb-1.bin"),
            ),
        ];
        for &(name, rom, fixture) in fixtures {
            let mut m = Machine::load_rom(rom).unwrap();
            let events = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
            let hblanks = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
            m.hook = Some(Box::new(OracleTrace {
                events: events.clone(),
                context: MemoryContext::default(),
                hblanks: hblanks.clone(),
            }));
            let mut r = StateReader::new(fixture);
            let mut previous = 0;
            for target in [0, 1, 10, 100, 1000, 10000, 10001, 10002] {
                if target <= 10000 {
                    for _ in previous..target {
                        m.step_instruction();
                    }
                } else {
                    m.run_frame();
                }
                previous = target;
                let length = r.read_i32().unwrap() as usize;
                let expected = r.read_bytes(length).unwrap();
                let mut actual = StateWriter::default();
                m.write_state(&mut actual);
                if actual.bytes != expected {
                    let index = actual.bytes.iter().zip(expected).position(|(a, b)| a != b);
                    panic!(
                        "{name} checkpoint{target} state mismatch at {index:?}, lengths {} vs {}",
                        actual.bytes.len(),
                        expected.len()
                    );
                }
                let mut restored = Machine::load_rom(rom).unwrap();
                restored
                    .read_state(&mut StateReader::new(expected))
                    .unwrap();
                let mut roundtrip = StateWriter::default();
                restored.write_state(&mut roundtrip);
                assert_eq!(
                    roundtrip.bytes, expected,
                    "{name} checkpoint{target} managed restore"
                );
            }
            assert_eq!(
                m.ppu.framebuffer,
                r.read_bytes(160 * 144 * 4).unwrap(),
                "{name} front framebuffer"
            );
            assert_eq!(
                m.ppu.back_buffer(),
                r.read_bytes(160 * 144 * 4).unwrap(),
                "{name} back framebuffer"
            );
            let count = r.read_i32().unwrap() as usize;
            let mut pcm = vec![0; 8192];
            assert_eq!(m.apu.sample_buffer.drain(&mut pcm), count);
            for (index, &sample) in pcm[..count].iter().enumerate() {
                assert_eq!(
                    sample,
                    u16::from_le_bytes(r.read_bytes(2).unwrap().try_into().unwrap()) as i16,
                    "{name} PCM{index}"
                );
            }
            let count = r.read_i32().unwrap() as usize;
            let events = events.lock().unwrap();
            assert_eq!(events.len(), count, "{name} mode3 hook count");
            assert!(count > 100);
            for &((address, value, pc, ly, dot, mode), context) in events.iter() {
                assert_eq!(address, r.read_u16().unwrap());
                assert_eq!(value, r.read_byte().unwrap());
                assert_eq!(pc, r.read_u16().unwrap(), "{name} hook PC");
                assert_eq!(ly, r.read_byte().unwrap());
                assert_eq!(dot, r.read_i32().unwrap(), "{name} hook dot");
                assert_eq!(mode as i32, r.read_i32().unwrap());
                assert_eq!(
                    context,
                    MemoryContext {
                        ime: r.read_bool().unwrap(),
                        tcycles: r.read_u64().unwrap(),
                        system_ticks: r.read_u64().unwrap(),
                        lcdc: r.read_byte().unwrap(),
                        scx: r.read_byte().unwrap(),
                        boot_mapped: r.read_bool().unwrap(),
                        rom_bank: r.read_byte().unwrap()
                    },
                    "{name} memory hook context"
                );
            }
            let count = r.read_i32().unwrap() as usize;
            let hblanks = hblanks.lock().unwrap();
            assert_eq!(hblanks.len(), count);
            for &(ticks, ly, dot, scx) in hblanks.iter() {
                assert_eq!(
                    (ticks, ly, dot, scx),
                    (
                        r.read_u64().unwrap(),
                        r.read_byte().unwrap(),
                        r.read_i32().unwrap(),
                        r.read_byte().unwrap()
                    ),
                    "{name} exact HBlank event"
                );
            }
            assert_eq!(r.remaining(), 0);
        }
    }
    #[test]
    fn power_on_registers_ram_and_palettes_remain_raw() {
        for mode in [HardwareMode::Dmg, HardwareMode::Cgb] {
            let m = machine(mode);
            assert_eq!(m.cpu.registers, crate::cpu::Registers::default());
            assert!(
                m.vram
                    .iter()
                    .chain(&m.wram)
                    .chain(&m.oam)
                    .chain(&m.hram)
                    .all(|&b| b == 255)
            );
            assert!(
                m.ppu
                    .bg_palette
                    .raw_data()
                    .iter()
                    .chain(m.ppu.obj_palette.raw_data())
                    .all(|&b| b == 0)
            );
        }
    }
    #[test]
    fn wram_echo_hram_ie_and_prohibited_addresses() {
        let mut m = machine(HardwareMode::Dmg);
        for (address, value) in [
            (0xc234, 0x88),
            (0xd100, 0x77),
            (0xff90, 0x55),
            (0xffff, 255),
        ] {
            m.write_cpu(address, value);
            assert_eq!(m.read_cpu(address), value);
        }
        assert_eq!(m.read_cpu(0xe234), 0x88);
        assert_eq!(m.read_cpu(0xf100), 0x77);
        assert_eq!(m.read_cpu(0xfea5), 0);
        m.write_cpu(0xff56, 0);
        assert_eq!(m.read_cpu(0xff56), 255);
    }
    #[test]
    fn cgb_banks_isolate_ram_and_dmg_writes_are_inert() {
        for mode in [HardwareMode::Dmg, HardwareMode::Cgb] {
            let mut m = machine(mode);
            m.ppu.lcdc = 0;
            m.write_cpu(0x8000, 0xaa);
            m.write_cpu(0xff4f, 1);
            m.write_cpu(0x8000, 0xbb);
            m.write_cpu(0xff4f, 0);
            assert_eq!(
                m.read_cpu(0x8000),
                if mode == HardwareMode::Cgb {
                    0xaa
                } else {
                    0xbb
                }
            );
            m.write_cpu(0xff70, 2);
            m.write_cpu(0xd000, 0x11);
            m.write_cpu(0xff70, 3);
            m.write_cpu(0xd000, 0x22);
            m.write_cpu(0xff70, 2);
            assert_eq!(
                m.read_cpu(0xd000),
                if mode == HardwareMode::Cgb {
                    0x11
                } else {
                    0x22
                }
            );
            m.write_cpu(0xff70, 0);
            assert_eq!(m.wram_bank, 1);
        }
    }
    #[test]
    fn joypad_selects_active_low_groups_and_irqs_on_transition() {
        let mut m = machine(HardwareMode::Dmg);
        m.joypad_press(0x10 | 2);
        assert_eq!(m.interrupts.iflag, 16);
        m.interrupts.iflag = 0;
        m.joypad_press(0x10);
        assert_eq!(m.interrupts.iflag, 0);
        m.write_cpu(0xff00, 0x10);
        assert_eq!(m.read_cpu(0xff00), 0xde);
        m.write_cpu(0xff00, 0x20);
        assert_eq!(m.read_cpu(0xff00), 0xed);
        m.joypad_release(2);
        assert_eq!(m.read_cpu(0xff00), 0xef);
    }
    #[test]
    fn ppu_mode_lockouts_and_debug_read_bypass() {
        let mut m = machine(HardwareMode::Dmg);
        m.ppu.lcdc = 0;
        m.write_cpu(0x8000, 0x12);
        m.write_cpu(0xfe00, 0x34);
        m.ppu.lcdc = 0x91;
        m.ppu.mode = PpuMode::Drawing;
        assert_eq!(m.read_cpu(0x8000), 255);
        assert_eq!(m.read_debug(0x8000), 0x12);
        m.write_cpu(0x8000, 0x99);
        m.write_cpu(0xfe00, 0x99);
        assert_eq!(m.read_debug(0x8000), 0x12);
        assert_eq!(m.read_debug(0xfe00), 0x34);
    }
    #[test]
    fn oam_dma_start_delay_contention_retrigger_and_high_aliases() {
        for (source, alias) in [
            (0xc000, 0xc000),
            (0xe000, 0xc000),
            (0xfe00, 0xde00),
            (0xff00, 0xdf00),
        ] {
            let mut m = machine(HardwareMode::Dmg);
            for i in 0..160 {
                m.write_cpu(alias + i, (i as u8).wrapping_add(1));
            }
            m.write_cpu(0xff90, 0x42);
            m.write_cpu(0xff46, (source >> 8) as u8);
            assert!(!m.oam_dma.is_bus_locking);
            m.tick_mcycle();
            assert!(m.oam_dma.is_bus_locking);
            assert_eq!(m.oam[0], 1);
            assert_eq!(m.read_cpu(0xc000), 255);
            assert_eq!(m.read_cpu(0xff90), 0x42);
            m.write_cpu(0xc000, 0x99);
            m.write_cpu(0xff90, 0x55);
            assert_eq!(m.read_debug(0xff90), 0x55);
            for _ in 0..160 {
                m.tick_mcycle();
            }
            assert!(!m.oam_dma.is_bus_locking);
            assert_eq!(m.oam.len(), 160);
            for (i, byte) in m.oam.iter().enumerate() {
                assert_eq!(*byte, (i as u8).wrapping_add(1));
            }
        }
    }
    #[test]
    fn gdma_freezes_cpu_and_burns_32_system_dots_in_each_speed() {
        for double in [false, true] {
            let mut m = machine(HardwareMode::Cgb);
            m.ppu.lcdc = 0;
            m.double_speed = double;
            for i in 0..16 {
                m.write_cpu(0xc100 + i, (i + 1) as u8);
            }
            for (a, v) in [(0xff51, 0xc1), (0xff52, 0), (0xff53, 0x80), (0xff54, 0)] {
                m.write_cpu(a, v);
            }
            m.wram[..4].copy_from_slice(&[0x3e, 0, 0xe0, 0x55]);
            m.cpu.registers.pc = 0xc000;
            m.step_instruction();
            let before = m.system_ticks;
            let result = m.step_instruction();
            assert_eq!(m.system_ticks - before, 32 + if double { 6 } else { 12 });
            assert_eq!(result.final_pc, 0xc004);
            assert_eq!(result.tcycles_ran, 12);
            assert_eq!(&m.vram[..16], &(1..=16).collect::<Vec<_>>());
        }
    }
    #[test]
    fn hdma_waits_for_hblank_cancel_and_mode3_drops() {
        let mut m = machine(HardwareMode::Cgb);
        m.wram[..16].fill(0x55);
        m.hdma.source1 = 0xc0;
        m.hdma.dest1 = 0x80;
        m.hdma.write_length_register(0x81);
        for _ in 0..32 {
            m.hdma.tick_t();
        }
        assert_eq!(m.vram[0], 255);
        m.transfer_hdma_block(true);
        assert_eq!(&m.vram[..16], &[0x55; 16]);
        assert!(m.hdma.active);
        m.hdma.write_length_register(0);
        assert!(!m.hdma.active);
        m.ppu.mode = PpuMode::Drawing;
        m.hdma.write_length_register(0);
        m.transfer_hdma_block(false);
        assert_eq!(&m.vram[..16], &[0x55; 16]);
    }
    #[test]
    fn speed_switch_resets_div_and_peripheral_dot_ratio() {
        let mut m = machine(HardwareMode::Cgb);
        for _ in 0..1024 {
            m.tick_mcycle();
        }
        assert!(m.timer.div_apu_bit_high());
        m.switch_armed = true;
        assert!(CpuBus::stop(&mut m));
        assert_eq!(m.timer.div(), 0);
        assert!(m.double_speed);
        let dots = m.system_ticks;
        m.tick_mcycle();
        assert_eq!(m.system_ticks - dots, 2);
    }
    #[test]
    fn oam_retrigger_restarts_and_hooks_report_debug_boundary_pc() {
        let mut m = machine(HardwareMode::Dmg);
        m.wram[..160].fill(0x11);
        m.wram[0x1000..0x1000 + 160].fill(0x22);
        m.write_cpu(0xff46, 0xc0);
        for _ in 0..13 {
            m.tick_mcycle();
        }
        m.write_cpu(0xff46, 0xd0);
        for _ in 0..162 {
            m.tick_mcycle();
        }
        assert_eq!(m.oam, &[0x22; 160]);
        let events = std::sync::Arc::new(std::sync::Mutex::new(Vec::new()));
        m.hook = Some(Box::new(Mode3Trace(events.clone())));
        m.ppu.mode = PpuMode::Drawing;
        m.cpu.registers.pc = 0x1234;
        m.write_debug(0x9000, 0xab);
        assert_eq!(
            events.lock().unwrap()[0],
            (0x9000, 0xab, 0x1234, m.ppu.ly, m.ppu.dot, PpuMode::Drawing)
        );
    }
    #[test]
    fn boot_family_overlay_unmap_and_identity_rejection() {
        for mode in [HardwareMode::Dmg, HardwareMode::Cgb] {
            let mut m = machine(mode);
            m.cartridge.rom[..0x1000].fill(0xc7);
            let size = if mode == HardwareMode::Dmg { 256 } else { 2304 };
            m.load_boot_rom(vec![0xb0; size]).unwrap();
            assert_eq!(m.read_debug(0), 0xb0);
            assert_eq!(m.read_debug(0x100), 0xc7);
            assert_eq!(
                m.read_debug(0x200),
                if mode == HardwareMode::Cgb {
                    0xb0
                } else {
                    0xc7
                }
            );
            m.write_cpu(0xff50, 0);
            assert!(m.boot_rom_mapped());
            let mut w = StateWriter::default();
            m.write_state(&mut w);
            m.write_cpu(0xff50, 1);
            m.write_cpu(0xff50, 0);
            assert!(!m.boot_rom_mapped());
            m.read_state(&mut StateReader::new(&w.bytes)).unwrap();
            assert!(m.boot_rom_mapped());
            let mut wrong = machine(mode);
            wrong.load_boot_rom(vec![0xb1; size]).unwrap();
            wrong.cpu.registers.pc = 0x1234;
            assert!(wrong.read_state(&mut StateReader::new(&w.bytes)).is_err());
            assert_eq!(wrong.cpu.registers.pc, 0x1234);
        }
    }
    #[test]
    fn frame_counter_and_instruction_results_match_managed_contract() {
        let mut m = machine(HardwareMode::Dmg);
        assert_eq!(m.step_instruction().tcycles_ran, 4);
        let before = m.system_ticks;
        let result = m.run_frame();
        assert_eq!(result.reason, StopReason::FrameComplete);
        assert_eq!(m.system_ticks - before, 70224);
        assert_eq!(result.tcycles_ran, m.cpu.total_tcycles);
    }
    #[test]
    fn spinning_detector_crosses_frames_without_false_init_loop_stop() {
        let mut m = machine(HardwareMode::Dmg);
        let program = [
            0x01, 0x00, 0x20, 0, 0, 0, 0x0b, 0x78, 0xb1, 0x20, 0xf8, 0x3e, 0x42, 0xea, 0, 0xc0,
            0x18, 0xfe,
        ];
        m.cartridge.rom[0x100..0x100 + program.len()].copy_from_slice(&program);
        m.cpu.registers.pc = 0x100;
        let mut short = machine(HardwareMode::Dmg);
        short.cartridge.rom = m.cartridge.rom.clone();
        short.cpu.registers.pc = 0x100;
        assert_eq!(
            short
                .run_until(StopCondition::spinning_or_budget(2000))
                .reason,
            StopReason::BudgetExceeded
        );
        let result = m.run_until(StopCondition::spinning_or_budget(500000));
        assert_eq!(result.reason, StopReason::Spinning);
        assert_eq!(result.final_pc, 0x110);
        assert_eq!(m.read_debug(0xc000), 0x42);
        assert_eq!(
            machine(HardwareMode::Dmg)
                .run_until(StopCondition::at_pc(0xffff))
                .reason,
            StopReason::FrameComplete
        );
    }
}
