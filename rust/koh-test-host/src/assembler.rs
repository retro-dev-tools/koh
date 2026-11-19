use crate::common::Result;
use koh_emulator::{HardwareMode, cartridge::Cartridge, machine::Machine};

pub fn run(args: &[String]) -> Result {
    let [operation, path] = args else {
        return Err("Expected execute <rom>".into());
    };
    if operation != "execute" {
        return Err("Expected execute <rom>".into());
    }
    let rom = std::fs::read(path).map_err(|error| error.to_string())?;
    let steps = execute(&rom)?;
    println!("Executed {steps} instructions; WRAM C000=2A C001=2B");
    Ok(())
}

fn execute(rom: &[u8]) -> Result<usize> {
    let mut machine = Machine::new(Cartridge::load(rom)?, Some(HardwareMode::Dmg));
    machine.cpu.registers.pc = 0x100;
    machine.cpu.registers.sp = 0xfffe;
    for step in 1..=10000 {
        machine.step_instruction();
        if machine.read_debug(0xc000) == 42 && machine.read_debug(0xc001) == 43 {
            return Ok(step);
        }
    }
    Err(format!(
        "Execution signature absent after 10000 instructions; PC={:04X}, C000={:02X}, C001={:02X}",
        machine.cpu.registers.pc,
        machine.read_debug(0xc000),
        machine.read_debug(0xc001)
    ))
}

#[cfg(test)]
mod tests {
    use super::{execute, run};

    #[test]
    fn rejects_invalid_arguments() {
        for args in [
            vec![],
            vec!["execute".into()],
            vec!["unknown".into(), "unused.gb".into()],
            vec!["execute".into(), "unused.gb".into(), "extra".into()],
        ] {
            assert_eq!(run(&args).unwrap_err(), "Expected execute <rom>");
        }
    }

    #[test]
    fn executes_the_wram_signature() {
        let mut rom = vec![0; 0x8000];
        rom[0x100..0x10b].copy_from_slice(&[
            0x3e, 42, 0xea, 0x00, 0xc0, 0x3c, 0xea, 0x01, 0xc0, 0x18, 0xfe,
        ]);
        assert_eq!(execute(&rom).unwrap(), 4);
    }

    #[test]
    fn missing_signature_reports_the_final_machine_state() {
        let mut rom = vec![0; 0x8000];
        rom[0x100..0x102].copy_from_slice(&[0x18, 0xfe]);
        let error = execute(&rom).unwrap_err();
        assert!(error.contains("after 10000 instructions"));
        assert!(error.contains("PC=0100, C000=FF, C001=FF"));
    }

    #[test]
    fn rejects_a_truncated_rom() {
        assert!(execute(&[0; 16]).is_err());
    }
}
