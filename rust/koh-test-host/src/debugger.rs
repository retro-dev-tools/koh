use crate::common::*;
use koh_debugger::{Action, Debugger};
use koh_emulator::{
    HardwareMode,
    cartridge::Cartridge,
    machine::{Machine, MemoryHook},
    ppu::PpuMode,
};
use serde_json::{Value, json};
use std::{
    io::{self, BufRead},
    sync::{Arc, Mutex},
};

struct MachineAccesses(Arc<Mutex<Vec<String>>>);
impl MemoryHook for MachineAccesses {
    fn on_read(&mut self, a: u16, v: u8, _pc: u16, _ly: u8, _dot: i32, _mode: PpuMode) {
        if a >= 0x8000 {
            let mut events = self.0.lock().unwrap();
            assert!(events.len() < 16, "Machine trace capacity exceeded");
            events.push(format!("R{a:04X}:{v:02X}"));
        }
    }
    fn on_write(&mut self, a: u16, v: u8, _pc: u16, _ly: u8, _dot: i32, _mode: PpuMode) {
        let mut events = self.0.lock().unwrap();
        assert!(events.len() < 16, "Machine trace capacity exceeded");
        events.push(format!("W{a:04X}:{v:02X}"));
    }
}
fn writes(m: &mut Machine, state: &Value) -> Result {
    for write in state["writes"].as_array().ok_or("Missing writes")? {
        if !m.write_debug(
            write[0].as_u64().ok_or("Invalid address")? as u16,
            write[1].as_u64().ok_or("Invalid byte")? as u8,
        ) {
            return Err("Machine write rejected".into());
        }
    }
    Ok(())
}
pub fn run() -> Result {
    let mut debugger = Debugger::default();
    let mut machine: Option<Machine> = None;
    let mut sequence = 1;
    for line in io::stdin().lock().lines() {
        let request: Value =
            serde_json::from_str(&line.map_err(|e| e.to_string())?).map_err(|e| e.to_string())?;
        let command = request["command"].as_str().ok_or("Missing command")?;
        let state = &request["arguments"];
        match command {
            "machine" => {
                let mut m = load(state["program"].as_str().ok_or("Missing program")?)?;
                let r = &state["r"];
                m.cpu.registers.b = r[0].as_u64().unwrap() as u8;
                m.cpu.registers.c = r[1].as_u64().unwrap() as u8;
                m.cpu.registers.d = r[2].as_u64().unwrap() as u8;
                m.cpu.registers.e = r[3].as_u64().unwrap() as u8;
                m.cpu.registers.h = r[4].as_u64().unwrap() as u8;
                m.cpu.registers.l = r[5].as_u64().unwrap() as u8;
                m.cpu.registers.a = r[7].as_u64().unwrap() as u8;
                m.cpu.registers.f = state["f"].as_u64().unwrap() as u8;
                m.cpu.registers.pc = state["pc"].as_u64().unwrap() as u16;
                m.cpu.registers.sp = state["sp"].as_u64().unwrap() as u16;
                m.cpu.ime = state["ime"].as_bool().unwrap();
                writes(&mut m, state)?;
                let events = Arc::new(Mutex::new(Vec::new()));
                m.hook = Some(Box::new(MachineAccesses(events.clone())));
                let steps = state["steps"].as_u64().ok_or("Missing steps")?;
                if !(1..=2).contains(&steps) {
                    return Err("Invalid machine step count".into());
                }
                for _ in 0..steps {
                    m.step_instruction();
                }
                let r = m.cpu.registers;
                println!(
                    "{}",
                    json!({"r":[r.b,r.c,r.d,r.e,r.h,r.l,m.read_debug(0xc020),r.a],"f":r.f,"pc":r.pc,"sp":r.sp,"ime":m.cpu.ime,"halted":m.cpu.halted,"io":m.read_debug(0xff40),"stack":read(&m,0xd00e,4),"events":*events.lock().unwrap()})
                );
            }
            "state" => {
                let m = machine.as_mut().ok_or("No session")?;
                m.cpu.registers.pc = state["pc"].as_u64().unwrap() as u16;
                m.cpu.registers.sp = state["sp"].as_u64().unwrap() as u16;
                for symbol in state["symbols"].as_array().ok_or("Missing symbols")? {
                    debugger.debug_info.symbols.push(koh_link::LinkedSymbol {
                        name: symbol.as_str().unwrap().into(),
                        kind: 0,
                        bank: Some(0),
                        value: m.cpu.registers.pc as i32,
                        source: None,
                    });
                }
                writes(m, state)?;
                println!("{{\"configured\":true}}");
            }
            "advance" => {
                let m = machine.as_mut().ok_or("No session")?;
                let limit = state["frames"].as_i64().unwrap_or(120).clamp(1, 1200);
                let mut reason = "FrameComplete";
                for _ in 0..limit {
                    if debugger
                        .run_frame(m)
                        .is_some_and(|event| event["body"]["reason"] == "breakpoint")
                    {
                        reason = "Breakpoint";
                        break;
                    }
                }
                println!(
                    "{}",
                    json!({"reason":reason,"bootMapped":m.boot_rom_mapped(),"pc":m.cpu.registers.pc,"bank":m.cartridge.current_rom_bank(),"sp":m.cpu.registers.sp,"stack":hex(&read(m,m.cpu.registers.sp,64))})
                );
            }
            _ => {
                if command == "launch" {
                    let program = state["program"].as_str().ok_or("Missing program")?;
                    let mode = match state["hardwareMode"].as_str() {
                        Some("dmg") => Some(HardwareMode::Dmg),
                        Some("cgb") => Some(HardwareMode::Cgb),
                        _ => None,
                    };
                    let mut m = Machine::new(
                        Cartridge::load(&std::fs::read(program).map_err(|e| e.to_string())?)?,
                        mode,
                    );
                    boot(&mut m)?;
                    for _ in 0..10000000 {
                        if !m.boot_rom_mapped() {
                            break;
                        }
                        m.step_instruction();
                    }
                    machine = Some(m);
                }
                let mut reply = if let Some(m) = &mut machine {
                    debugger.handle(&request, m)
                } else {
                    debugger.handle_empty(&request)
                };
                if matches!(
                    reply.action,
                    Action::StepIn | Action::Next | Action::StepOut
                ) {
                    if let Some(m) = machine.as_mut() {
                        match reply.action {
                            Action::StepIn => {
                                m.step_instruction();
                            }
                            Action::Next => {
                                let opcode = m.read_debug(m.cpu.registers.pc);
                                let call = matches!(opcode, 0xcd | 0xc4 | 0xcc | 0xd4 | 0xdc)
                                    || opcode & 0xc7 == 0xc7;
                                let sp = m.cpu.registers.sp;
                                for _ in 0..if call { 1000000 } else { 1 } {
                                    m.step_instruction();
                                    if m.cpu.registers.sp == sp {
                                        break;
                                    }
                                }
                            }
                            Action::StepOut => {
                                let pc = word(m, m.cpu.registers.sp);
                                for _ in 0..1000000 {
                                    m.step_instruction();
                                    if m.cpu.registers.pc == pc {
                                        break;
                                    }
                                }
                            }
                            _ => {}
                        }
                    } else {
                        reply.response["success"] = false.into();
                        reply.response["message"] = format!("{command}: no active session").into();
                    }
                }
                reply.response["seq"] = sequence.into();
                sequence += 1;
                println!("{}", reply.response);
            }
        }
    }
    Ok(())
}
