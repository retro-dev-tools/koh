use crate::{HardwareMode, cartridge::MapperKind, machine::Machine, ppu::PpuMode};

impl Machine {
    pub(crate) fn hook_context(&self) -> crate::machine::MemoryContext {
        crate::machine::MemoryContext {
            ime: if self.cpu_active {
                self.active_ime
            } else {
                self.cpu.ime
            },
            tcycles: if self.cpu_active {
                self.active_tcycles
            } else {
                self.cpu.total_tcycles
            },
            system_ticks: self.system_ticks,
            lcdc: self.ppu.lcdc,
            scx: self.ppu.scx,
            boot_mapped: self.boot_rom_mapped(),
            rom_bank: self.cartridge.current_rom_bank(),
        }
    }
    pub fn vram_locked(&self) -> bool {
        self.ppu.lcdc & 128 != 0 && self.ppu.mode == PpuMode::Drawing
    }
    pub fn oam_locked(&self) -> bool {
        self.ppu.lcdc & 128 != 0 && matches!(self.ppu.mode, PpuMode::Drawing | PpuMode::OamScan)
    }
    pub fn read_cpu(&mut self, address: u16) -> u8 {
        let value = if (self.oam_dma.is_bus_locking && address < 0xff00)
            || ((0x8000..0xa000).contains(&address) && self.vram_locked())
            || ((0xfe00..0xfea0).contains(&address) && self.oam_locked())
        {
            255
        } else {
            self.read_debug(address)
        };
        let context = self.hook.as_ref().map(|_| self.hook_context());
        if let Some(hook) = &mut self.hook {
            hook.set_context(context.unwrap());
            hook.on_read(
                address,
                value,
                if self.cpu_active {
                    self.active_pc
                } else {
                    self.cpu.registers.pc
                },
                self.ppu.ly,
                self.ppu.dot,
                self.ppu.mode,
            );
        }
        value
    }
    pub fn read_debug(&self, address: u16) -> u8 {
        match address {
            0..=0x7fff => {
                if self.boot_rom_mapped()
                    && self
                        .boot
                        .as_ref()
                        .is_some_and(|boot| boot.contains(address))
                {
                    self.boot.as_ref().unwrap().bytes[address as usize]
                } else {
                    self.cartridge.read_rom(address)
                }
            }
            0x8000..=0x9fff => {
                self.vram[self.vram_bank as usize * 0x2000 + address as usize - 0x8000]
            }
            0xa000..=0xbfff => self.cartridge.read_ram(address),
            0xc000..=0xcfff => self.wram[address as usize - 0xc000],
            0xd000..=0xdfff => {
                self.wram[self.wram_bank as usize * 0x1000 + address as usize - 0xd000]
            }
            0xe000..=0xefff => self.wram[address as usize - 0xe000],
            0xf000..=0xfdff => {
                self.wram[self.wram_bank as usize * 0x1000 + address as usize - 0xf000]
            }
            0xfe00..=0xfe9f => self.oam[address as usize - 0xfe00],
            0xfea0..=0xfeff => 0,
            0xff00..=0xff7f => self.read_io(address),
            0xff80..=0xfffe => self.hram[address as usize - 0xff80],
            0xffff => self.interrupts.ie,
        }
    }
    pub fn write_cpu(&mut self, address: u16, value: u8) {
        let context = self.hook.as_ref().map(|_| self.hook_context());
        if let Some(hook) = &mut self.hook {
            hook.set_context(context.unwrap());
            hook.on_write(
                address,
                value,
                if self.cpu_active {
                    self.active_pc
                } else {
                    self.cpu.registers.pc
                },
                self.ppu.ly,
                self.ppu.dot,
                self.ppu.mode,
            );
        }
        if self.oam_dma.is_bus_locking && address < 0xff00 {
            return;
        }
        if address == 0xff46 {
            self.oam_dma.trigger(value);
        }
        match address {
            0..=0x7fff => self.cartridge.write_rom(address, value),
            0x8000..=0x9fff => {
                if !self.vram_locked() {
                    self.vram[self.vram_bank as usize * 0x2000 + address as usize - 0x8000] = value;
                }
            }
            0xa000..=0xbfff => self.cartridge.write_ram(address, value),
            0xc000..=0xcfff => self.wram[address as usize - 0xc000] = value,
            0xd000..=0xdfff => {
                self.wram[self.wram_bank as usize * 0x1000 + address as usize - 0xd000] = value
            }
            0xe000..=0xefff => self.wram[address as usize - 0xe000] = value,
            0xf000..=0xfdff => {
                self.wram[self.wram_bank as usize * 0x1000 + address as usize - 0xf000] = value
            }
            0xfe00..=0xfe9f => {
                if !self.oam_locked() {
                    self.oam[address as usize - 0xfe00] = value;
                }
            }
            0xfea0..=0xfeff => {}
            0xff00..=0xff7f => self.write_io(address, value),
            0xff80..=0xfffe => self.hram[address as usize - 0xff80] = value,
            0xffff => self.interrupts.ie = value,
        }
    }
    pub fn write_debug(&mut self, address: u16, value: u8) -> bool {
        if address < 0x8000 {
            if (self.cartridge.header.mapper_kind == MapperKind::RomOnly || address < 0x4000)
                && (address as usize) < self.cartridge.rom.len()
            {
                self.cartridge.rom[address as usize] = value;
            }
        } else {
            self.write_cpu(address, value);
        }
        true
    }
    pub fn write_direct(&mut self, address: u16, value: u8) {
        if (0x8000..0xa000).contains(&address) {
            self.vram[self.vram_bank as usize * 0x2000 + address as usize - 0x8000] = value;
        } else {
            self.write_cpu(address, value);
        }
    }
    pub fn write_hdma(&mut self, address: u16, value: u8) {
        if (0x8000..0xa000).contains(&address) {
            if !self.vram_locked() {
                self.vram[self.vram_bank as usize * 0x2000 + address as usize - 0x8000] = value;
            }
        } else {
            self.write_direct(address, value);
        }
    }
    fn read_io(&self, address: u16) -> u8 {
        let cgb = self.mode == HardwareMode::Cgb;
        match address {
            0xff00 => {
                let select = self.io[0] & 0x30;
                let pressed = (if select & 16 == 0 {
                    self.joypad & 15
                } else {
                    0
                }) | (if select & 32 == 0 {
                    self.joypad >> 4
                } else {
                    0
                });
                0xc0 | select | (!pressed & 15)
            }
            0xff01 => self.serial.sb,
            0xff02 => self.serial.read_sc(),
            0xff04 => self.timer.div(),
            0xff05 => self.timer.tima,
            0xff06 => self.timer.tma,
            0xff07 => self.timer.tac | 0xf8,
            0xff0f => self.interrupts.iflag | 0xe0,
            0xff10..=0xff3f => self.apu.read(address),
            0xff40 => self.ppu.lcdc,
            0xff41 => self
                .ppu
                .stat
                .read(self.ppu.mode, self.ppu.ly == self.ppu.lyc),
            0xff42 => self.ppu.scy,
            0xff43 => self.ppu.scx,
            0xff44 => self.ppu.ly,
            0xff45 => self.ppu.lyc,
            0xff47 => self.ppu.bgp,
            0xff48 => self.ppu.obp0,
            0xff49 => self.ppu.obp1,
            0xff4a => self.ppu.wy,
            0xff4b => self.ppu.wx,
            0xff4d => {
                if cgb {
                    0x7e | if self.double_speed { 128 } else { 0 } | self.switch_armed as u8
                } else {
                    255
                }
            }
            0xff4f => {
                if cgb {
                    0xfe | (self.vram_bank & 1)
                } else {
                    255
                }
            }
            0xff70 => {
                if cgb {
                    0xf8 | (self.wram_bank & 7)
                } else {
                    255
                }
            }
            0xff51 => {
                if cgb {
                    self.hdma.source1
                } else {
                    255
                }
            }
            0xff52 => {
                if cgb {
                    self.hdma.source2
                } else {
                    255
                }
            }
            0xff53 => {
                if cgb {
                    self.hdma.dest1
                } else {
                    255
                }
            }
            0xff54 => {
                if cgb {
                    self.hdma.dest2
                } else {
                    255
                }
            }
            0xff55 => {
                if cgb {
                    self.hdma.read_length_register()
                } else {
                    255
                }
            }
            0xff56 => {
                if cgb {
                    self.io[0x56] | 0x3c
                } else {
                    255
                }
            }
            0xff68 => {
                if cgb {
                    self.ppu.bg_palette.index_register
                } else {
                    255
                }
            }
            0xff69 => {
                if cgb {
                    self.ppu.bg_palette.read_data()
                } else {
                    255
                }
            }
            0xff6a => {
                if cgb {
                    self.ppu.obj_palette.index_register
                } else {
                    255
                }
            }
            0xff6b => {
                if cgb {
                    self.ppu.obj_palette.read_data()
                } else {
                    255
                }
            }
            0xff6c => {
                if cgb {
                    self.ppu.opri | 0xfe
                } else {
                    255
                }
            }
            0xff03
            | 0xff08..=0xff0e
            | 0xff4c
            | 0xff4e
            | 0xff50
            | 0xff57..=0xff67
            | 0xff6d..=0xff6f
            | 0xff71..=0xff7f => 255,
            _ => self.io[address as usize - 0xff00],
        }
    }
    fn write_io(&mut self, address: u16, value: u8) {
        let cgb = self.mode == HardwareMode::Cgb;
        match address {
            0xff00 => self.io[0] = value & 0x30,
            0xff01 => self.serial.sb = value,
            0xff02 => self.serial.write_sc(value),
            0xff04 => {
                if self.timer.write_div() {
                    self.apu.advance_frame_sequencer();
                }
            }
            0xff05 => self.timer.write_tima(value),
            0xff06 => self.timer.write_tma(value),
            0xff07 => self.timer.write_tac(value),
            0xff0f => self.interrupts.iflag = value & 31,
            0xff10..=0xff3f => self
                .apu
                .write(address, value, self.timer.div_apu_bit_high()),
            0xff40 => self.ppu.lcdc = value,
            0xff41 => self.ppu.stat.write(value),
            0xff42 => self.ppu.scy = value,
            0xff43 => self.ppu.scx = value,
            0xff44 => {}
            0xff45 => self.ppu.lyc = value,
            0xff47 => self.ppu.bgp = value,
            0xff48 => self.ppu.obp0 = value,
            0xff49 => self.ppu.obp1 = value,
            0xff4a => self.ppu.wy = value,
            0xff4b => self.ppu.wx = value,
            0xff4d => {
                if cgb {
                    self.switch_armed = value & 1 != 0;
                }
            }
            0xff4f => {
                if cgb {
                    self.vram_bank = value & 1;
                }
            }
            0xff70 => {
                if cgb {
                    self.wram_bank = (value & 7).max(1);
                }
            }
            0xff51 => {
                if cgb {
                    self.hdma.source1 = value;
                }
            }
            0xff52 => {
                if cgb {
                    self.hdma.source2 = value;
                }
            }
            0xff53 => {
                if cgb {
                    self.hdma.dest1 = value;
                }
            }
            0xff54 => {
                if cgb {
                    self.hdma.dest2 = value;
                }
            }
            0xff55 => {
                if cgb {
                    self.hdma.write_length_register(value);
                }
            }
            0xff68 => {
                if cgb {
                    self.ppu.bg_palette.index_register = value;
                }
            }
            0xff69 => {
                if cgb {
                    self.ppu.bg_palette.write_data(value);
                }
            }
            0xff6a => {
                if cgb {
                    self.ppu.obj_palette.index_register = value;
                }
            }
            0xff6b => {
                if cgb {
                    self.ppu.obj_palette.write_data(value);
                }
            }
            0xff6c => {
                if cgb {
                    self.ppu.opri = value & 1;
                }
            }
            0xff50 => {
                if value != 0 {
                    self.io[0x50] = 1;
                }
            }
            _ => self.io[address as usize - 0xff00] = value,
        }
    }
}
