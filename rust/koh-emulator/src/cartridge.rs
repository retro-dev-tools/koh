use crate::state::{StateReader, StateWriter};
use std::{
    io,
    time::{SystemTime, UNIX_EPOCH},
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum MapperKind {
    RomOnly = 0,
    Mbc1 = 1,
    Mbc3 = 3,
    Mbc5 = 5,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CartridgeHeader {
    pub title: String,
    pub mapper_kind: MapperKind,
    pub rom_banks: usize,
    pub ram_banks: usize,
    pub cgb_flag: bool,
    pub cgb_only: bool,
}
impl CartridgeHeader {
    pub fn parse(rom: &[u8]) -> Result<Self, String> {
        if rom.len() < 0x150 {
            return Err("ROM smaller than header size".into());
        }
        let cgb_flag = rom[0x143] & 0x80 != 0;
        let title = rom[0x134..0x134 + if cgb_flag { 15 } else { 16 }]
            .iter()
            .take_while(|&&v| v != 0)
            .map(|&v| if v < 128 { v as char } else { '?' })
            .collect();
        let mapper_kind = match rom[0x147] {
            0 => MapperKind::RomOnly,
            1..=3 => MapperKind::Mbc1,
            0xf..=0x13 => MapperKind::Mbc3,
            0x19..=0x1e => MapperKind::Mbc5,
            v => return Err(format!("Cartridge type ${v:02X} not supported")),
        };
        let rom_banks = match rom[0x148] {
            v @ 0..=8 => 2usize << v,
            _ => 2,
        };
        let ram_banks = match rom[0x149] {
            2 => 1,
            3 => 4,
            4 => 16,
            5 => 8,
            _ => 0,
        };
        Ok(Self {
            title,
            mapper_kind,
            rom_banks,
            ram_banks,
            cgb_flag,
            cgb_only: rom[0x143] == 0xc0,
        })
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Rtc {
    pub seconds: u8,
    pub minutes: u8,
    pub hours: u8,
    pub day_low: u8,
    pub day_high_and_flags: u8,
    pub latched_seconds: u8,
    pub latched_minutes: u8,
    pub latched_hours: u8,
    pub latched_day_low: u8,
    pub latched_day_high_and_flags: u8,
    pub base_unix_seconds: i64,
}
impl Rtc {
    pub fn latch(&mut self) {
        self.latched_seconds = self.seconds;
        self.latched_minutes = self.minutes;
        self.latched_hours = self.hours;
        self.latched_day_low = self.day_low;
        self.latched_day_high_and_flags = self.day_high_and_flags;
    }
    pub fn advance_from_host(&mut self, current_unix_seconds: i64) {
        if self.base_unix_seconds == 0 || self.day_high_and_flags & 0x40 != 0 {
            self.base_unix_seconds = current_unix_seconds;
            return;
        }
        let delta = current_unix_seconds.wrapping_sub(self.base_unix_seconds);
        self.base_unix_seconds = current_unix_seconds;
        if delta <= 0 {
            return;
        }
        let seconds = (self.seconds as i64).wrapping_add(delta);
        self.seconds = (seconds % 60) as u8;
        let minutes = self.minutes as i64 + seconds / 60;
        self.minutes = (minutes % 60) as u8;
        let hours = self.hours as i64 + minutes / 60;
        self.hours = (hours % 24) as u8;
        let mut day =
            ((self.day_high_and_flags as i64 & 1) << 8 | self.day_low as i64) + hours / 24;
        if day > 0x1ff {
            self.day_high_and_flags |= 0x80;
            day &= 0x1ff;
        }
        self.day_low = day as u8;
        self.day_high_and_flags = (self.day_high_and_flags & 0xfe) | ((day >> 8) & 1) as u8;
    }
    pub fn write_state(&self, w: &mut StateWriter) {
        for v in [
            self.seconds,
            self.minutes,
            self.hours,
            self.day_low,
            self.day_high_and_flags,
            self.latched_seconds,
            self.latched_minutes,
            self.latched_hours,
            self.latched_day_low,
            self.latched_day_high_and_flags,
        ] {
            w.write_byte(v);
        }
        w.write_i64(self.base_unix_seconds);
    }
    pub fn read_state(&mut self, r: &mut StateReader<'_>) -> io::Result<()> {
        let next = Self {
            seconds: r.read_byte()?,
            minutes: r.read_byte()?,
            hours: r.read_byte()?,
            day_low: r.read_byte()?,
            day_high_and_flags: r.read_byte()?,
            latched_seconds: r.read_byte()?,
            latched_minutes: r.read_byte()?,
            latched_hours: r.read_byte()?,
            latched_day_low: r.read_byte()?,
            latched_day_high_and_flags: r.read_byte()?,
            base_unix_seconds: r.read_i64()?,
        };
        *self = next;
        Ok(())
    }
}

#[derive(Clone, Debug)]
pub struct Cartridge {
    pub header: CartridgeHeader,
    pub kind: MapperKind,
    pub rom: Vec<u8>,
    pub ram: Vec<u8>,
    pub mbc1_bank_low: u8,
    pub mbc1_bank_high: u8,
    pub mbc1_ram_enabled: bool,
    pub mbc1_mode: u8,
    pub mbc3_latch_latch: u8,
    pub rtc: Rtc,
    pub mbc5_ram_bank: u8,
}
impl Cartridge {
    pub fn load(rom: &[u8]) -> Result<Self, String> {
        let header = CartridgeHeader::parse(rom)?;
        Ok(Self {
            kind: header.mapper_kind,
            ram: vec![0; header.ram_banks * 0x2000],
            header,
            rom: rom.to_vec(),
            mbc1_bank_low: 1,
            mbc1_bank_high: 0,
            mbc1_ram_enabled: false,
            mbc1_mode: 0,
            mbc3_latch_latch: 0,
            rtc: Rtc::default(),
            mbc5_ram_bank: 0,
        })
    }
    pub fn read_rom(&self, address: u16) -> u8 {
        let offset = if self.kind == MapperKind::RomOnly {
            address as usize
        } else if address < 0x4000 {
            let bank = if self.kind == MapperKind::Mbc1 && self.mbc1_mode == 1 {
                (self.mbc1_bank_high as usize) << 5
            } else {
                0
            };
            bank * 0x4000 + address as usize
        } else {
            let bank = match self.kind {
                MapperKind::Mbc1 => {
                    ((self.mbc1_bank_high as usize) << 5)
                        | usize::from((self.mbc1_bank_low & 31).max(1))
                }
                MapperKind::Mbc3 => usize::from((self.mbc1_bank_low & 127).max(1)),
                MapperKind::Mbc5 => {
                    ((self.mbc1_bank_high as usize) << 8) | self.mbc1_bank_low as usize
                }
                MapperKind::RomOnly => unreachable!(),
            };
            bank * 0x4000 + address as usize - 0x4000
        };
        self.rom.get(offset).copied().unwrap_or(0xff)
    }
    pub fn write_rom(&mut self, address: u16, value: u8) {
        if self.kind == MapperKind::RomOnly {
            return;
        }
        if address < 0x2000 {
            self.mbc1_ram_enabled = value & 15 == 10;
            return;
        }
        match self.kind {
            MapperKind::Mbc1 => {
                if address < 0x4000 {
                    self.mbc1_bank_low = (value & 31).max(1)
                } else if address < 0x6000 {
                    self.mbc1_bank_high = value & 3
                } else {
                    self.mbc1_mode = value & 1
                }
            }
            MapperKind::Mbc3 => {
                if address < 0x4000 {
                    self.mbc1_bank_low = value & 127
                } else if address < 0x6000 {
                    self.mbc1_bank_high = value
                } else {
                    if self.mbc3_latch_latch == 0 && value == 1 {
                        let now = match SystemTime::now().duration_since(UNIX_EPOCH) {
                            Ok(v) => v.as_secs() as i64,
                            Err(v) => {
                                -(v.duration().as_secs() as i64)
                                    - i64::from(v.duration().subsec_nanos() != 0)
                            }
                        };
                        self.rtc.advance_from_host(now);
                        self.rtc.latch();
                    }
                    self.mbc3_latch_latch = value;
                }
            }
            MapperKind::Mbc5 => {
                if address < 0x3000 {
                    self.mbc1_bank_low = value
                } else if address < 0x4000 {
                    self.mbc1_bank_high = value & 1
                } else if address < 0x6000 {
                    self.mbc5_ram_bank = value & 15
                }
            }
            MapperKind::RomOnly => {}
        }
    }
    fn ram_offset(&self, address: u16) -> Option<usize> {
        if !self.mbc1_ram_enabled || self.ram.is_empty() {
            return None;
        }
        let bank = match self.kind {
            MapperKind::Mbc1 => {
                if self.mbc1_mode == 1 {
                    self.mbc1_bank_high
                } else {
                    0
                }
            }
            MapperKind::Mbc3 if self.mbc1_bank_high < 4 => self.mbc1_bank_high,
            MapperKind::Mbc5 => self.mbc5_ram_bank,
            _ => return None,
        };
        let offset = bank as i32 * 0x2000 + address as i32 - 0xa000;
        usize::try_from(offset)
            .ok()
            .filter(|&offset| offset < self.ram.len())
    }
    pub fn read_ram(&self, address: u16) -> u8 {
        if self.kind == MapperKind::Mbc3 && self.mbc1_ram_enabled && self.mbc1_bank_high >= 4 {
            return match self.mbc1_bank_high {
                8 => self.rtc.latched_seconds,
                9 => self.rtc.latched_minutes,
                10 => self.rtc.latched_hours,
                11 => self.rtc.latched_day_low,
                12 => self.rtc.latched_day_high_and_flags,
                _ => 0xff,
            };
        }
        self.ram_offset(address)
            .map_or(0xff, |offset| self.ram[offset])
    }
    pub fn write_ram(&mut self, address: u16, value: u8) {
        if self.kind == MapperKind::Mbc3 && self.mbc1_ram_enabled && self.mbc1_bank_high >= 4 {
            match self.mbc1_bank_high {
                8 => self.rtc.seconds = value & 63,
                9 => self.rtc.minutes = value & 63,
                10 => self.rtc.hours = value & 31,
                11 => self.rtc.day_low = value,
                12 => self.rtc.day_high_and_flags = value & 0xc1,
                _ => {}
            }
            return;
        }
        if let Some(offset) = self.ram_offset(address) {
            self.ram[offset] = value;
        }
    }
    pub fn current_rom_bank(&self) -> u8 {
        match self.kind {
            MapperKind::RomOnly => 1,
            MapperKind::Mbc1 => (self.mbc1_bank_high << 5) | (self.mbc1_bank_low & 31).max(1),
            MapperKind::Mbc3 => (self.mbc1_bank_low & 127).max(1),
            MapperKind::Mbc5 => self.mbc1_bank_low,
        }
    }
    pub fn write_state(&self, w: &mut StateWriter) {
        w.write_i32(self.ram.len() as i32);
        w.write_bytes(&self.ram);
        w.write_byte(self.mbc1_bank_low);
        w.write_byte(self.mbc1_bank_high);
        w.write_bool(self.mbc1_ram_enabled);
        w.write_byte(self.mbc1_mode);
        w.write_byte(self.mbc3_latch_latch);
        self.rtc.write_state(w);
        w.write_byte(self.mbc5_ram_bank);
    }
    pub fn read_state(&mut self, r: &mut StateReader<'_>) -> io::Result<()> {
        if r.read_i32()? != self.ram.len() as i32 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "cartridge RAM size mismatch",
            ));
        }
        let ram = r.read_bytes(self.ram.len())?;
        let low = r.read_byte()?;
        let high = r.read_byte()?;
        let enabled = r.read_bool()?;
        let mode = r.read_byte()?;
        let latch = r.read_byte()?;
        let mut rtc = Rtc::default();
        rtc.read_state(r)?;
        let ram_bank = r.read_byte()?;
        self.ram.copy_from_slice(ram);
        self.mbc1_bank_low = low;
        self.mbc1_bank_high = high;
        self.mbc1_ram_enabled = enabled;
        self.mbc1_mode = mode;
        self.mbc3_latch_latch = latch;
        self.rtc = rtc;
        self.mbc5_ram_bank = ram_bank;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn cart(kind: u8, rom_banks: usize, ram_code: u8) -> Cartridge {
        let mut rom = vec![0; rom_banks * 0x4000];
        rom[0x147] = kind;
        rom[0x148] = rom_banks.trailing_zeros() as u8 - 1;
        rom[0x149] = ram_code;
        for bank in 0..rom_banks {
            rom[bank * 0x4000] = bank as u8;
            rom[bank * 0x4000 + 1] = (bank >> 8) as u8;
        }
        Cartridge::load(&rom).unwrap()
    }
    #[test]
    fn header_mapper_sizes_title_and_cgb_flags() {
        for (code, kind) in [
            (0, MapperKind::RomOnly),
            (1, MapperKind::Mbc1),
            (2, MapperKind::Mbc1),
            (3, MapperKind::Mbc1),
            (0xf, MapperKind::Mbc3),
            (0x10, MapperKind::Mbc3),
            (0x11, MapperKind::Mbc3),
            (0x12, MapperKind::Mbc3),
            (0x13, MapperKind::Mbc3),
            (0x19, MapperKind::Mbc5),
            (0x1a, MapperKind::Mbc5),
            (0x1b, MapperKind::Mbc5),
            (0x1c, MapperKind::Mbc5),
            (0x1d, MapperKind::Mbc5),
            (0x1e, MapperKind::Mbc5),
        ] {
            let mut rom = vec![0; 0x150];
            rom[0x147] = code;
            rom[0x148] = 3;
            rom[0x149] = 3;
            rom[0x134..0x138].copy_from_slice(b"TEST");
            let h = CartridgeHeader::parse(&rom).unwrap();
            assert_eq!(
                (
                    h.mapper_kind,
                    h.rom_banks,
                    h.ram_banks,
                    h.title.as_str(),
                    h.cgb_flag
                ),
                (kind, 16, 4, "TEST", false)
            );
            for (flag, only) in [(0x80, false), (0xc0, true)] {
                rom[0x143] = flag;
                let h = CartridgeHeader::parse(&rom).unwrap();
                assert!(h.cgb_flag);
                assert_eq!(h.cgb_only, only);
            }
        }
    }
    #[test]
    fn mbc1_bank_zero_remap_high_bits_and_ram_modes() {
        let mut c = cart(3, 128, 3);
        assert_eq!((c.read_rom(0), c.read_rom(0x4000)), (0, 1));
        assert_eq!(c.read_ram(0xa000), 0xff);
        c.write_rom(0x2000, 2);
        assert_eq!(c.read_rom(0x4000), 2);
        c.write_rom(0x2000, 0);
        assert_eq!(c.read_rom(0x4000), 1);
        c.write_rom(0x4000, 2);
        assert_eq!(c.read_rom(0x4000), 65);
        c.write_rom(0x6000, 1);
        assert_eq!(c.read_rom(0), 64);
        c.write_rom(0, 0x1a);
        c.write_ram(0xa000, 0x42);
        assert_eq!(c.read_ram(0xa000), 0x42);
        c.write_rom(0x6000, 0);
        assert_eq!(c.read_ram(0xa000), 0);
        c.write_ram(0xa000, 0x11);
        c.write_rom(0x6000, 1);
        assert_eq!(c.read_ram(0xa000), 0x42);
    }
    #[test]
    fn mbc3_rom_ram_gating_and_rtc_latch() {
        let mut c = cart(0x13, 4, 3);
        assert_eq!(c.read_rom(0x4000), 1);
        c.write_rom(0x2000, 2);
        assert_eq!(c.read_rom(0x4000), 2);
        c.write_rom(0x2000, 0);
        assert_eq!(c.read_rom(0x4000), 1);
        c.write_ram(0xa000, 0x77);
        assert_eq!(c.read_ram(0xa000), 0xff);
        c.write_rom(0, 0xa);
        c.write_ram(0xa000, 0x77);
        assert_eq!(c.read_ram(0xa000), 0x77);
        c.write_rom(0x4000, 8);
        c.write_ram(0xa000, 42);
        assert_eq!(c.read_ram(0xa000), 0);
        c.write_rom(0x6000, 0);
        c.write_rom(0x6000, 1);
        assert_eq!(c.read_ram(0xa000), 42);
    }
    #[test]
    fn mbc5_nine_bit_banks_zero_select_and_ram_banks() {
        let mut c = cart(0x1b, 512, 3);
        for bank in [0, 2, 3, 255, 256, 257, 511] {
            c.write_rom(0x2000, bank as u8);
            c.write_rom(0x3000, (bank >> 8) as u8);
            assert_eq!(
                (c.read_rom(0x4000), c.read_rom(0x4001)),
                (bank as u8, (bank >> 8) as u8)
            );
            assert_eq!(c.current_rom_bank(), bank as u8);
        }
        c.write_rom(0, 0xa);
        c.write_rom(0x4000, 0);
        c.write_ram(0xa000, 0x11);
        c.write_rom(0x4000, 1);
        c.write_ram(0xa000, 0x22);
        for (bank, value) in [(0, 0x11), (1, 0x22)] {
            c.write_rom(0x4000, bank);
            assert_eq!(c.read_ram(0xa000), value);
        }
    }
    #[test]
    fn rtc_bootstrap_halt_backwards_rollover_and_day_carry() {
        let mut r = Rtc {
            seconds: 50,
            minutes: 10,
            ..Rtc::default()
        };
        r.advance_from_host(1000);
        assert_eq!((r.seconds, r.minutes, r.base_unix_seconds), (50, 10, 1000));
        r.advance_from_host(1020);
        assert_eq!((r.seconds, r.minutes), (10, 11));
        r.day_high_and_flags = 0x40;
        r.advance_from_host(9999);
        assert_eq!((r.seconds, r.base_unix_seconds), (10, 9999));
        r.day_high_and_flags = 0;
        r.advance_from_host(9000);
        assert_eq!(r.seconds, 10);
        r.seconds = 59;
        r.minutes = 59;
        r.hours = 23;
        r.day_low = 255;
        r.day_high_and_flags = 1;
        r.advance_from_host(9001);
        assert_eq!(
            (
                r.seconds,
                r.minutes,
                r.hours,
                r.day_low,
                r.day_high_and_flags
            ),
            (0, 0, 0, 0, 0x80)
        );
        r.latch();
        assert_eq!(r.latched_day_high_and_flags, 0x80);
    }
    #[test]
    fn header_short_unsupported_defaults_and_ascii_replacement() {
        for len in 0..0x150 {
            assert_eq!(
                CartridgeHeader::parse(&vec![0; len]).unwrap_err(),
                "ROM smaller than header size"
            );
        }
        let mut rom = vec![0; 0x150];
        rom[0x147] = 0xff;
        assert_eq!(
            CartridgeHeader::parse(&rom).unwrap_err(),
            "Cartridge type $FF not supported"
        );
        rom[0x147] = 0;
        for code in 0..=255u8 {
            rom[0x148] = code;
            rom[0x149] = code;
            let h = CartridgeHeader::parse(&rom).unwrap();
            assert_eq!(h.rom_banks, if code <= 8 { 2usize << code } else { 2 });
            assert_eq!(
                h.ram_banks,
                match code {
                    2 => 1,
                    3 => 4,
                    4 => 16,
                    5 => 8,
                    _ => 0,
                }
            );
        }
        rom[0x134..0x144].fill(0xfe);
        rom[0x143] = 0x7f;
        let h = CartridgeHeader::parse(&rom).unwrap();
        assert_eq!(h.title, "???????????????\u{7f}");
        rom[0x143] = 0x80;
        assert_eq!(
            CartridgeHeader::parse(&rom).unwrap().title,
            "???????????????"
        );
    }
    #[test]
    fn state_size_mismatch_truncation_and_raw_order() {
        let mut c = cart(0x13, 4, 2);
        c.ram[0] = 0x42;
        c.mbc1_bank_low = 7;
        c.mbc1_bank_high = 8;
        c.mbc1_ram_enabled = true;
        c.mbc1_mode = 1;
        c.mbc3_latch_latch = 0x99;
        c.rtc = Rtc {
            seconds: 1,
            minutes: 2,
            hours: 3,
            day_low: 4,
            day_high_and_flags: 5,
            latched_seconds: 6,
            latched_minutes: 7,
            latched_hours: 8,
            latched_day_low: 9,
            latched_day_high_and_flags: 10,
            base_unix_seconds: -1,
        };
        c.mbc5_ram_bank = 15;
        let mut w = StateWriter::default();
        c.write_state(&mut w);
        assert_eq!(&w.bytes[..5], &[0, 0x20, 0, 0, 0x42]);
        assert_eq!(
            &w.bytes[4 + 0x2000..],
            &[
                7, 8, 1, 1, 0x99, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 255, 255, 255, 255, 255, 255, 255,
                255, 15
            ]
        );
        let mut restored = cart(0x13, 4, 2);
        restored
            .read_state(&mut StateReader::new(&w.bytes))
            .unwrap();
        let mut output = StateWriter::default();
        restored.write_state(&mut output);
        assert_eq!(w.bytes, output.bytes);
        for n in (0..4).chain(4 + 0x2000..w.bytes.len()) {
            assert_eq!(
                c.read_state(&mut StateReader::new(&w.bytes[..n]))
                    .unwrap_err()
                    .kind(),
                io::ErrorKind::UnexpectedEof
            );
            let mut after = StateWriter::default();
            c.write_state(&mut after);
            assert_eq!(after.bytes, w.bytes);
        }
        for length in [-1, 0, 8191, 8193, i32::MAX] {
            assert_eq!(
                c.read_state(&mut StateReader::new(&length.to_le_bytes()))
                    .unwrap_err()
                    .kind(),
                io::ErrorKind::InvalidData
            );
        }
    }
    #[test]
    fn independent_csharp_mapper_and_rtc_states() {
        let mut reader = StateReader::new(include_bytes!("cartridge/oracle.bin"));
        assert_eq!(reader.read_u32().unwrap(), 4);
        for _ in 0..4 {
            let kind = reader.read_byte().unwrap();
            let mut c = cart(kind, if kind == 0 { 4 } else { 512 }, 4);
            let count = reader.read_u32().unwrap();
            assert_eq!(count, 1024);
            for index in 0..count {
                let a = reader.read_u16().unwrap();
                let value = reader.read_byte().unwrap();
                let ra = reader.read_u16().unwrap();
                let rv = reader.read_byte().unwrap();
                c.write_rom(a, value);
                c.write_ram(ra, rv);
                let result = [
                    c.read_rom(0),
                    c.read_rom(1),
                    c.read_rom(0x4000),
                    c.read_rom(0x4001),
                    c.read_ram(ra),
                    c.read_ram(0xbfff),
                    c.current_rom_bank(),
                ];
                assert_eq!(
                    result,
                    reader.read_bytes(7).unwrap(),
                    "kind {kind} operation {index}"
                );
            }
            let len = reader.read_i32().unwrap() as usize;
            let expected = reader.read_bytes(len).unwrap();
            let mut state = StateWriter::default();
            c.write_state(&mut state);
            assert_eq!(state.bytes, expected, "mapper state {kind}");
        }
        assert_eq!(reader.read_u32().unwrap(), 128);
        for index in 0..128 {
            let mut rtc = Rtc::default();
            rtc.read_state(&mut reader).unwrap();
            let now = reader.read_i64().unwrap();
            rtc.advance_from_host(now);
            rtc.latch();
            let mut actual = StateWriter::default();
            rtc.write_state(&mut actual);
            assert_eq!(
                actual.bytes,
                reader.read_bytes(18).unwrap(),
                "RTC case {index}"
            );
        }
        assert_eq!(reader.remaining(), 0);
    }
}
