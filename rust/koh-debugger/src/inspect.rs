use crate::{Debugger, bank, metadata};
use koh_emulator::machine::Machine;
use serde_json::{Value, json};
use std::path::Path;

fn variable(name: &str, value: String, kind: Option<&str>) -> Value {
    let mut result = json!({"name":name,"value":value,"variablesReference":0,"type":null});
    if let Some(kind) = kind {
        result["type"] = kind.into();
    }
    result
}

impl Debugger {
    pub fn location_label(&self, m: &Machine, pc: u16) -> String {
        self.debug_info
            .line(bank(m, pc), pc)
            .map(|l| format!("{}:{}", l.file, l.line))
            .unwrap_or_else(|| format!("${:02X}:${pc:04X}", bank(m, pc)))
    }
    pub(crate) fn evaluate(&self, m: &Machine, text: &str) -> Result<Value, String> {
        let expr = text.trim();
        if expr.is_empty() {
            return Err("evaluate: empty expression".into());
        }
        let literal = crate::expression::literal(expr, true);
        let register = crate::expression::register(m, expr, true);
        let symbol = self.debug_info.symbol(expr);
        let value_kind = if let Some(v) = literal {
            Some((v, "literal"))
        } else if let Some(v) = register {
            Some((v, "register"))
        } else {
            symbol
                .filter(|s| s.kind == 1)
                .map(|s| (s.value, "constant"))
        };
        if let Some((value, kind)) = value_kind {
            let hex = if value < 0 {
                format!("{:08X}", value as u32)
            } else if value <= 255 {
                format!("{value:02X}")
            } else if value <= 65535 {
                format!("{value:04X}")
            } else {
                format!("{value:08X}")
            };
            let mut body = json!({"result":format!("${hex} ({value})"),"type":kind,"variablesReference":0,"memoryReference":null});
            if (0..=65535).contains(&value) {
                body["memoryReference"] = format!("0x{value:04X}").into();
            }
            return Ok(body);
        }
        if let Some(symbol) = symbol {
            return Ok(
                json!({"result":format!("${:02X}:${:04X}",symbol.bank.unwrap_or(0),symbol.value),"type":metadata::kind(symbol),
                "memoryReference":format!("0x{:04X}",symbol.value),"variablesReference":0}),
            );
        }
        Err(format!("evaluate: unknown identifier '{expr}'"))
    }
    pub(crate) fn variables(&self, m: &Machine, reference: i32) -> Vec<Value> {
        let r = &m.cpu.registers;
        match reference {
            1 => {
                let mut vars: Vec<_> = [
                    ("A", r.a),
                    ("F", r.f),
                    ("B", r.b),
                    ("C", r.c),
                    ("D", r.d),
                    ("E", r.e),
                    ("H", r.h),
                    ("L", r.l),
                ]
                .into_iter()
                .map(|(n, v)| variable(n, format!("${v:02X}"), None))
                .collect();
                vars.extend(
                    [
                        ("AF", r.af()),
                        ("BC", r.bc()),
                        ("DE", r.de()),
                        ("HL", r.hl()),
                        ("SP", r.sp),
                        ("PC", r.pc),
                    ]
                    .into_iter()
                    .map(|(n, v)| variable(n, format!("${v:04X}"), None)),
                );
                vars.extend(
                    [("Z", 0x80), ("N", 0x40), ("H_", 0x20), ("C_", 0x10)]
                        .into_iter()
                        .map(|(n, mask)| variable(n, (r.f & mask != 0).to_string(), None)),
                );
                vars
            }
            2 => {
                let mut vars = vec![
                    variable("LY", format!("${:02X}", m.ppu.ly), None),
                    variable("IF", format!("${:02X}", m.interrupts.iflag), None),
                    variable("IE", format!("${:02X}", m.interrupts.ie), None),
                    variable("IME", m.cpu.ime.to_string(), None),
                ];
                vars.extend(
                    [
                        ("DIV", m.timer.div()),
                        ("TIMA", m.timer.tima),
                        ("TMA", m.timer.tma),
                        ("TAC", m.timer.tac),
                    ]
                    .into_iter()
                    .map(|(n, v)| variable(n, format!("${v:02X}"), None)),
                );
                vars
            }
            3 => self
                .debug_info
                .all_symbols()
                .into_iter()
                .filter(|s| !metadata::marker(&s.name))
                .take(200)
                .map(|s| {
                    variable(
                        &s.name,
                        if s.kind == 1 {
                            s.value.to_string()
                        } else {
                            format!("${:02X}:${:04X}", s.bank.unwrap_or(0), s.value)
                        },
                        Some(metadata::kind(s)),
                    )
                })
                .collect(),
            4 => {
                let pc = r.pc;
                let bank = bank(m, pc);
                let mut vars = vec![variable("PC", format!("${bank:02X}:${pc:04X}"), None)];
                if let Some(s) = self
                    .debug_info
                    .all_symbols()
                    .into_iter()
                    .filter(|s| {
                        s.kind != 1
                            && s.bank == Some(bank as u32)
                            && s.value <= pc as i32
                            && !metadata::marker(&s.name)
                    })
                    .reduce(|best, s| if s.value > best.value { s } else { best })
                {
                    let offset = pc as i32 - s.value;
                    vars.push(variable(
                        "nearestSymbol",
                        if offset == 0 {
                            s.name.clone()
                        } else {
                            format!("{} + {offset}", s.name)
                        },
                        Some(metadata::kind(s)),
                    ));
                }
                for (i, s) in self
                    .debug_info
                    .symbols
                    .iter()
                    .filter(|s| {
                        s.kind != 1
                            && s.bank == Some(bank as u32)
                            && s.value == pc as i32
                            && !metadata::marker(&s.name)
                    })
                    .enumerate()
                {
                    vars.push(variable(
                        &format!("symbol[{i}]"),
                        s.name.clone(),
                        Some(metadata::kind(s)),
                    ));
                }
                vars
            }
            _ => Vec::new(),
        }
    }
    fn frame(&self, bank: u8, pc: u16, id: usize) -> Value {
        let bank = if pc < 0x4000 { 0 } else { bank };
        let location = self.debug_info.line(bank, pc);
        let mut frame = json!({"id":id,"name":format!("${pc:04X}"),"line":1,"column":1,"source":null,"instructionPointerReference":format!("0x{pc:04X}")});
        if let Some(l) = location {
            let name = Path::new(&l.file)
                .file_name()
                .unwrap_or_default()
                .to_string_lossy();
            frame["name"] = format!("{name}:{}", l.line).into();
            frame["line"] = l.line.into();
            frame["source"] = json!({"name":name,"path":l.file});
        }
        frame
    }
    fn caller(&self, m: &Machine, bank: u8, pc: u16, sp: u16) -> Option<(u8, u16, u16)> {
        if !(0xc100..0xd000).contains(&sp) {
            return None;
        }
        let mut recipe = None;
        for s in self.debug_info.symbols.iter().filter(|s| {
            s.kind != 1
                && s.bank == Some(bank as u32)
                && s.value == pc as i32
                && metadata::marker(&s.name)
        }) {
            if s.kind != 0 {
                return None;
            }
            let text = s.name.strip_prefix("__koh_unwind_v1_")?;
            let bytes = text.as_bytes();
            if bytes.len() < 8 || bytes[0] != b'b' || bytes[1] != b'_' || bytes[6] != b'_' {
                return None;
            }
            let delta = u16::from_str_radix(text.get(2..6)?, 16).ok()?;
            if delta > 0xf00 || recipe.is_some_and(|prior| prior != delta) {
                return None;
            }
            recipe = Some(delta);
        }
        let entry = sp as u32 + recipe? as u32;
        let next = entry + 6;
        if next > 0xd000 {
            return None;
        }
        let word = |address: u16| {
            u16::from_le_bytes([m.read_debug(address), m.read_debug(address.wrapping_add(1))])
        };
        let stub = self.debug_info.symbol("__koh_far_return")?;
        if stub.bank != Some(0) || word(entry as u16) != stub.value as u16 {
            return None;
        }
        let caller_pc = word((entry + 2) as u16);
        let caller_bank = word((entry + 4) as u16);
        if caller_bank > 255
            || caller_pc == 0
            || caller_pc >= 0x8000
            || caller_bank as usize * 0x4000 >= m.cartridge.rom.len()
        {
            return None;
        }
        Some((
            if caller_pc < 0x4000 {
                0
            } else {
                caller_bank as u8
            },
            caller_pc,
            next as u16,
        ))
    }
    pub(crate) fn stack_trace(&self, m: &Machine) -> Value {
        let mut pc = m.cpu.registers.pc;
        let mut sp = m.cpu.registers.sp;
        let mut bank = bank(m, pc);
        let mut frames = vec![self.frame(bank, pc, 0)];
        if self
            .debug_info
            .all_symbols()
            .iter()
            .any(|s| metadata::marker(&s.name))
        {
            while frames.len() < 32 {
                let Some(caller) = self.caller(m, bank, pc, sp) else {
                    break;
                };
                if caller.2 <= sp {
                    break;
                }
                (bank, pc, sp) = caller;
                frames.push(self.frame(bank, pc, frames.len()));
            }
        } else {
            while frames.len() < 32 && sp < 0xfffe {
                let pc = u16::from_le_bytes([m.read_debug(sp), m.read_debug(sp.wrapping_add(1))]);
                if pc == 0 {
                    break;
                }
                frames.push(self.frame(m.cartridge.current_rom_bank(), pc, frames.len()));
                sp = sp.wrapping_add(2);
            }
        }
        json!({"totalFrames":frames.len(),"stackFrames":frames})
    }
}
