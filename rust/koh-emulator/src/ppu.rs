use crate::{
    HardwareMode, Interrupts,
    state::{StateReader, StateWriter},
};
use std::io;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(i32)]
pub enum PpuMode {
    HBlank = 0,
    VBlank = 1,
    OamScan = 2,
    Drawing = 3,
}

#[derive(Clone, Default)]
pub struct StatRegister {
    pub user_bits: u8,
}
impl StatRegister {
    pub fn read(&self, mode: PpuMode, coincidence: bool) -> u8 {
        0x80 | (self.user_bits & 0x78) | mode as u8 | if coincidence { 4 } else { 0 }
    }
    pub fn write(&mut self, value: u8) {
        self.user_bits = value & 0x78;
    }
}
#[derive(Clone)]
pub struct CgbPalette {
    pub index_register: u8,
    data: [u8; 64],
}
impl Default for CgbPalette {
    fn default() -> Self {
        Self {
            index_register: 0,
            data: [0; 64],
        }
    }
}
impl CgbPalette {
    pub fn read_data(&self) -> u8 {
        self.data[(self.index_register & 63) as usize]
    }
    pub fn write_data(&mut self, value: u8) {
        self.data[(self.index_register & 63) as usize] = value;
        if self.index_register & 128 != 0 {
            self.index_register = 128 | (self.index_register.wrapping_add(1) & 63);
        }
    }
    pub fn get_color(&self, palette: usize, color: usize) -> u16 {
        let i = palette * 8 + color * 2;
        u16::from_le_bytes([self.data[i], self.data[i + 1]])
    }
    pub fn set_color(&mut self, palette: usize, color: usize, value: u16) {
        let i = palette * 8 + color * 2;
        self.data[i..i + 2].copy_from_slice(&value.to_le_bytes());
    }
    pub fn raw_data(&self) -> &[u8] {
        &self.data
    }
    pub fn fill_white(&mut self) {
        for color in self.data.as_chunks_mut::<2>().0 {
            color.copy_from_slice(&[255, 127]);
        }
    }
    pub fn write_state(&self, w: &mut StateWriter) {
        w.write_byte(self.index_register);
        w.write_bytes(&self.data);
    }
    pub fn read_state(&mut self, r: &mut StateReader<'_>) -> io::Result<()> {
        self.index_register = r.read_byte()?;
        self.data.copy_from_slice(r.read_bytes(64)?);
        Ok(())
    }
}
#[derive(Clone, Default)]
struct Fetcher {
    step: u8,
    budget: i32,
    x: i32,
    y: i32,
    map: u16,
    window: bool,
    tile: u8,
    attrs: u8,
    low: u8,
    high: u8,
}
impl Fetcher {
    fn reset(&mut self, scx: u8, scy: u8, ly: u8, map: u16) {
        self.step = 0;
        self.budget = 2;
        self.x = (scx / 8) as i32;
        self.y = ((ly as i32 + scy as i32) / 8) & 31;
        self.map = map;
        self.window = false;
    }
    fn window(&mut self, map: u16, line: i32) {
        self.step = 0;
        self.budget = 2;
        self.x = 0;
        self.y = line / 8;
        self.map = map;
        self.window = true;
    }
    fn write_state(&self, w: &mut StateWriter) {
        w.write_byte(self.step);
        for n in [self.budget, self.x, self.y] {
            w.write_i32(n);
        }
        w.write_u16(self.map);
        w.write_bool(self.window);
        w.write_bytes(&[self.tile, self.attrs, self.low, self.high]);
    }
    fn read_state(&mut self, r: &mut StateReader<'_>) -> io::Result<()> {
        self.step = r.read_byte()?;
        self.budget = r.read_i32()?;
        self.x = r.read_i32()?;
        self.y = r.read_i32()?;
        self.map = r.read_u16()?;
        self.window = r.read_bool()?;
        self.tile = r.read_byte()?;
        self.attrs = r.read_byte()?;
        self.low = r.read_byte()?;
        self.high = r.read_byte()?;
        Ok(())
    }
}
#[derive(Clone, Default)]
struct Fifo {
    bg: [u8; 16],
    attrs: [u8; 16],
    head: usize,
    count: usize,
    colors: [u8; 8],
    palettes: [u8; 8],
    flags: [u8; 8],
    priority: [i32; 8],
    sprites: usize,
}
impl Fifo {
    fn reset(&mut self) {
        self.head = 0;
        self.count = 0;
        self.sprites = 0;
    }
    fn push_bg(&mut self, low: u8, high: u8, flip: bool, attrs: u8) -> bool {
        if self.count + 8 > 16 {
            return false;
        }
        for i in 0..8 {
            let bit = if flip { i } else { 7 - i };
            let slot = (self.head + self.count + i) & 15;
            self.bg[slot] = ((low >> bit) & 1) | (((high >> bit) & 1) << 1);
            self.attrs[slot] = attrs;
        }
        self.count += 8;
        true
    }
    fn push_sprite(&mut self, index: usize, color: u8, palette: u8, flags: u8, key: i32) {
        let slot = if index < self.sprites {
            if color == 0 || (self.colors[index] != 0 && key >= self.priority[index]) {
                return;
            }
            index
        } else {
            let slot = self.sprites;
            self.sprites += 1;
            slot
        };
        self.colors[slot] = color;
        self.palettes[slot] = palette;
        self.flags[slot] = flags;
        self.priority[slot] = key;
    }
    fn shift(&mut self) -> (u8, u8, u8, u8, u8) {
        let mut pixel = (self.bg[self.head], self.attrs[self.head], 0, 0, 0);
        self.head = (self.head + 1) & 15;
        self.count -= 1;
        if self.sprites > 0 {
            pixel.2 = self.colors[0];
            pixel.3 = self.palettes[0];
            pixel.4 = self.flags[0];
            for i in 1..self.sprites {
                self.colors[i - 1] = self.colors[i];
                self.palettes[i - 1] = self.palettes[i];
                self.flags[i - 1] = self.flags[i];
                self.priority[i - 1] = self.priority[i];
            }
            self.sprites -= 1;
        }
        pixel
    }
    fn write_state(&self, w: &mut StateWriter) {
        w.write_bytes(&self.bg);
        w.write_bytes(&self.attrs);
        w.write_i32(self.head as i32);
        w.write_i32(self.count as i32);
        w.write_bytes(&self.colors);
        w.write_bytes(&self.palettes);
        w.write_bytes(&self.flags);
        for n in self.priority {
            w.write_i32(n);
        }
        w.write_i32(self.sprites as i32);
    }
    fn read_state(&mut self, r: &mut StateReader<'_>) -> io::Result<()> {
        self.bg.copy_from_slice(r.read_bytes(16)?);
        self.attrs.copy_from_slice(r.read_bytes(16)?);
        self.head = r.read_i32()? as usize;
        self.count = r.read_i32()? as usize;
        self.colors.copy_from_slice(r.read_bytes(8)?);
        self.palettes.copy_from_slice(r.read_bytes(8)?);
        self.flags.copy_from_slice(r.read_bytes(8)?);
        for n in &mut self.priority {
            *n = r.read_i32()?;
        }
        self.sprites = r.read_i32()? as usize;
        Ok(())
    }
}
#[derive(Clone)]
pub struct Ppu {
    pub ly: u8,
    pub lyc: u8,
    pub scx: u8,
    pub scy: u8,
    pub wx: u8,
    pub wy: u8,
    pub bgp: u8,
    pub obp0: u8,
    pub obp1: u8,
    pub lcdc: u8,
    pub opri: u8,
    pub stat: StatRegister,
    pub bg_palette: CgbPalette,
    pub obj_palette: CgbPalette,
    pub mode: PpuMode,
    pub dot: i32,
    pub framebuffer: Vec<u8>,
    back: Vec<u8>,
    hw: HardwareMode,
    fetcher: Fetcher,
    fifo: Fifo,
    lcd_x: i32,
    window_line: i32,
    window_triggered: bool,
    discard_done: bool,
    line_sprites: [[u8; 4]; 10],
    consumed: [bool; 10],
    sprite_count: usize,
    sprite_fetch: i32,
    sprite_penalty: i32,
    prev_stat: bool,
    lcd_was_enabled: bool,
}
impl Ppu {
    pub const WIDTH: usize = 160;
    pub const HEIGHT: usize = 144;
    pub fn new(hw: HardwareMode) -> Self {
        let buffer = [0x2e, 0x2e, 0x2e, 255].repeat(160 * 144);
        Self {
            ly: 0,
            lyc: 0,
            scx: 0,
            scy: 0,
            wx: 0,
            wy: 0,
            bgp: 0xfc,
            obp0: 0,
            obp1: 0,
            lcdc: 0x91,
            opri: 0,
            stat: StatRegister::default(),
            bg_palette: CgbPalette::default(),
            obj_palette: CgbPalette::default(),
            mode: PpuMode::OamScan,
            dot: 0,
            framebuffer: buffer.clone(),
            back: buffer,
            hw,
            fetcher: Fetcher::default(),
            fifo: Fifo::default(),
            lcd_x: 0,
            window_line: 0,
            window_triggered: false,
            discard_done: false,
            line_sprites: [[0; 4]; 10],
            consumed: [false; 10],
            sprite_count: 0,
            sprite_fetch: -1,
            sprite_penalty: 0,
            prev_stat: false,
            lcd_was_enabled: true,
        }
    }
    pub fn back_buffer(&self) -> &[u8] {
        &self.back
    }
    pub fn reset(&mut self) {
        self.ly = 0;
        self.dot = 0;
        self.mode = PpuMode::OamScan;
        self.fifo.reset();
        self.window_line = 0;
        self.window_triggered = false;
        self.discard_done = false;
        self.prev_stat = false;
        self.lcd_was_enabled = self.lcdc & 128 != 0;
    }
    fn height(&self) -> i32 {
        if self.lcdc & 4 != 0 { 16 } else { 8 }
    }
    fn map(&self, window: bool) -> u16 {
        if self.lcdc & if window { 64 } else { 8 } != 0 {
            0x9c00
        } else {
            0x9800
        }
    }
    pub fn tick_dot(&mut self, vram: &[u8], oam: &[u8], irq: &mut Interrupts) -> bool {
        if self.lcdc & 128 == 0 {
            self.ly = 0;
            self.dot = 0;
            self.mode = PpuMode::HBlank;
            self.lcd_was_enabled = false;
            return false;
        }
        if !self.lcd_was_enabled {
            self.ly = 0;
            self.dot = 0;
            self.mode = PpuMode::OamScan;
            self.window_line = 0;
            self.window_triggered = false;
            self.discard_done = false;
            self.fifo.reset();
            self.lcd_was_enabled = true;
        }
        let mut hblank = false;
        match self.mode {
            PpuMode::OamScan => {
                if self.dot == 0 {
                    self.sprite_count = 0;
                    for sprite in oam.as_chunks::<4>().0.iter().take(40) {
                        let y = sprite[0] as i32 - 16;
                        if self.ly as i32 >= y && (self.ly as i32) < y + self.height() {
                            self.line_sprites[self.sprite_count].copy_from_slice(sprite);
                            self.consumed[self.sprite_count] = false;
                            self.sprite_count += 1;
                            if self.sprite_count == 10 {
                                break;
                            }
                        }
                    }
                }
                self.dot += 1;
                if self.dot >= 80 {
                    self.mode = PpuMode::Drawing;
                    self.lcd_x = 0;
                    self.fifo.reset();
                    self.window_triggered = false;
                    self.discard_done = false;
                    self.sprite_fetch = -1;
                    self.fetcher
                        .reset(self.scx, self.scy, self.ly, self.map(false));
                }
            }
            PpuMode::Drawing => {
                hblank = self.draw(vram);
            }
            PpuMode::HBlank => {
                self.dot += 1;
                if self.dot >= 456 {
                    self.dot = 0;
                    self.ly = self.ly.wrapping_add(1);
                    if self.ly == 144 {
                        self.mode = PpuMode::VBlank;
                        irq.raise(Interrupts::VBLANK);
                        std::mem::swap(&mut self.framebuffer, &mut self.back);
                    } else {
                        self.mode = PpuMode::OamScan;
                    }
                }
            }
            PpuMode::VBlank => {
                self.dot += 1;
                if self.dot >= 456 {
                    self.dot = 0;
                    self.ly = self.ly.wrapping_add(1);
                    if self.ly >= 154 {
                        self.ly = 0;
                        self.mode = PpuMode::OamScan;
                        self.window_line = 0;
                    }
                }
            }
        }
        let user = self.stat.user_bits;
        let line = (user & 64 != 0 && self.ly == self.lyc)
            || (user & 32 != 0 && self.mode == PpuMode::OamScan)
            || (user & 16 != 0 && self.mode == PpuMode::VBlank)
            || (user & 8 != 0 && self.mode == PpuMode::HBlank);
        if line && !self.prev_stat {
            irq.raise(Interrupts::STAT);
        }
        self.prev_stat = line;
        hblank
    }
    fn draw(&mut self, vram: &[u8]) -> bool {
        if self.sprite_fetch >= 0 {
            self.sprite_penalty -= 1;
            if self.sprite_penalty <= 0 {
                let i = self.sprite_fetch as usize;
                self.push_sprite(vram, i);
                self.consumed[i] = true;
                self.sprite_fetch = -1;
            }
            self.dot += 1;
            return false;
        }
        if self.lcdc & 2 != 0 {
            for i in 0..self.sprite_count {
                let x = self.line_sprites[i][1] as i32;
                if !self.consumed[i] && x != 0 && x < 168 && (x - 8).max(0) == self.lcd_x {
                    self.sprite_fetch = i as i32;
                    self.sprite_penalty = 6;
                    self.dot += 1;
                    return false;
                }
            }
        }
        self.run_fetcher(vram);
        let mut hblank = false;
        if self.fifo.count > 0 {
            let discard = self.scx & 7;
            if self.lcd_x == 0 && self.fifo.count >= 8 && discard > 0 && !self.discard_done {
                for _ in 0..discard {
                    self.fifo.shift();
                }
                self.discard_done = true;
            }
            if !self.window_triggered
                && self.lcdc & 32 != 0
                && self.ly >= self.wy
                && self.lcd_x + 7 >= self.wx as i32
            {
                self.window_triggered = true;
                self.fifo.head = 0;
                self.fifo.count = 0;
                self.fetcher.window(self.map(true), self.window_line);
                self.dot += 1;
                return false;
            }
            if self.fifo.count > 0 {
                let pixel = self.fifo.shift();
                self.emit(pixel);
                self.lcd_x += 1;
                if self.lcd_x >= 160 {
                    self.mode = PpuMode::HBlank;
                    self.discard_done = false;
                    if self.window_triggered {
                        self.window_line += 1;
                    }
                    hblank = true;
                }
            }
        }
        self.dot += 1;
        hblank
    }
    fn run_fetcher(&mut self, vram: &[u8]) {
        self.fetcher.budget -= 1;
        if self.fetcher.budget > 0 {
            return;
        }
        match self.fetcher.step {
            0 => {
                let i = (self.fetcher.map as i32 - 0x8000 + self.fetcher.y * 32 + self.fetcher.x)
                    as usize;
                self.fetcher.tile = vram[i];
                if self.hw == HardwareMode::Cgb {
                    self.fetcher.attrs = vram[0x2000 + i];
                }
                self.fetcher.step = 1;
                self.fetcher.budget = 2;
            }
            1 => {
                self.fetcher.low = self.tile_byte(vram, true);
                self.fetcher.step = 2;
                self.fetcher.budget = 2;
            }
            2 => {
                self.fetcher.high = self.tile_byte(vram, false);
                self.fetcher.step = 3;
                self.fetcher.budget = 2;
            }
            3 => {
                let f = &mut self.fetcher;
                if self.fifo.push_bg(
                    f.low,
                    f.high,
                    self.hw == HardwareMode::Cgb && f.attrs & 32 != 0,
                    f.attrs,
                ) {
                    f.x = (f.x + 1) & 31;
                    f.step = 0;
                    f.budget = 2;
                } else {
                    f.budget = 1;
                }
            }
            _ => {}
        }
    }
    fn tile_byte(&self, vram: &[u8], low: bool) -> u8 {
        let f = &self.fetcher;
        let mut row = if f.window {
            self.window_line & 7
        } else {
            (self.ly as i32 + self.scy as i32) & 7
        };
        if self.hw == HardwareMode::Cgb && f.attrs & 64 != 0 {
            row = 7 - row;
        }
        let bank = if self.hw == HardwareMode::Cgb && f.attrs & 8 != 0 {
            0x2000
        } else {
            0
        };
        let addr = if self.lcdc & 16 != 0 {
            f.tile as i32 * 16
        } else {
            0x1000 + f.tile as i8 as i32 * 16
        };
        vram[(bank + addr + row * 2 + if low { 0 } else { 1 }) as usize]
    }
    fn push_sprite(&mut self, vram: &[u8], index: usize) {
        let [y, x, tile, flags] = self.line_sprites[index];
        let key = if self.hw == HardwareMode::Cgb && self.opri & 1 == 0 {
            index as i32
        } else {
            ((x as i32) << 8) | index as i32
        };
        let height = self.height();
        let mut row = self.ly as i32 - (y as i32 - 16);
        if flags & 64 != 0 {
            row = height - 1 - row;
        }
        let mut tile = tile as i32;
        if height == 16 {
            tile &= 0xfe;
            if row >= 8 {
                tile |= 1;
                row -= 8;
            }
        }
        let bank = if self.hw == HardwareMode::Cgb && flags & 8 != 0 {
            0x2000
        } else {
            0
        };
        let addr = (bank + tile * 16 + row * 2) as usize;
        let low = vram[addr];
        let high = vram[addr + 1];
        let skip = 8usize.saturating_sub(x as usize);
        for px in skip..8 {
            let bit = if flags & 32 != 0 { px } else { 7 - px };
            self.fifo.push_sprite(
                px - skip,
                ((low >> bit) & 1) | (((high >> bit) & 1) << 1),
                flags & 7,
                flags,
                key,
            );
        }
    }
    fn emit(&mut self, (mut bg, attrs, sprite, palette, flags): (u8, u8, u8, u8, u8)) {
        let i = (self.ly as usize * 160 + self.lcd_x as usize) * 4;
        let rgb = if self.hw == HardwareMode::Cgb {
            let priority = self.lcdc & 1 != 0 && (attrs & 128 != 0 || flags & 128 != 0);
            let color = if sprite != 0 && (!priority || bg == 0) {
                self.obj_palette
                    .get_color((palette & 7) as usize, sprite as usize)
            } else {
                self.bg_palette.get_color((attrs & 7) as usize, bg as usize)
            };
            let expand = |v: u16| ((v << 3) | (v >> 2)) as u8;
            [
                expand(color & 31),
                expand((color >> 5) & 31),
                expand((color >> 10) & 31),
            ]
        } else {
            if self.lcdc & 1 == 0 {
                bg = 0;
            }
            let (color, pal) = if sprite != 0 && (flags & 128 == 0 || bg == 0) {
                (
                    sprite,
                    if flags & 16 != 0 {
                        self.obp1
                    } else {
                        self.obp0
                    },
                )
            } else {
                (bg, self.bgp)
            };
            let shade = 255 - ((pal >> (color * 2)) & 3) * 85;
            [shade; 3]
        };
        self.back[i..i + 3].copy_from_slice(&rgb);
        self.back[i + 3] = 255;
    }
    pub fn write_state(&self, w: &mut StateWriter) {
        w.write_bytes(&[
            self.ly,
            self.lyc,
            self.scx,
            self.scy,
            self.wx,
            self.wy,
            self.bgp,
            self.obp0,
            self.obp1,
            self.lcdc,
            self.opri,
            self.stat.user_bits,
        ]);
        w.write_i32(self.mode as i32);
        w.write_i32(self.dot);
        w.write_i32(self.window_line);
        for flag in [
            self.window_triggered,
            self.discard_done,
            self.prev_stat,
            self.lcd_was_enabled,
        ] {
            w.write_bool(flag);
        }
        self.bg_palette.write_state(w);
        self.obj_palette.write_state(w);
        self.fetcher.write_state(w);
        self.fifo.write_state(w);
        for n in [
            self.lcd_x,
            self.sprite_fetch,
            self.sprite_penalty,
            self.sprite_count as i32,
        ] {
            w.write_i32(n);
        }
        for sprite in self.line_sprites {
            w.write_bytes(&sprite);
        }
        for flag in self.consumed {
            w.write_bool(flag);
        }
    }
    pub fn read_state(&mut self, r: &mut StateReader<'_>) -> io::Result<()> {
        self.ly = r.read_byte()?;
        self.lyc = r.read_byte()?;
        self.scx = r.read_byte()?;
        self.scy = r.read_byte()?;
        self.wx = r.read_byte()?;
        self.wy = r.read_byte()?;
        self.bgp = r.read_byte()?;
        self.obp0 = r.read_byte()?;
        self.obp1 = r.read_byte()?;
        self.lcdc = r.read_byte()?;
        self.opri = r.read_byte()?;
        self.stat.user_bits = r.read_byte()?;
        self.mode = match r.read_i32()? {
            0 => PpuMode::HBlank,
            1 => PpuMode::VBlank,
            2 => PpuMode::OamScan,
            3 => PpuMode::Drawing,
            _ => {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "invalid PPU mode",
                ));
            }
        };
        self.dot = r.read_i32()?;
        self.window_line = r.read_i32()?;
        self.window_triggered = r.read_bool()?;
        self.discard_done = r.read_bool()?;
        self.prev_stat = r.read_bool()?;
        self.lcd_was_enabled = r.read_bool()?;
        self.bg_palette.read_state(r)?;
        self.obj_palette.read_state(r)?;
        self.fetcher.read_state(r)?;
        self.fifo.read_state(r)?;
        self.lcd_x = r.read_i32()?;
        self.sprite_fetch = r.read_i32()?;
        self.sprite_penalty = r.read_i32()?;
        self.sprite_count = r.read_i32()? as usize;
        for sprite in &mut self.line_sprites {
            sprite.copy_from_slice(r.read_bytes(4)?);
        }
        for flag in &mut self.consumed {
            *flag = r.read_bool()?;
        }
        if self.fifo.head >= 16
            || self.fifo.count > 16
            || self.fifo.sprites > 8
            || self.sprite_count > 10
            || !(-1..10).contains(&self.sprite_fetch)
            || !(0..=160).contains(&self.lcd_x)
            || !(0..456).contains(&self.dot)
            || self.ly >= 154
            || !(0..=144).contains(&self.window_line)
            || self.fetcher.step > 4
            || !(0..=2).contains(&self.fetcher.budget)
            || !(0..=6).contains(&self.sprite_penalty)
            || !(0..32).contains(&self.fetcher.x)
            || !(0..32).contains(&self.fetcher.y)
            || (self.fetcher.map != 0 && self.fetcher.map != 0x9800 && self.fetcher.map != 0x9c00)
            || (self.mode == PpuMode::Drawing
                && (self.fetcher.map == 0 || self.lcd_x == 160 || self.ly >= 144))
            || (self.sprite_fetch >= 0 && self.sprite_fetch as usize >= self.sprite_count)
            || self
                .fifo
                .bg
                .iter()
                .chain(self.fifo.colors.iter())
                .any(|&color| color > 3)
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "invalid PPU state",
            ));
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn tick(p: &mut Ppu, v: &[u8], o: &[u8], count: usize) -> Interrupts {
        let mut irq = Interrupts::default();
        for _ in 0..count {
            p.tick_dot(v, o, &mut irq);
        }
        irq
    }
    #[test]
    fn vblank_flip_and_frame_wrap() {
        let mut p = Ppu::new(HardwareMode::Dmg);
        let v = [0; 0x4000];
        let o = [0; 0xa0];
        assert_eq!(&p.framebuffer[..4], &[46, 46, 46, 255]);
        let irq = tick(&mut p, &v, &o, 144 * 456);
        assert_eq!((p.ly, p.dot, p.mode), (144, 0, PpuMode::VBlank));
        assert_eq!(irq.iflag & 1, 1);
        assert_eq!(&p.framebuffer[..4], &[255, 255, 255, 255]);
        tick(&mut p, &v, &o, 10 * 456);
        assert_eq!((p.ly, p.dot, p.mode), (0, 0, PpuMode::OamScan));
    }
    #[test]
    fn stat_masks_and_shared_irq_edges() {
        let mut p = Ppu::new(HardwareMode::Dmg);
        p.stat.write(255);
        assert_eq!(p.stat.user_bits, 0x78);
        assert_eq!(p.stat.read(PpuMode::Drawing, true), 255);
        p.stat.write(0x60);
        let v = [0; 0x4000];
        let o = [0; 0xa0];
        assert_eq!(tick(&mut p, &v, &o, 1).iflag & 2, 2);
        assert_eq!(tick(&mut p, &v, &o, 79).iflag & 2, 0);
        p.lyc = 1;
        assert_eq!(tick(&mut p, &v, &o, 1).iflag & 2, 0);
        assert_eq!(tick(&mut p, &v, &o, 375).iflag & 2, 2);
    }
    #[test]
    fn tile_pixels_scx_discard_and_exact_hblank_entry() {
        for scx in 0..8 {
            let mut p = Ppu::new(HardwareMode::Dmg);
            p.bgp = 0xe4;
            p.scx = scx;
            let mut v = [0; 0x4000];
            v[0] = 0x55;
            v[1] = 0x33;
            let mut irq = Interrupts::default();
            let mut entry = 0;
            for dot in 1..=456 {
                if p.tick_dot(&v, &[0; 0xa0], &mut irq) {
                    assert_eq!(entry, 0);
                    entry = dot;
                }
            }
            assert_eq!(entry, 247 + scx as usize);
            for x in 0..8 {
                assert_eq!(p.back[x * 4], 255 - ((x + scx as usize) % 4) as u8 * 85);
            }
        }
    }
    #[test]
    fn fifo_capacity_transparency_priority_and_bg_clear() {
        let mut f = Fifo::default();
        assert!(f.push_bg(0xff, 0, false, 3));
        assert!(f.push_bg(0, 0xff, false, 2));
        assert!(!f.push_bg(0, 0, false, 0));
        f.push_sprite(0, 1, 2, 4, 9);
        f.push_sprite(0, 0, 3, 8, 0);
        f.push_sprite(0, 2, 3, 8, 8);
        f.push_sprite(1, 0, 0, 0, 0);
        f.push_sprite(1, 3, 1, 16, 99);
        assert_eq!(f.shift(), (1, 3, 2, 3, 8));
        assert_eq!(f.shift(), (1, 3, 3, 1, 16));
        f.reset();
        assert_eq!((f.count, f.sprites), (0, 0));
    }
    #[test]
    fn window_uses_own_line_and_pauses_while_disabled() {
        let mut p = Ppu::new(HardwareMode::Dmg);
        p.lcdc = 0xf1;
        p.bgp = 0xe4;
        p.wx = 15;
        p.wy = 0;
        let mut v = [0; 0x4000];
        v[0x1c00] = 1;
        v[16] = 255;
        v[19] = 255;
        tick(&mut p, &v, &[0; 0xa0], 456);
        assert_eq!(p.window_line, 1);
        assert_eq!(p.back[7 * 4], 255);
        assert_eq!(p.back[8 * 4], 170);
        p.lcdc &= !32;
        tick(&mut p, &v, &[0; 0xa0], 456);
        assert_eq!(p.window_line, 1);
        p.lcdc |= 32;
        tick(&mut p, &v, &[0; 0xa0], 456);
        assert_eq!(p.back[(2 * 160 + 8) * 4], 85);
        assert_eq!(p.window_line, 2);
    }
    #[test]
    fn overlapping_sprites_use_dmg_x_or_cgb_oam_priority() {
        for (mode, opri, expected) in [
            (HardwareMode::Dmg, 0, [85, 85, 85]),
            (HardwareMode::Cgb, 0, [255, 0, 0]),
            (HardwareMode::Cgb, 1, [0, 255, 0]),
        ] {
            let mut p = Ppu::new(mode);
            p.lcdc = 0x93;
            p.opri = opri;
            p.bgp = 0xe4;
            p.obp0 = 0xe4;
            p.obj_palette.set_color(0, 1, 31);
            p.obj_palette.set_color(0, 2, 31 << 5);
            let mut v = [0; 0x4000];
            v[16] = 255;
            v[33] = 255;
            let mut o = [0; 0xa0];
            o[..8].copy_from_slice(&[16, 12, 1, 0, 16, 8, 2, 0]);
            tick(&mut p, &v, &o, 456);
            assert_eq!(&p.back[4 * 4..4 * 4 + 3], &expected);
        }
    }
    #[test]
    fn clipped_sprite_and_8x16_y_flip_use_correct_tile() {
        let mut p = Ppu::new(HardwareMode::Dmg);
        p.lcdc = 0x97;
        p.obp0 = 0xe4;
        let mut v = [0; 0x4000];
        v[3 * 16 + 14] = 255;
        let mut o = [0; 0xa0];
        o[..4].copy_from_slice(&[16, 4, 3, 64]);
        tick(&mut p, &v, &o, 456);
        assert_eq!(&p.back[..4], &[170, 170, 170, 255]);
        assert_eq!(&p.back[4 * 4..4 * 4 + 4], &[255, 255, 255, 255]);
    }
    #[test]
    fn cgb_bg_bank_flips_palette_and_priority() {
        for (lcdc0, bg_priority, expected) in [
            (1, 128, [0, 0, 255]),
            (0, 128, [255, 0, 0]),
            (1, 0, [255, 0, 0]),
        ] {
            let mut p = Ppu::new(HardwareMode::Cgb);
            p.lcdc = 0x92 | lcdc0;
            p.bg_palette.set_color(3, 1, 31 << 10);
            p.obj_palette.set_color(2, 2, 31);
            let mut v = [0; 0x4000];
            v[0x3800] = 3 | 8 | 32 | 64 | bg_priority;
            v[0x2000 + 14] = 1;
            v[17] = 255;
            let mut o = [0; 0xa0];
            o[..4].copy_from_slice(&[16, 8, 1, 2]);
            tick(&mut p, &v, &o, 456);
            assert_eq!(&p.back[..3], &expected);
        }
    }
    #[test]
    fn palette_increment_wrap_white_and_raw_state() {
        let mut p = CgbPalette {
            index_register: 255,
            ..Default::default()
        };
        p.write_data(0x12);
        assert_eq!(p.index_register, 128);
        p.write_data(0x34);
        assert_eq!(p.raw_data()[63], 0x12);
        assert_eq!(p.raw_data()[0], 0x34);
        p.fill_white();
        for palette in 0..8 {
            for color in 0..4 {
                assert_eq!(p.get_color(palette, color), 0x7fff);
            }
        }
        p.set_color(7, 3, 0x1234);
        p.index_register = 63;
        assert_eq!(p.read_data(), 0x12);
        let mut w = StateWriter::default();
        p.write_state(&mut w);
        assert_eq!(w.bytes.len(), 65);
    }
    #[test]
    fn managed_oracle_states_irqs_hblanks_and_exact_pixels() {
        let fixtures: &[(HardwareMode, u8, &[u8])] = &[
            (HardwareMode::Dmg, 0, include_bytes!("ppu/Dmg-0.bin")),
            (HardwareMode::Dmg, 1, include_bytes!("ppu/Dmg-1.bin")),
            (HardwareMode::Cgb, 0, include_bytes!("ppu/Cgb-0.bin")),
            (HardwareMode::Cgb, 1, include_bytes!("ppu/Cgb-1.bin")),
        ];
        for &(mode, opri, fixture) in fixtures {
            let mut vram = [0; 0x4000];
            for (i, byte) in vram.iter_mut().enumerate() {
                *byte = ((i * 73 + (i >> 3) * 19 + 0x35) ^ (i >> 7)) as u8;
            }
            let mut oam = [0; 0xa0];
            for i in 0..40 {
                oam[i * 4] = (16 + i % 10 * 13) as u8;
                oam[i * 4 + 1] = (4 + i * 19 % 164) as u8;
                oam[i * 4 + 2] = (i * 7) as u8;
                oam[i * 4 + 3] = (i * 29) as u8;
            }
            let mut p = Ppu::new(mode);
            p.lcdc = 0xf7;
            p.bgp = 0xe4;
            p.obp0 = 0xd8;
            p.obp1 = 0x6c;
            p.scx = 3;
            p.scy = 13;
            p.wx = 54;
            p.wy = 5;
            p.opri = opri;
            p.stat.write(0x78);
            for palette in 0..8 {
                for color in 0..4 {
                    p.bg_palette.set_color(
                        palette,
                        color,
                        ((palette * 3751 + color * 9163) & 0x7fff) as u16,
                    );
                    p.obj_palette.set_color(
                        palette,
                        color,
                        ((palette * 2129 + color * 7391) & 0x7fff) as u16,
                    );
                }
            }
            let mut reader = StateReader::new(fixture);
            let mut irq = Interrupts::default();
            let mut hblanks = 0;
            let mut previous = 0;
            for _ in 0..12 {
                let target = reader.read_i32().unwrap();
                for _ in previous..target {
                    if p.tick_dot(&vram, &oam, &mut irq) {
                        hblanks += 1;
                    }
                }
                previous = target;
                assert_eq!(
                    irq.iflag,
                    reader.read_byte().unwrap(),
                    "{mode:?} OPRI{opri} dot{target} IRQ"
                );
                assert_eq!(
                    hblanks,
                    reader.read_i32().unwrap(),
                    "{mode:?} OPRI{opri} dot{target} HBlank"
                );
                let mut state = StateWriter::default();
                p.write_state(&mut state);
                let managed_state = reader.read_bytes(344).unwrap();
                assert_eq!(
                    state.bytes, managed_state,
                    "{mode:?} OPRI{opri} dot{target} state"
                );
                let mut restored = p.clone();
                restored
                    .read_state(&mut StateReader::new(managed_state))
                    .unwrap();
                let mut restored_state = StateWriter::default();
                restored.write_state(&mut restored_state);
                assert_eq!(restored_state.bytes, managed_state);
                let mut next = p.clone();
                let mut a = irq;
                let mut b = irq;
                assert_eq!(
                    next.tick_dot(&vram, &oam, &mut a),
                    restored.tick_dot(&vram, &oam, &mut b)
                );
                assert_eq!(a, b);
                assert_eq!(next.back, restored.back);
            }
            assert_eq!(
                p.framebuffer,
                reader.read_bytes(160 * 144 * 4).unwrap(),
                "{mode:?} OPRI{opri} front"
            );
            assert_eq!(
                p.back,
                reader.read_bytes(160 * 144 * 4).unwrap(),
                "{mode:?} OPRI{opri} back"
            );
            assert_eq!(reader.remaining(), 0);
        }
    }
    #[test]
    fn state_order_truncation_and_mid_sprite_continuation() {
        let mut p = Ppu::new(HardwareMode::Cgb);
        p.lcdc = 0xf3;
        p.wx = 23;
        p.bgp = 0xe4;
        let v = [0x55; 0x4000];
        let mut o = [0; 0xa0];
        o[..4].copy_from_slice(&[16, 20, 1, 0]);
        tick(&mut p, &v, &o, 107);
        let mut w = StateWriter::default();
        p.write_state(&mut w);
        assert_eq!(w.bytes.len(), 344);
        assert_eq!(&w.bytes[..12], &[0, 0, 0, 0, 23, 0, 0xe4, 0, 0, 0xf3, 0, 0]);
        assert_eq!(&w.bytes[12..16], &3i32.to_le_bytes());
        for len in 0..w.bytes.len() {
            assert!(
                Ppu::new(HardwareMode::Cgb)
                    .read_state(&mut StateReader::new(&w.bytes[..len]))
                    .is_err()
            );
        }
        let mut restored = Ppu::new(HardwareMode::Cgb);
        restored
            .read_state(&mut StateReader::new(&w.bytes))
            .unwrap();
        let mut again = StateWriter::default();
        restored.write_state(&mut again);
        assert_eq!(w.bytes, again.bytes);
        restored.back.clone_from(&p.back);
        for _ in 0..800 {
            let a = tick(&mut p, &v, &o, 1);
            let b = tick(&mut restored, &v, &o, 1);
            assert_eq!(a, b);
        }
        assert_eq!(p.back, restored.back);
        let mut a = StateWriter::default();
        let mut b = StateWriter::default();
        p.write_state(&mut a);
        restored.write_state(&mut b);
        assert_eq!(a.bytes, b.bytes);
        let mut bad = w.bytes;
        bad[12..16].copy_from_slice(&99i32.to_le_bytes());
        assert!(restored.read_state(&mut StateReader::new(&bad)).is_err());
    }
    #[test]
    fn state_rejects_unsafe_pipeline_indices() {
        let p = Ppu::new(HardwareMode::Dmg);
        let mut w = StateWriter::default();
        p.write_state(&mut w);
        for (offset, value) in [(12, 3i32), (159, i32::MIN), (290, 11), (278, -1)] {
            let mut bytes = w.bytes.clone();
            bytes[offset..offset + 4].copy_from_slice(&value.to_le_bytes());
            assert!(
                Ppu::new(HardwareMode::Dmg)
                    .read_state(&mut StateReader::new(&bytes))
                    .is_err(),
                "offset {offset}"
            );
        }
    }
    #[test]
    fn mode_timing_and_lcd_restart() {
        let mut p = Ppu::new(HardwareMode::Dmg);
        let mut irq = Interrupts::default();
        for _ in 0..79 {
            assert!(!p.tick_dot(&[0; 0x4000], &[0; 0xa0], &mut irq));
        }
        assert_eq!(p.mode, PpuMode::OamScan);
        p.tick_dot(&[0; 0x4000], &[0; 0xa0], &mut irq);
        assert_eq!(p.mode, PpuMode::Drawing);
        for _ in 80..456 {
            p.tick_dot(&[0; 0x4000], &[0; 0xa0], &mut irq);
        }
        assert_eq!(p.ly, 1);
        p.lcdc = 0;
        p.tick_dot(&[0; 0x4000], &[0; 0xa0], &mut irq);
        assert_eq!((p.ly, p.dot, p.mode), (0, 0, PpuMode::HBlank));
        p.lcdc = 0x91;
        p.tick_dot(&[0; 0x4000], &[0; 0xa0], &mut irq);
        assert_eq!((p.ly, p.dot, p.mode), (0, 1, PpuMode::OamScan));
    }
}
