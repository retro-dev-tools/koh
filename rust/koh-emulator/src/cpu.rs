use crate::state::{StateReader, StateWriter};
use std::io;

pub trait CpuBus {
    fn set_pc(&mut self, _pc: u16) {}
    fn set_cpu_state(&mut self, pc: u16, _ime: bool, _tcycles: u64) {
        self.set_pc(pc);
    }
    fn read(&mut self, address: u16) -> u8;
    fn write(&mut self, address: u16, value: u8);
    fn internal_cycle(&mut self);
    fn pending_interrupts(&self) -> u8;
    fn clear_interrupt(&mut self, mask: u8);
    fn stop(&mut self) -> bool;
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Registers {
    pub a: u8,
    pub f: u8,
    pub b: u8,
    pub c: u8,
    pub d: u8,
    pub e: u8,
    pub h: u8,
    pub l: u8,
    pub sp: u16,
    pub pc: u16,
}
impl Registers {
    pub const FLAG_Z: u8 = 0x80;
    pub const FLAG_N: u8 = 0x40;
    pub const FLAG_H: u8 = 0x20;
    pub const FLAG_C: u8 = 0x10;
    pub fn af(&self) -> u16 {
        u16::from_be_bytes([self.a, self.f & 0xf0])
    }
    pub fn bc(&self) -> u16 {
        u16::from_be_bytes([self.b, self.c])
    }
    pub fn de(&self) -> u16 {
        u16::from_be_bytes([self.d, self.e])
    }
    pub fn hl(&self) -> u16 {
        u16::from_be_bytes([self.h, self.l])
    }
    pub fn set_af(&mut self, v: u16) {
        [self.a, self.f] = v.to_be_bytes();
        self.f &= 0xf0;
    }
    pub fn set_bc(&mut self, v: u16) {
        [self.b, self.c] = v.to_be_bytes();
    }
    pub fn set_de(&mut self, v: u16) {
        [self.d, self.e] = v.to_be_bytes();
    }
    pub fn set_hl(&mut self, v: u16) {
        [self.h, self.l] = v.to_be_bytes();
    }
    pub fn flag_set(&self, mask: u8) -> bool {
        self.f & mask != 0
    }
    pub fn set_flag(&mut self, mask: u8, on: bool) {
        if on { self.f |= mask } else { self.f &= !mask }
    }
}

#[derive(Clone, Debug, Default)]
pub struct Cpu {
    pub registers: Registers,
    pub halted: bool,
    pub stopped: bool,
    pub ime: bool,
    pub total_tcycles: u64,
    ei_pending: bool,
    ei_armed: bool,
    halt_bug_next_fetch: bool,
}
impl Cpu {
    pub fn step(&mut self, bus: &mut impl CpuBus) {
        if self.halted || self.stopped {
            self.cycle(bus);
            if self.halted && bus.pending_interrupts() != 0 {
                self.halted = false;
            }
        } else {
            let op = self.immediate(bus);
            if self.halt_bug_next_fetch {
                self.registers.pc = self.registers.pc.wrapping_sub(1);
                self.halt_bug_next_fetch = false;
            }
            self.execute(bus, op);
        }
        if self.ei_armed {
            self.ime = true;
            self.ei_armed = false;
        }
        if self.ei_pending {
            self.ei_armed = true;
            self.ei_pending = false;
        }
        if self.ime && bus.pending_interrupts() != 0 {
            self.ime = false;
            self.halted = false;
            self.cycle(bus);
            self.cycle(bus);
            self.registers.sp = self.registers.sp.wrapping_sub(1);
            self.write(bus, self.registers.sp, (self.registers.pc >> 8) as u8);
            let selected = bus.pending_interrupts();
            let vector = if selected == 0 {
                0
            } else {
                let bit = selected.trailing_zeros();
                bus.clear_interrupt(1 << bit);
                0x40 + bit as u16 * 8
            };
            self.registers.sp = self.registers.sp.wrapping_sub(1);
            self.write(bus, self.registers.sp, self.registers.pc as u8);
            self.cycle(bus);
            self.registers.pc = vector;
        }
    }
    fn cycle(&mut self, b: &mut impl CpuBus) {
        self.total_tcycles = self.total_tcycles.wrapping_add(4);
        b.set_cpu_state(self.registers.pc, self.ime, self.total_tcycles);
        b.internal_cycle();
    }
    fn read(&mut self, b: &mut impl CpuBus, a: u16) -> u8 {
        self.total_tcycles = self.total_tcycles.wrapping_add(4);
        b.set_cpu_state(self.registers.pc, self.ime, self.total_tcycles);
        b.read(a)
    }
    fn write(&mut self, b: &mut impl CpuBus, a: u16, v: u8) {
        self.total_tcycles = self.total_tcycles.wrapping_add(4);
        b.set_cpu_state(self.registers.pc, self.ime, self.total_tcycles);
        b.write(a, v);
    }
    fn immediate(&mut self, b: &mut impl CpuBus) -> u8 {
        let v = self.read(b, self.registers.pc);
        self.registers.pc = self.registers.pc.wrapping_add(1);
        v
    }
    fn immediate16(&mut self, b: &mut impl CpuBus) -> u16 {
        let lo = self.immediate(b);
        let hi = self.immediate(b);
        u16::from_le_bytes([lo, hi])
    }
    fn reg(&mut self, b: &mut impl CpuBus, i: u8) -> u8 {
        match i {
            0 => self.registers.b,
            1 => self.registers.c,
            2 => self.registers.d,
            3 => self.registers.e,
            4 => self.registers.h,
            5 => self.registers.l,
            6 => self.read(b, self.registers.hl()),
            _ => self.registers.a,
        }
    }
    fn set_reg(&mut self, b: &mut impl CpuBus, i: u8, v: u8) {
        match i {
            0 => self.registers.b = v,
            1 => self.registers.c = v,
            2 => self.registers.d = v,
            3 => self.registers.e = v,
            4 => self.registers.h = v,
            5 => self.registers.l = v,
            6 => self.write(b, self.registers.hl(), v),
            _ => self.registers.a = v,
        }
    }
    fn pair(&self, i: u8) -> u16 {
        match i {
            0 => self.registers.bc(),
            1 => self.registers.de(),
            2 => self.registers.hl(),
            _ => self.registers.sp,
        }
    }
    fn set_pair(&mut self, i: u8, v: u16) {
        match i {
            0 => self.registers.set_bc(v),
            1 => self.registers.set_de(v),
            2 => self.registers.set_hl(v),
            _ => self.registers.sp = v,
        }
    }
    fn push(&mut self, b: &mut impl CpuBus, v: u16) {
        self.registers.sp = self.registers.sp.wrapping_sub(1);
        self.write(b, self.registers.sp, (v >> 8) as u8);
        self.registers.sp = self.registers.sp.wrapping_sub(1);
        self.write(b, self.registers.sp, v as u8);
    }
    fn pop(&mut self, b: &mut impl CpuBus) -> u16 {
        let lo = self.read(b, self.registers.sp);
        self.registers.sp = self.registers.sp.wrapping_add(1);
        let hi = self.read(b, self.registers.sp);
        self.registers.sp = self.registers.sp.wrapping_add(1);
        u16::from_le_bytes([lo, hi])
    }
    fn cond(&self, i: u8) -> bool {
        match i {
            0 => !self.registers.flag_set(0x80),
            1 => self.registers.flag_set(0x80),
            2 => !self.registers.flag_set(0x10),
            _ => self.registers.flag_set(0x10),
        }
    }
    fn ime_now(&mut self, on: bool) {
        self.ime = on;
        self.ei_pending = false;
        self.ei_armed = false;
    }
    fn alu(&mut self, op: u8, v: u8) {
        let r = &mut self.registers;
        let a = r.a;
        let carry = u8::from((op == 1 || op == 3) && r.flag_set(0x10));
        match op {
            0 | 1 => {
                let sum = a as u16 + v as u16 + carry as u16;
                r.a = sum as u8;
                r.set_flag(0x80, r.a == 0);
                r.set_flag(0x40, false);
                r.set_flag(0x20, (a & 15) + (v & 15) + carry > 15);
                r.set_flag(0x10, sum > 255);
            }
            2 | 3 | 7 => {
                let sum = a as i16 - v as i16 - carry as i16;
                r.set_flag(0x80, sum as u8 == 0);
                r.set_flag(0x40, true);
                r.set_flag(0x20, (a & 15) as i16 - (v & 15) as i16 - (carry as i16) < 0);
                r.set_flag(0x10, sum < 0);
                if op != 7 {
                    r.a = sum as u8;
                }
            }
            _ => {
                r.a = match op {
                    4 => a & v,
                    5 => a ^ v,
                    _ => a | v,
                };
                r.set_flag(0x80, r.a == 0);
                r.set_flag(0x40, false);
                r.set_flag(0x20, op == 4);
                r.set_flag(0x10, false);
            }
        }
    }
    fn rotate(&mut self, op: u8, v: u8) -> u8 {
        let carry = u8::from(self.registers.flag_set(0x10));
        let (result, c) = match op {
            0 => (v.rotate_left(1), v >> 7),
            1 => (v.rotate_right(1), v & 1),
            2 => ((v << 1) | carry, v >> 7),
            3 => ((v >> 1) | (carry << 7), v & 1),
            4 => (v << 1, v >> 7),
            5 => ((v >> 1) | (v & 0x80), v & 1),
            6 => (v.rotate_left(4), 0),
            _ => (v >> 1, v & 1),
        };
        self.registers.f = if c != 0 { 0x10 } else { 0 };
        self.registers.set_flag(0x80, result == 0);
        result
    }
    fn execute(&mut self, b: &mut impl CpuBus, op: u8) {
        let y = (op >> 3) & 7;
        let z = op & 7;
        let p = y >> 1;
        match op {
            0x00 => {}
            0xcb => {
                let cb = self.immediate(b);
                let reg = cb & 7;
                let bit = (cb >> 3) & 7;
                let v = self.reg(b, reg);
                match cb >> 6 {
                    0 => {
                        let v = self.rotate(bit, v);
                        self.set_reg(b, reg, v);
                    }
                    1 => {
                        self.registers.set_flag(0x80, v & (1 << bit) == 0);
                        self.registers.set_flag(0x40, false);
                        self.registers.set_flag(0x20, true);
                    }
                    2 => self.set_reg(b, reg, v & !(1 << bit)),
                    _ => self.set_reg(b, reg, v | (1 << bit)),
                }
            }
            0x01 | 0x11 | 0x21 | 0x31 => {
                let v = self.immediate16(b);
                self.set_pair(p, v);
            }
            0x02 | 0x12 | 0x22 | 0x32 | 0x0a | 0x1a | 0x2a | 0x3a => {
                let a = if p < 2 {
                    self.pair(p)
                } else {
                    self.registers.hl()
                };
                if y & 1 == 0 {
                    self.write(b, a, self.registers.a)
                } else {
                    self.registers.a = self.read(b, a)
                }
                if p >= 2 {
                    self.registers.set_hl(if p == 2 {
                        a.wrapping_add(1)
                    } else {
                        a.wrapping_sub(1)
                    });
                }
            }
            0x03 | 0x13 | 0x23 | 0x33 | 0x0b | 0x1b | 0x2b | 0x3b => {
                let v = self.pair(p);
                self.set_pair(
                    p,
                    if y & 1 == 0 {
                        v.wrapping_add(1)
                    } else {
                        v.wrapping_sub(1)
                    },
                );
                self.cycle(b);
            }
            0x04 | 0x0c | 0x14 | 0x1c | 0x24 | 0x2c | 0x34 | 0x3c | 0x05 | 0x0d | 0x15 | 0x1d
            | 0x25 | 0x2d | 0x35 | 0x3d => {
                let v = self.reg(b, y);
                let dec = z == 5;
                let n = if dec {
                    v.wrapping_sub(1)
                } else {
                    v.wrapping_add(1)
                };
                self.registers.set_flag(0x80, n == 0);
                self.registers.set_flag(0x40, dec);
                self.registers
                    .set_flag(0x20, if dec { v & 15 == 0 } else { v & 15 == 15 });
                self.set_reg(b, y, n);
            }
            0x06 | 0x0e | 0x16 | 0x1e | 0x26 | 0x2e | 0x36 | 0x3e => {
                let v = self.immediate(b);
                self.set_reg(b, y, v);
            }
            0x07 | 0x0f | 0x17 | 0x1f => {
                self.registers.a = self.rotate(y, self.registers.a);
                self.registers.f &= !0x80;
            }
            0x08 => {
                let a = self.immediate16(b);
                self.write(b, a, self.registers.sp as u8);
                self.write(b, a.wrapping_add(1), (self.registers.sp >> 8) as u8);
            }
            0x09 | 0x19 | 0x29 | 0x39 => {
                let a = self.registers.hl();
                let v = self.pair(p);
                let sum = a as u32 + v as u32;
                self.registers.set_flag(0x40, false);
                self.registers
                    .set_flag(0x20, (a & 0xfff) + (v & 0xfff) > 0xfff);
                self.registers.set_flag(0x10, sum > 0xffff);
                self.registers.set_hl(sum as u16);
                self.cycle(b);
            }
            0x10 => {
                self.immediate(b);
                if !b.stop() {
                    self.stopped = true;
                }
            }
            0x18 | 0x20 | 0x28 | 0x30 | 0x38 => {
                let offset = self.immediate(b) as i8;
                if op == 0x18 || self.cond(y - 4) {
                    self.registers.pc = self.registers.pc.wrapping_add_signed(offset as i16);
                    self.cycle(b);
                }
            }
            0x27 => {
                let r = &mut self.registers;
                let mut a = r.a as i32;
                if !r.flag_set(0x40) {
                    if r.flag_set(0x10) || a > 0x99 {
                        a += 0x60;
                        r.set_flag(0x10, true);
                    }
                    if r.flag_set(0x20) || a & 15 > 9 {
                        a += 6;
                    }
                } else {
                    if r.flag_set(0x10) {
                        a -= 0x60;
                    }
                    if r.flag_set(0x20) {
                        a -= 6;
                    }
                }
                r.a = a as u8;
                r.set_flag(0x80, r.a == 0);
                r.set_flag(0x20, false);
            }
            0x2f => {
                self.registers.a = !self.registers.a;
                self.registers.set_flag(0x40, true);
                self.registers.set_flag(0x20, true);
            }
            0x37 | 0x3f => {
                let c = op == 0x37 || !self.registers.flag_set(0x10);
                self.registers.set_flag(0x40, false);
                self.registers.set_flag(0x20, false);
                self.registers.set_flag(0x10, c);
            }
            0x76 => {
                if !self.ime && b.pending_interrupts() != 0 {
                    self.halt_bug_next_fetch = true;
                } else {
                    self.halted = true;
                }
            }
            0x40..=0x7f => {
                let v = self.reg(b, z);
                self.set_reg(b, y, v);
            }
            0x80..=0xbf => {
                let v = self.reg(b, z);
                self.alu(y, v);
            }
            0xc0 | 0xc8 | 0xd0 | 0xd8 => {
                self.cycle(b);
                if self.cond(y) {
                    self.registers.pc = self.pop(b);
                    self.cycle(b);
                }
            }
            0xc1 | 0xd1 | 0xe1 | 0xf1 => {
                let v = self.pop(b);
                if p == 3 {
                    self.registers.set_af(v)
                } else {
                    self.set_pair(p, v)
                }
            }
            0xc5 | 0xd5 | 0xe5 | 0xf5 => {
                self.cycle(b);
                let v = if p == 3 {
                    self.registers.af()
                } else {
                    self.pair(p)
                };
                self.push(b, v);
            }
            0xc2 | 0xca | 0xd2 | 0xda | 0xc3 => {
                let a = self.immediate16(b);
                if op == 0xc3 || self.cond(y) {
                    self.registers.pc = a;
                    self.cycle(b);
                }
            }
            0xc4 | 0xcc | 0xd4 | 0xdc | 0xcd => {
                let a = self.immediate16(b);
                if op == 0xcd || self.cond(y) {
                    self.cycle(b);
                    self.push(b, self.registers.pc);
                    self.registers.pc = a;
                }
            }
            0xc7 | 0xcf | 0xd7 | 0xdf | 0xe7 | 0xef | 0xf7 | 0xff => {
                self.cycle(b);
                self.push(b, self.registers.pc);
                self.registers.pc = y as u16 * 8;
            }
            0xc9 | 0xd9 => {
                self.registers.pc = self.pop(b);
                self.cycle(b);
                if op == 0xd9 {
                    self.ime_now(true);
                }
            }
            0xc6 | 0xce | 0xd6 | 0xde | 0xe6 | 0xee | 0xf6 | 0xfe => {
                let v = self.immediate(b);
                self.alu(y, v);
            }
            0xe0 | 0xf0 | 0xe2 | 0xf2 | 0xea | 0xfa => {
                let a = match op {
                    0xe0 | 0xf0 => 0xff00 | self.immediate(b) as u16,
                    0xe2 | 0xf2 => 0xff00 | self.registers.c as u16,
                    _ => self.immediate16(b),
                };
                if op & 0x10 == 0 {
                    self.write(b, a, self.registers.a)
                } else {
                    self.registers.a = self.read(b, a)
                }
            }
            0xe8 | 0xf8 => {
                let offset = self.immediate(b);
                let sp = self.registers.sp;
                self.registers.set_flag(0x80, false);
                self.registers.set_flag(0x40, false);
                self.registers
                    .set_flag(0x20, (sp & 15) + (offset & 15) as u16 > 15);
                self.registers
                    .set_flag(0x10, (sp & 255) + offset as u16 > 255);
                let v = sp.wrapping_add_signed(offset as i8 as i16);
                if op == 0xe8 {
                    self.registers.sp = v;
                    self.cycle(b);
                } else {
                    self.registers.set_hl(v);
                }
                self.cycle(b);
            }
            0xe9 => self.registers.pc = self.registers.hl(),
            0xf9 => {
                self.registers.sp = self.registers.hl();
                self.cycle(b);
            }
            0xf3 => self.ime_now(false),
            0xfb => self.ei_pending = true,
            _ => self.halted = true,
        }
    }
    pub fn reset(&mut self) {
        *self = Self::default();
    }
    pub fn write_state(&self, w: &mut StateWriter) {
        let r = &self.registers;
        for v in [r.a, r.f, r.b, r.c, r.d, r.e, r.h, r.l] {
            w.write_byte(v);
        }
        w.write_u16(r.sp);
        w.write_u16(r.pc);
        for v in [
            self.halted,
            self.stopped,
            self.ime,
            self.ei_pending,
            self.ei_armed,
            self.halt_bug_next_fetch,
        ] {
            w.write_bool(v);
        }
        w.write_u64(self.total_tcycles);
    }
    pub fn read_state(&mut self, r: &mut StateReader<'_>) -> io::Result<()> {
        let mut next = Self::default();
        let regs = &mut next.registers;
        regs.a = r.read_byte()?;
        regs.f = r.read_byte()?;
        regs.b = r.read_byte()?;
        regs.c = r.read_byte()?;
        regs.d = r.read_byte()?;
        regs.e = r.read_byte()?;
        regs.h = r.read_byte()?;
        regs.l = r.read_byte()?;
        regs.sp = r.read_u16()?;
        regs.pc = r.read_u16()?;
        next.halted = r.read_bool()?;
        next.stopped = r.read_bool()?;
        next.ime = r.read_bool()?;
        next.ei_pending = r.read_bool()?;
        next.ei_armed = r.read_bool()?;
        next.halt_bug_next_fetch = r.read_bool()?;
        next.total_tcycles = r.read_u64()?;
        *self = next;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    struct Bus {
        mem: Box<[u8; 65536]>,
        events: Vec<(char, u16, u8)>,
        ie: u8,
        iflag: u8,
        switch: bool,
    }
    impl Default for Bus {
        fn default() -> Self {
            Self {
                mem: Box::new([0; 65536]),
                events: vec![],
                ie: 0,
                iflag: 0,
                switch: false,
            }
        }
    }
    impl CpuBus for Bus {
        fn read(&mut self, a: u16) -> u8 {
            let v = self.mem[a as usize];
            self.events.push(('r', a, v));
            v
        }
        fn write(&mut self, a: u16, v: u8) {
            self.events.push(('w', a, v));
            self.mem[a as usize] = v;
            if a == 0xffff {
                self.ie = v;
            }
        }
        fn internal_cycle(&mut self) {
            self.events.push(('i', 0, 0));
        }
        fn pending_interrupts(&self) -> u8 {
            self.ie & self.iflag & 31
        }
        fn clear_interrupt(&mut self, m: u8) {
            self.iflag &= !m;
        }
        fn stop(&mut self) -> bool {
            self.switch
        }
    }
    #[test]
    fn memory_hooks_observe_current_pc_ime_and_cycle_boundary() {
        #[derive(Default)]
        struct ContextBus {
            bus: Bus,
            context: (u16, bool, u64),
            observed: Vec<(u16, bool, u64)>,
        }
        impl CpuBus for ContextBus {
            fn set_cpu_state(&mut self, pc: u16, ime: bool, tcycles: u64) {
                self.context = (pc, ime, tcycles);
            }
            fn read(&mut self, address: u16) -> u8 {
                self.observed.push(self.context);
                self.bus.read(address)
            }
            fn write(&mut self, address: u16, value: u8) {
                self.observed.push(self.context);
                self.bus.write(address, value);
            }
            fn internal_cycle(&mut self) {
                self.observed.push(self.context);
                self.bus.internal_cycle();
            }
            fn pending_interrupts(&self) -> u8 {
                self.bus.pending_interrupts()
            }
            fn clear_interrupt(&mut self, mask: u8) {
                self.bus.clear_interrupt(mask);
            }
            fn stop(&mut self) -> bool {
                self.bus.stop()
            }
        }
        let mut c = Cpu::default();
        let mut b = ContextBus::default();
        c.registers.pc = 0x100;
        c.registers.sp = 0xdffe;
        b.bus.mem[0x100..0x106].copy_from_slice(&[0xfb, 0, 0xea, 0, 0xc0, 0xc5]);
        for _ in 0..4 {
            c.step(&mut b);
        }
        assert_eq!(
            b.observed,
            [
                (0x100, false, 4),
                (0x101, false, 8),
                (0x102, true, 12),
                (0x103, true, 16),
                (0x104, true, 20),
                (0x105, true, 24),
                (0x105, true, 28),
                (0x106, true, 32),
                (0x106, true, 36),
                (0x106, true, 40)
            ]
        );
    }

    #[test]
    fn immediate_add_flags_and_cycles() {
        let mut c = Cpu::default();
        let mut b = Bus::default();
        b.mem[..4].copy_from_slice(&[0x3e, 0xff, 0xc6, 1]);
        c.step(&mut b);
        c.step(&mut b);
        assert_eq!(
            (
                c.registers.a,
                c.registers.f,
                c.registers.pc,
                c.total_tcycles
            ),
            (0, 0xb0, 4, 16)
        );
    }
    #[test]
    fn ei_delay_and_interrupt_stack() {
        let mut c = Cpu::default();
        let mut b = Bus::default();
        b.mem[0] = 0xfb;
        b.ie = 1;
        b.iflag = 1;
        c.registers.sp = 0xc000;
        c.step(&mut b);
        assert!(!c.ime);
        assert_eq!(c.registers.pc, 1);
        c.step(&mut b);
        assert_eq!(
            (c.registers.pc, c.registers.sp, c.total_tcycles),
            (0x40, 0xbffe, 28)
        );
        assert_eq!(
            &b.events[2..],
            &[
                ('i', 0, 0),
                ('i', 0, 0),
                ('w', 0xbfff, 0),
                ('w', 0xbffe, 2),
                ('i', 0, 0)
            ]
        );
    }
    #[test]
    fn halt_bug_reuses_opcode_as_immediate() {
        let mut c = Cpu::default();
        let mut b = Bus::default();
        b.mem[..3].copy_from_slice(&[0x76, 0x3e, 0x12]);
        b.ie = 1;
        b.iflag = 1;
        c.step(&mut b);
        assert!(!c.halted);
        c.step(&mut b);
        assert_eq!((c.registers.a, c.registers.pc), (0x3e, 2));
    }
    #[test]
    fn interrupt_high_push_cancels_but_low_push_is_late() {
        for (sp, pc, vector, flag) in [(0, 0x1200, 0, 1), (1, 0x1200, 0x40, 0)] {
            let mut c = Cpu::default();
            let mut b = Bus::default();
            c.ime = true;
            c.registers.sp = sp;
            c.registers.pc = pc;
            b.ie = 1;
            b.iflag = 1;
            c.step(&mut b);
            assert_eq!((c.registers.pc, b.iflag), (vector, flag));
            assert_eq!(c.total_tcycles, 24);
        }
    }
    #[test]
    fn interrupt_priority_reselects_after_high_push() {
        let mut c = Cpu::default();
        let mut b = Bus::default();
        c.ime = true;
        c.registers.pc = 0x0201;
        c.registers.sp = 0;
        b.ie = 3;
        b.iflag = 3;
        c.step(&mut b);
        assert_eq!((c.registers.pc, b.ie, b.iflag), (0x48, 2, 1));
        for bit in 0..5 {
            c.reset();
            c.ime = true;
            c.registers.sp = 0xc000;
            b.mem[0] = 0;
            b.ie = 31;
            b.iflag = 31 << bit;
            c.step(&mut b);
            assert_eq!(c.registers.pc, 0x40 + bit as u16 * 8);
            assert_eq!(b.iflag & (1 << bit), 0);
        }
    }
    #[test]
    fn stop_consumes_padding_and_switches_or_stops() {
        for switch in [false, true] {
            let mut c = Cpu::default();
            let mut b = Bus::default();
            b.mem[0] = 0x10;
            b.switch = switch;
            c.step(&mut b);
            assert_eq!(
                (c.stopped, c.registers.pc, c.total_tcycles),
                (!switch, 2, 8)
            );
        }
    }
    #[test]
    fn every_unprefixed_opcode_has_reference_cycle_cost() {
        let rows: [[u8; 16]; 16] = [
            [4, 12, 8, 8, 4, 4, 8, 4, 20, 8, 8, 8, 4, 4, 8, 4],
            [8, 12, 8, 8, 4, 4, 8, 4, 12, 8, 8, 8, 4, 4, 8, 4],
            [8, 12, 8, 8, 4, 4, 8, 4, 8, 8, 8, 8, 4, 4, 8, 4],
            [8, 12, 8, 8, 12, 12, 12, 4, 8, 8, 8, 8, 4, 4, 8, 4],
            [4, 4, 4, 4, 4, 4, 8, 4, 4, 4, 4, 4, 4, 4, 8, 4],
            [4, 4, 4, 4, 4, 4, 8, 4, 4, 4, 4, 4, 4, 4, 8, 4],
            [4, 4, 4, 4, 4, 4, 8, 4, 4, 4, 4, 4, 4, 4, 8, 4],
            [8, 8, 8, 8, 8, 8, 4, 8, 4, 4, 4, 4, 4, 4, 8, 4],
            [4, 4, 4, 4, 4, 4, 8, 4, 4, 4, 4, 4, 4, 4, 8, 4],
            [4, 4, 4, 4, 4, 4, 8, 4, 4, 4, 4, 4, 4, 4, 8, 4],
            [4, 4, 4, 4, 4, 4, 8, 4, 4, 4, 4, 4, 4, 4, 8, 4],
            [4, 4, 4, 4, 4, 4, 8, 4, 4, 4, 4, 4, 4, 4, 8, 4],
            [8, 12, 12, 16, 12, 16, 8, 16, 8, 16, 12, 8, 12, 24, 8, 16],
            [8, 12, 12, 4, 12, 16, 8, 16, 8, 16, 12, 4, 12, 4, 8, 16],
            [12, 12, 8, 4, 4, 16, 8, 16, 16, 4, 16, 4, 4, 4, 8, 16],
            [12, 12, 8, 4, 4, 16, 8, 16, 12, 8, 16, 4, 4, 4, 8, 16],
        ];
        for op in 0..=255u8 {
            for flags in [0, 0x10, 0x80, 0x90] {
                let mut c = Cpu::default();
                let mut b = Bus::default();
                c.registers.pc = 0x100;
                c.registers.sp = 0xc100;
                c.registers.set_hl(0xc000);
                c.registers.f = flags;
                b.mem[0x100] = op;
                let mut expected = rows[op as usize / 16][op as usize % 16] as u64;
                let cc = match op {
                    0x20 | 0x28 | 0x30 | 0x38 => Some(((op >> 3) & 7) - 4),
                    0xc0 | 0xc8 | 0xd0 | 0xd8 | 0xc2 | 0xca | 0xd2 | 0xda | 0xc4 | 0xcc | 0xd4
                    | 0xdc => Some((op >> 3) & 7),
                    _ => None,
                };
                if cc.is_some_and(|cc| c.cond(cc)) {
                    expected += match op & 7 {
                        0 => {
                            if op < 0x40 {
                                4
                            } else {
                                12
                            }
                        }
                        2 => 4,
                        4 => 12,
                        _ => unreachable!(),
                    };
                }
                c.step(&mut b);
                assert_eq!(c.total_tcycles, expected, "op {op:02x} flags {flags:02x}");
                assert_eq!(b.events.len() as u64 * 4, expected);
                let illegal = matches!(
                    op,
                    0xd3 | 0xdb | 0xdd | 0xe3 | 0xe4 | 0xeb | 0xec | 0xed | 0xf4 | 0xfc | 0xfd
                );
                assert_eq!(c.halted, illegal || op == 0x76, "op {op:02x}");
            }
        }
    }
    #[test]
    fn every_cb_opcode_value_flags_and_cycles() {
        for op in 0..=255u8 {
            for value in 0..=255u8 {
                for carry in [0, 0x10] {
                    let mut c = Cpu::default();
                    let mut b = Bus::default();
                    let reg = op & 7;
                    let bit = (op >> 3) & 7;
                    let group = op >> 6;
                    c.registers.pc = 0x100;
                    c.registers.set_hl(0xc000);
                    c.registers.f = carry | 0x60;
                    c.set_reg(&mut b, reg, value);
                    b.events.clear();
                    c.total_tcycles = 0;
                    b.mem[0x100] = 0xcb;
                    b.mem[0x101] = op;
                    let (result, flags) = match group {
                        0 => {
                            let old_c = carry >> 4;
                            let (v, c) = match bit {
                                0 => ((value as u16 * 2 + value as u16 / 128) as u8, value / 128),
                                1 => (value / 2 + value % 2 * 128, value % 2),
                                2 => (value.wrapping_mul(2) + old_c, value / 128),
                                3 => (value / 2 + old_c * 128, value % 2),
                                4 => (value.wrapping_mul(2), value / 128),
                                5 => (value / 2 + (value & 128), value % 2),
                                6 => (value / 16 + value % 16 * 16, 0),
                                _ => (value / 2, value % 2),
                            };
                            (v, if v == 0 { 0x80 } else { 0 } | c * 16)
                        }
                        1 => (
                            value,
                            carry | 0x20 | if value & (1 << bit) == 0 { 0x80 } else { 0 },
                        ),
                        2 => (value & !(1 << bit), carry | 0x60),
                        _ => (value | (1 << bit), carry | 0x60),
                    };
                    c.step(&mut b);
                    let cycles = if reg != 6 {
                        8
                    } else if group == 1 {
                        12
                    } else {
                        16
                    };
                    assert_eq!(
                        (c.total_tcycles, c.registers.f),
                        (cycles, flags),
                        "CB {op:02x}, {value:02x}, carry {carry}"
                    );
                    assert_eq!(c.reg(&mut b, reg), result, "CB {op:02x}, {value:02x}");
                }
            }
        }
    }
    #[test]
    fn reti_is_immediate_di_cancels_ei_and_halt_wakes_cleanly() {
        for pending in [false, true] {
            let mut c = Cpu::default();
            let mut b = Bus::default();
            c.registers.sp = 0xc000;
            b.mem[0] = 0xd9;
            b.mem[0xc000] = 0x34;
            b.mem[0xc001] = 0x12;
            b.ie = 1;
            b.iflag = u8::from(pending);
            c.step(&mut b);
            assert_eq!(
                (c.registers.pc, c.ime, c.total_tcycles),
                if pending {
                    (0x40, false, 36)
                } else {
                    (0x1234, true, 16)
                }
            );
        }
        let mut c = Cpu::default();
        let mut b = Bus::default();
        b.mem[..3].copy_from_slice(&[0xfb, 0xf3, 0]);
        b.ie = 1;
        b.iflag = 1;
        for _ in 0..3 {
            c.step(&mut b);
        }
        assert!(!c.ime);
        assert_eq!(c.registers.pc, 3);
        c.reset();
        b.ie = 0;
        b.iflag = 0;
        b.mem[..3].copy_from_slice(&[0x76, 0x3c, 0]);
        c.step(&mut b);
        assert!(c.halted);
        b.ie = 1;
        b.iflag = 1;
        c.step(&mut b);
        assert!(!c.halted);
        assert_eq!(c.registers.pc, 1);
        c.step(&mut b);
        assert_eq!((c.registers.a, c.registers.pc), (1, 2));
    }
    #[test]
    fn state_order_latches_roundtrip_and_truncation() {
        let bytes: Vec<u8> = (1..=12).chain([1, 0, 1, 1, 0, 1]).chain(19..=26).collect();
        let mut c = Cpu::default();
        c.read_state(&mut StateReader::new(&bytes)).unwrap();
        let mut w = StateWriter::default();
        c.write_state(&mut w);
        assert_eq!(w.bytes, bytes);
        assert_eq!((c.registers.sp, c.registers.pc), (0x0a09, 0x0c0b));
        assert!(c.ei_pending && c.halt_bug_next_fetch);
        assert!(!c.ei_armed);
        for n in 0..bytes.len() {
            assert_eq!(
                c.read_state(&mut StateReader::new(&bytes[..n]))
                    .unwrap_err()
                    .kind(),
                io::ErrorKind::UnexpectedEof
            );
            let mut w = StateWriter::default();
            c.write_state(&mut w);
            assert_eq!(w.bytes, bytes);
        }
    }
    #[test]
    fn stack_call_return_and_address_wrap() {
        let mut c = Cpu::default();
        let mut b = Bus::default();
        c.registers.pc = 0xffff;
        b.mem[0xffff] = 0x01;
        b.mem[0] = 0x34;
        b.mem[1] = 0x12;
        c.step(&mut b);
        assert_eq!((c.registers.bc(), c.registers.pc), (0x1234, 2));
        c.reset();
        c.registers.sp = 0;
        b.mem[..3].copy_from_slice(&[0xcd, 0x10, 0]);
        b.mem[0x10] = 0xc9;
        c.step(&mut b);
        assert_eq!((c.registers.pc, c.registers.sp), (0x10, 0xfffe));
        assert_eq!(&b.mem[0xfffe..], &[3, 0]);
        c.step(&mut b);
        assert_eq!((c.registers.pc, c.registers.sp), (3, 0));
        c.reset();
        c.registers.a = 0x42;
        c.registers.set_hl(0xffff);
        b.mem[0] = 0x22;
        c.step(&mut b);
        assert_eq!((b.mem[0xffff], c.registers.hl()), (0x42, 0));
    }
    #[test]
    fn independent_csharp_cpu_state_and_timed_bus_trace() {
        struct OracleBus {
            events: Vec<(bool, u16, u8, u64)>,
            index: usize,
            ticks: u64,
        }
        impl CpuBus for OracleBus {
            fn read(&mut self, a: u16) -> u8 {
                self.ticks += 4;
                let (write, address, value, ticks) = self.events[self.index];
                self.index += 1;
                assert_eq!((false, a, self.ticks), (write, address, ticks));
                value
            }
            fn write(&mut self, a: u16, v: u8) {
                self.ticks += 4;
                let event = self.events[self.index];
                self.index += 1;
                assert_eq!((true, a, v, self.ticks), event);
            }
            fn internal_cycle(&mut self) {
                self.ticks += 4;
            }
            fn pending_interrupts(&self) -> u8 {
                0
            }
            fn clear_interrupt(&mut self, _: u8) {
                panic!("unexpected interrupt")
            }
            fn stop(&mut self) -> bool {
                false
            }
        }
        let mut data = StateReader::new(include_bytes!("cpu/oracle.bin"));
        let count = data.read_u32().unwrap();
        assert_eq!(count, 2048);
        for _ in 0..count {
            let prefixed = data.read_bool().unwrap();
            let op = data.read_byte().unwrap();
            let variant = data.read_byte().unwrap() as usize;
            let expected = data.read_bytes(26).unwrap();
            let len = data.read_byte().unwrap();
            let mut events = vec![];
            for _ in 0..len {
                events.push((
                    data.read_bool().unwrap(),
                    data.read_u16().unwrap(),
                    data.read_byte().unwrap(),
                    data.read_u64().unwrap(),
                ));
            }
            let mut b = OracleBus {
                events,
                index: 0,
                ticks: 0,
            };
            let mut c = Cpu::default();
            c.registers.pc = 0x100;
            c.registers.sp = 0xdffe;
            c.registers.a = [0, 1, 0x7f, 0xff][variant];
            c.registers.f = [0, 0x10, 0x80, 0xf0][variant];
            c.registers.set_bc(0xc180);
            c.registers.set_de(0xc234);
            c.registers.set_hl(0xc123);
            c.step(&mut b);
            let mut state = StateWriter::default();
            c.write_state(&mut state);
            assert_eq!(
                state.bytes, expected,
                "prefixed {prefixed} opcode {op:02x} variant {variant}"
            );
            assert_eq!(b.index, b.events.len());
            assert_eq!(b.ticks, c.total_tcycles);
        }
        assert_eq!(data.remaining(), 0);
    }
}
