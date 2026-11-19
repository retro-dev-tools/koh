pub mod state;
pub mod timer;
pub mod cpu;
pub mod cartridge;
pub mod ppu;
pub mod apu;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum HardwareMode {
    #[default]
    Dmg,
    Cgb,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Interrupts {
    pub iflag: u8,
    pub ie: u8,
}

impl Interrupts {
    pub const VBLANK: u8 = 1;
    pub const STAT: u8 = 2;
    pub const TIMER: u8 = 4;
    pub const SERIAL: u8 = 8;
    pub const JOYPAD: u8 = 16;

    pub fn pending(&self) -> u8 {
        self.iflag & self.ie & 0x1f
    }

    pub fn raise(&mut self, mask: u8) {
        self.iflag |= mask;
    }

    pub fn clear(&mut self, mask: u8) {
        self.iflag &= !mask;
    }
}
