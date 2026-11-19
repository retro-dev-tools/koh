use crate::state::{StateReader, StateWriter};
use std::io;

#[derive(Clone, Default)]
pub struct OamDma {
    pub source_high_byte: u8,
    pub is_bus_locking: bool,
    start: i32,
    index: i32,
    byte_countdown: i32,
    running: bool,
}
impl OamDma {
    pub fn trigger(&mut self, value: u8) {
        self.source_high_byte = value;
        self.start = 4;
        self.index = 0;
        self.byte_countdown = 4;
        self.running = true;
    }
    pub fn tick_t(&mut self) -> Option<(u16, usize)> {
        if !self.running {
            self.is_bus_locking = false;
            return None;
        }
        if self.start > 0 {
            self.start -= 1;
            if self.start != 0 {
                return None;
            }
            self.is_bus_locking = true;
        } else {
            self.byte_countdown -= 1;
            if self.byte_countdown != 0 {
                return None;
            }
            self.index += 1;
            if self.index >= 160 {
                self.running = false;
                self.is_bus_locking = false;
                return None;
            }
            self.byte_countdown = 4;
        }
        let mut source = ((self.source_high_byte as u16) << 8) | self.index as u16;
        if source >= 0xe000 {
            source &= 0xdfff;
        }
        Some((source, self.index as usize))
    }
    pub fn write_state(&self, w: &mut StateWriter) {
        w.write_byte(self.source_high_byte);
        for n in [self.start, self.index, self.byte_countdown] {
            w.write_i32(n);
        }
        w.write_bool(self.running);
        w.write_bool(self.is_bus_locking);
    }
    pub fn read_state(&mut self, r: &mut StateReader<'_>) -> io::Result<()> {
        self.source_high_byte = r.read_byte()?;
        self.start = r.read_i32()?;
        self.index = r.read_i32()?;
        self.byte_countdown = r.read_i32()?;
        self.running = r.read_bool()?;
        self.is_bus_locking = r.read_bool()?;
        if !(0..=4).contains(&self.start)
            || !(0..=160).contains(&self.index)
            || !(0..=4).contains(&self.byte_countdown)
            || (self.running && self.index >= 160)
            || (self.running && self.byte_countdown == 0)
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "invalid OAM DMA state",
            ));
        }
        Ok(())
    }
}
#[derive(Clone, Default)]
pub struct Hdma {
    pub source1: u8,
    pub source2: u8,
    pub dest1: u8,
    pub dest2: u8,
    pub is_hblank_mode: bool,
    pub active: bool,
    pub cpu_halted_by_gp: bool,
    pub current_source: u16,
    pub current_dest: u16,
    remaining: i32,
    index: i32,
    pending: bool,
}
impl Hdma {
    pub fn read_length_register(&self) -> u8 {
        if !self.active {
            255
        } else {
            (if self.is_hblank_mode { 128 } else { 0 }) | (((self.remaining / 16 - 1) & 127) as u8)
        }
    }
    pub fn write_length_register(&mut self, value: u8) {
        if self.active && self.is_hblank_mode && value & 128 == 0 {
            self.active = false;
            self.cpu_halted_by_gp = false;
            return;
        }
        self.is_hblank_mode = value & 128 != 0;
        self.remaining = ((value & 127) as i32 + 1) * 16;
        self.index = 0;
        self.active = true;
        self.cpu_halted_by_gp = !self.is_hblank_mode;
        self.current_source = u16::from_be_bytes([self.source1, self.source2]) & 0xfff0;
        self.current_dest = 0x8000 | (u16::from_be_bytes([self.dest1, self.dest2]) & 0x1ff0);
        self.pending = false;
    }
    pub fn block_len(&self, hblank: bool) -> usize {
        if (hblank && (!self.active || !self.is_hblank_mode)) || (!hblank && !self.cpu_halted_by_gp)
        {
            0
        } else {
            self.remaining.min(16) as usize
        }
    }
    pub fn advance_byte(&mut self) {
        self.current_source = self.current_source.wrapping_add(1);
        self.current_dest = self.current_dest.wrapping_add(1);
        self.remaining -= 1;
    }
    pub fn finish_block(&mut self, hblank: bool) {
        if hblank {
            self.index = 0;
            self.pending = false;
        }
        if self.remaining <= 0 {
            self.active = false;
            if !hblank {
                self.cpu_halted_by_gp = false;
            }
        }
    }
    pub fn tick_t(&mut self) {
        if !self.active {
            self.cpu_halted_by_gp = false;
        }
    }
    pub fn write_state(&self, w: &mut StateWriter) {
        w.write_bytes(&[self.source1, self.source2, self.dest1, self.dest2]);
        for flag in [self.is_hblank_mode, self.active, self.cpu_halted_by_gp] {
            w.write_bool(flag);
        }
        w.write_u16(self.current_source);
        w.write_u16(self.current_dest);
        w.write_i32(self.remaining);
        w.write_i32(self.index);
        w.write_bool(self.pending);
    }
    pub fn read_state(&mut self, r: &mut StateReader<'_>) -> io::Result<()> {
        self.source1 = r.read_byte()?;
        self.source2 = r.read_byte()?;
        self.dest1 = r.read_byte()?;
        self.dest2 = r.read_byte()?;
        self.is_hblank_mode = r.read_bool()?;
        self.active = r.read_bool()?;
        self.cpu_halted_by_gp = r.read_bool()?;
        self.current_source = r.read_u16()?;
        self.current_dest = r.read_u16()?;
        self.remaining = r.read_i32()?;
        self.index = r.read_i32()?;
        self.pending = r.read_bool()?;
        if !(0..=2048).contains(&self.remaining)
            || !(0..16).contains(&self.index)
            || (self.active && self.remaining == 0)
            || (self.cpu_halted_by_gp && (!self.active || self.is_hblank_mode))
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "invalid HDMA state",
            ));
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn invalid_active_dma_state_is_rejected() {
        let mut dma = Hdma::default();
        dma.write_length_register(0);
        let mut w = StateWriter::default();
        dma.write_state(&mut w);
        w.bytes[11..15].copy_from_slice(&0i32.to_le_bytes());
        assert!(
            Hdma::default()
                .read_state(&mut StateReader::new(&w.bytes))
                .is_err()
        );
        let mut dma = OamDma::default();
        dma.trigger(0xc0);
        let mut w = StateWriter::default();
        dma.write_state(&mut w);
        w.bytes[9..13].copy_from_slice(&0i32.to_le_bytes());
        assert!(
            OamDma::default()
                .read_state(&mut StateReader::new(&w.bytes))
                .is_err()
        );
    }
}
