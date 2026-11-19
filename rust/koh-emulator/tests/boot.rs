use koh_emulator::{
    HardwareMode,
    boot::{BootRom, CGB_BOOT, DMG_BOOT, default_for},
    cartridge::Cartridge,
    machine::Machine,
};

fn cartridge(cgb: bool) -> Vec<u8> {
    let mut rom = vec![0; 0x8000];
    rom[0x100..0x104].copy_from_slice(&[0, 0xc3, 0x50, 1]);
    rom[0x104..0x134].copy_from_slice(&[
        0xce, 0xed, 0x66, 0x66, 0xcc, 0x0d, 0, 0x0b, 3, 0x73, 0, 0x83, 0, 0x0c, 0, 0x0d, 0, 8,
        0x11, 0x1f, 0x88, 0x89, 0, 0x0e, 0xdc, 0xcc, 0x6e, 0xe6, 0xdd, 0xdd, 0xd9, 0x99, 0xbb,
        0xbb, 0x67, 0x63, 0x6e, 0x0e, 0xec, 0xcc, 0xdd, 0xdc, 0x99, 0x9f, 0xbb, 0xb9, 0x33, 0x3e,
    ]);
    rom[0x143] = if cgb { 0x80 } else { 0 };
    rom[0x14d] = rom[0x134..=0x14c]
        .iter()
        .fold(0u8, |sum, byte| sum.wrapping_sub(*byte).wrapping_sub(1));
    rom[0x150..0x152].copy_from_slice(&[0x18, 0xfe]);
    rom
}

fn machine(mode: HardwareMode, rom: &[u8]) -> Machine {
    let mut machine = Machine::new(Cartridge::load(rom).unwrap(), Some(mode));
    machine.load_boot_rom(default_for(mode).to_vec()).unwrap();
    machine
}

fn handoff(mode: HardwareMode, cgb: bool) -> Machine {
    let mut machine = machine(mode, &cartridge(cgb));
    for _ in 0..5_000_000 {
        machine.step_instruction();
        if !machine.boot_rom_mapped() {
            return machine;
        }
    }
    panic!("Boot did not unmap; PC={:04X}", machine.cpu.registers.pc);
}

#[test]
fn embedded_images_validate_shape_family_and_dmg_handoff_instruction() {
    assert_eq!(DMG_BOOT.len(), 0x100);
    assert_eq!(CGB_BOOT.len(), 0x900);
    assert_eq!(&DMG_BOOT[0xfc..], &[0x3e, 1, 0xe0, 0x50]);
    for mode in [HardwareMode::Dmg, HardwareMode::Cgb] {
        assert_eq!(
            BootRom::new(default_for(mode).to_vec()).unwrap().family,
            mode
        );
    }
    for size in [0, 0xff, 0x101, 0x800, 0x1000] {
        let error = BootRom::new(vec![0; size]).err().unwrap();
        assert!(error.contains("256") && error.contains("2304"));
    }
}

#[test]
fn real_boot_handoff_registers_logo_palettes_and_cleared_vram() {
    for (mode, cgb) in [
        (HardwareMode::Dmg, false),
        (HardwareMode::Cgb, true),
        (HardwareMode::Cgb, false),
    ] {
        let machine = handoff(mode, cgb);
        let r = &machine.cpu.registers;
        assert_eq!(r.pc, 0x100);
        assert_eq!(r.sp, 0xfffe);
        assert_eq!(
            [r.a, r.f, r.b, r.c, r.d, r.e, r.h, r.l],
            if mode == HardwareMode::Dmg {
                [1, 0xb0, 0, 0x13, 0, 0xd8, 1, 0x4d]
            } else {
                [0x11, 0x80, u8::from(!cgb), 0, 0xff, 0x56, 0, 0x0d]
            }
        );
        assert!(!machine.boot_rom_mapped());
        assert_eq!(machine.read_debug(0), 0);
        assert_eq!(machine.read_debug(0xff40), 0x91);
        assert_eq!(machine.read_debug(0xff42), 0);
        assert_eq!(machine.read_debug(0xff26) & 0x80, 0x80);
        for range in [0..0x10, 0x190..0x1900, 0x1940..0x2000] {
            assert!(machine.vram[range].iter().all(|byte| *byte == 0));
        }
        assert!(machine.vram[0x10..0x190].iter().any(|byte| *byte != 0));
        assert_eq!(machine.vram[0x1904], 1);
        assert_eq!(machine.vram[0x192f], 24);
        if mode == HardwareMode::Cgb {
            assert!(machine.vram[0x2000..].iter().all(|byte| *byte == 0));
            assert_eq!(machine.ppu.bg_palette.get_color(7, 0), 0x7fff);
            assert_eq!(machine.ppu.obj_palette.get_color(7, 1), 0x294a);
        } else {
            assert_eq!(machine.read_debug(0xff47), 0xfc);
        }
    }
}

#[test]
fn invalid_logo_or_checksum_remains_visibly_locked_after_600_frames() {
    for mode in [HardwareMode::Dmg, HardwareMode::Cgb] {
        for offset in [0x104, 0x14d] {
            let mut rom = cartridge(mode == HardwareMode::Cgb);
            rom[offset] ^= 0xff;
            let mut machine = machine(mode, &rom);
            for _ in 0..600 {
                machine.run_frame();
            }
            assert!(machine.boot_rom_mapped(), "{mode:?}, corrupt {offset:04X}");
            assert_ne!(machine.read_debug(0xff40) & 0x80, 0);
        }
    }
}

#[test]
fn real_boot_animation_duration_and_wrong_family_rejection() {
    for mode in [HardwareMode::Dmg, HardwareMode::Cgb] {
        let mut machine = machine(mode, &cartridge(mode == HardwareMode::Cgb));
        let mut frames = 0;
        while machine.boot_rom_mapped() && frames < 400 {
            machine.run_frame();
            frames += 1;
        }
        assert!(frames > 100 && frames < 250, "{mode:?}: {frames} frames");
        let wrong = if mode == HardwareMode::Dmg {
            CGB_BOOT
        } else {
            DMG_BOOT
        };
        let mut machine = Machine::new(Cartridge::load(&cartridge(false)).unwrap(), Some(mode));
        assert!(machine.load_boot_rom(wrong.to_vec()).is_err());
        assert!(!machine.boot_rom_mapped());
    }
}
