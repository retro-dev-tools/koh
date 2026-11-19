use crate::{
    machine::Machine,
    state::{StateReader, StateWriter},
};
use sha2::{Digest, Sha256};
use std::io;

pub fn save(machine: &Machine, rom: &[u8]) -> Vec<u8> {
    let mut w = StateWriter::default();
    w.write_bytes(b"STES");
    w.write_u16(2);
    w.write_u16(0);
    w.write_bytes(&Sha256::digest(rom));
    machine.write_state(&mut w);
    w.bytes
}
pub fn load(machine: &mut Machine, data: &[u8], rom: &[u8]) -> io::Result<()> {
    let invalid = |message| io::Error::new(io::ErrorKind::InvalidData, message);
    let mut r = StateReader::new(data);
    if r.read_bytes(4)? != b"STES" {
        return Err(invalid("bad save-state magic"));
    }
    if r.read_u16()? != 2 {
        return Err(invalid("unsupported save-state version"));
    }
    r.read_u16()?;
    if r.read_bytes(32)? != Sha256::digest(rom).as_slice() {
        return Err(invalid("ROM hash mismatch"));
    }
    let mut backup = StateWriter::default();
    machine.write_state(&mut backup);
    if let Err(error) = machine.read_state(&mut r) {
        machine.read_state(&mut StateReader::new(&backup.bytes))?;
        return Err(error);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn machine() -> Machine {
        Machine::load_rom(&vec![0; 32768]).unwrap()
    }
    #[test]
    fn wrapped_state_preserves_register_memory_timer_and_clock() {
        let rom = vec![0; 32768];
        let mut m = machine();
        m.cpu.registers.pc = 0x100;
        m.cpu.registers.sp = 0xdffe;
        m.write_debug(0xc123, 0x42);
        m.write_debug(0xff05, 0x67);
        for _ in 0..23 {
            m.step_instruction();
        }
        let data = save(&m, &rom);
        assert!(data.len() > 40);
        assert_eq!(data, include_bytes!("state_file/csharp.bin"));
        assert_eq!(&data[..8], b"STES\x02\0\0\0");
        assert_eq!(
            &data[8..40],
            &[
                0xc3, 0x50, 0x20, 0x47, 0x3a, 0xed, 0x1b, 0x46, 0x42, 0xcd, 0x72, 0x6c, 0xad, 0x72,
                0x7b, 0x63, 0xff, 0xf2, 0x82, 0x4a, 0xd6, 0x8c, 0xed, 0xd7, 0xff, 0xb7, 0x3c, 0x7c,
                0xbd, 0x89, 0x04, 0x79
            ]
        );
        let mut restored = machine();
        load(&mut restored, &data, &rom).unwrap();
        assert_eq!(restored.cpu.registers, m.cpu.registers);
        assert_eq!(restored.system_ticks, m.system_ticks);
        assert_eq!(restored.read_debug(0xc123), 0x42);
        assert_eq!(restored.timer.tima, 0x67);
        for _ in 0..10 {
            m.run_frame();
            restored.run_frame();
        }
        assert_eq!(restored.ppu.framebuffer, m.ppu.framebuffer);
    }
    #[test]
    fn invalid_identity_and_truncated_payload_preserve_loaded_machine() {
        let rom = vec![0; 32768];
        let mut m = machine();
        m.cpu.registers.a = 0x5a;
        let good = save(&m, &rom);
        let mut variants = vec![good[..good.len().saturating_sub(1)].to_vec()];
        for offset in [0, 4, 8] {
            let mut data = good.clone();
            if let Some(byte) = data.get_mut(offset) {
                *byte ^= 1;
            }
            variants.push(data);
        }
        for data in variants {
            assert!(load(&mut m, &data, &rom).is_err());
            assert_eq!(m.cpu.registers.a, 0x5a);
        }
    }
}
