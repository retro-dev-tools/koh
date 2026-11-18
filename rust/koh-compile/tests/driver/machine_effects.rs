use super::{debugger, debugger_replies, instruction_rom};
use crate::Host;
use rstest::rstest;
use serde_json::{Value, json};

#[derive(Clone)]
struct State {
    r: [u8; 8],
    f: u8,
    pc: u16,
    sp: u16,
    ime: bool,
    halted: bool,
    io: u8,
    stack: [u8; 4],
    events: Vec<String>,
}

impl State {
    fn result(&self) -> Value {
        json!({"r":self.r,"f":self.f,"pc":self.pc,"sp":self.sp,
            "ime":self.ime,"halted":self.halted,"io":self.io,
            "stack":self.stack,"events":self.events})
    }

    fn access(&mut self, kind: char, address: u16, value: u8) {
        self.events.push(format!("{kind}{address:04X}:{value:02X}"));
    }

    fn operand(&mut self, register: usize) -> u8 {
        let value = self.r[register];
        if register == 6 {
            self.access('R', 0xc020, value);
        }
        value
    }

    fn store(&mut self, register: usize, value: u8) {
        self.r[register] = value;
        if register == 6 {
            self.access('W', 0xc020, value);
        }
    }
}

fn flags(zero: bool, subtract: bool, half: bool, carry: bool) -> u8 {
    u8::from(zero) * 0x80
        | u8::from(subtract) * 0x40
        | u8::from(half) * 0x20
        | u8::from(carry) * 0x10
}

fn expected(code: &[u8], initial: &State) -> State {
    let mut s = initial.clone();
    s.pc += code.len() as u16;
    let op = code[0];
    match op {
        0x40..=0x7f if op != 0x76 => {
            let value = s.operand((op & 7) as usize);
            s.store(((op >> 3) & 7) as usize, value);
        }
        0x06 | 0x0e | 0x16 | 0x1e | 0x26 | 0x2e | 0x36 | 0x3e => {
            s.store(((op >> 3) & 7) as usize, code[1]);
        }
        0x80..=0xbf | 0xc6 => {
            let a = u16::from(s.r[7]);
            let b = u16::from(if op == 0xc6 {
                code[1]
            } else {
                s.operand((op & 7) as usize)
            });
            let group = if op == 0xc6 { 0 } else { (op >> 3) & 7 };
            let carry = u16::from(matches!(group, 1 | 3) && s.f & 0x10 != 0);
            let (value, subtract, half, carry) = match group {
                0 | 1 => (
                    a + b + carry,
                    false,
                    (a & 15) + (b & 15) + carry > 15,
                    a + b + carry > 255,
                ),
                2 | 3 | 7 => (
                    a.wrapping_sub(b + carry),
                    true,
                    (a & 15) < (b & 15) + carry,
                    a < b + carry,
                ),
                4 => (a & b, false, true, false),
                5 => (a ^ b, false, false, false),
                6 => (a | b, false, false, false),
                _ => unreachable!(),
            };
            s.f = flags(value as u8 == 0, subtract, half, carry);
            if group != 7 {
                s.r[7] = value as u8;
            }
        }
        0x00..=0x3f if op & 7 == 4 || op & 7 == 5 => {
            let register = ((op >> 3) & 7) as usize;
            let old = s.operand(register);
            let decrement = op & 1 != 0;
            let value = if decrement {
                old.wrapping_sub(1)
            } else {
                old.wrapping_add(1)
            };
            s.f = flags(
                value == 0,
                decrement,
                if decrement {
                    old & 15 == 0
                } else {
                    old & 15 == 15
                },
                s.f & 0x10 != 0,
            );
            s.store(register, value);
        }
        0xcb => {
            let sub = code[1];
            let register = (sub & 7) as usize;
            let old = s.operand(register);
            let bit = (sub >> 3) & 7;
            match sub >> 6 {
                0 => {
                    let carry = u8::from(s.f & 0x10 != 0);
                    let (value, out) = match bit {
                        0 => (old.rotate_left(1), old >> 7),
                        1 => (old.rotate_right(1), old & 1),
                        2 => ((old << 1) | carry, old >> 7),
                        3 => ((old >> 1) | (carry << 7), old & 1),
                        4 => (old << 1, old >> 7),
                        5 => ((old >> 1) | (old & 0x80), old & 1),
                        6 => (old.rotate_left(4), 0),
                        7 => (old >> 1, old & 1),
                        _ => unreachable!(),
                    };
                    s.f = flags(value == 0, false, false, out != 0);
                    s.store(register, value);
                }
                1 => s.f = flags(old & (1 << bit) == 0, false, true, s.f & 0x10 != 0),
                2 => s.store(register, old & !(1 << bit)),
                3 => s.store(register, old | (1 << bit)),
                _ => unreachable!(),
            }
        }
        0x00 => {}
        0x76 => s.halted = true,
        0xc3 => s.pc = 0x3155,
        0xc2 | 0xca | 0xd2 | 0xda => {
            let take = match op {
                0xc2 => s.f & 0x80 == 0,
                0xca => s.f & 0x80 != 0,
                0xd2 => s.f & 0x10 == 0,
                0xda => s.f & 0x10 != 0,
                _ => unreachable!(),
            };
            if take {
                s.pc = 0x3155;
            }
        }
        0x19 => {
            let hl = u32::from(u16::from_be_bytes([s.r[4], s.r[5]]));
            let de = u32::from(u16::from_be_bytes([s.r[2], s.r[3]]));
            s.f = flags(
                s.f & 0x80 != 0,
                false,
                (hl & 0xfff) + (de & 0xfff) > 0xfff,
                hl + de > 0xffff,
            );
            [s.r[4], s.r[5]] = ((hl + de) as u16).to_be_bytes();
        }
        0x23 => {
            [s.r[4], s.r[5]] = u16::from_be_bytes([s.r[4], s.r[5]])
                .wrapping_add(1)
                .to_be_bytes();
        }
        0x18 => s.pc += 2,
        0x20 => {
            if s.f & 0x80 == 0 {
                s.pc += 2;
            }
        }
        0xc9 | 0xd9 | 0xf1 => {
            s.access('R', 0xd010, 0x55);
            s.access('R', 0xd011, 0x31);
            s.sp += 2;
            if op == 0xf1 {
                s.r[7] = 0x31;
                s.f = 0x50;
            } else {
                s.pc = 0x3155;
                if op == 0xd9 {
                    s.ime = true;
                }
            }
        }
        0xcd | 0xf5 => {
            let (lo, hi) = if op == 0xcd {
                (s.pc as u8, (s.pc >> 8) as u8)
            } else {
                (s.f, s.r[7])
            };
            s.access('W', 0xd00f, hi);
            s.access('W', 0xd00e, lo);
            s.stack[0] = lo;
            s.stack[1] = hi;
            s.sp -= 2;
            if op == 0xcd {
                s.pc = 0x3155;
            }
        }
        0xf3 => s.ime = false,
        0xfb => s.ime = code.len() == 2,
        0x2a => {
            s.r[7] = s.operand(6);
            s.r[5] += 1;
        }
        0x32 => {
            s.store(6, s.r[7]);
            s.r[5] -= 1;
        }
        0xe0 | 0xe2 => {
            s.io = s.r[7];
            s.access('W', 0xff40, s.io);
        }
        0xf0 | 0xf2 => {
            s.r[7] = s.io;
            s.access('R', 0xff40, s.io);
        }
        0xea => s.store(6, s.r[7]),
        0xfa => s.r[7] = s.operand(6),
        _ => panic!("Missing independent instruction expectation: {code:02X?}"),
    }
    s
}

#[rstest]
#[case("loads")]
#[case("immediates")]
#[case("alu")]
#[case("inc_dec")]
#[case("rotate_shift")]
#[case("bit")]
#[case("reset")]
#[case("set")]
#[case("control_memory")]
fn sm83_machine_effects_follow_independent_isa_contracts(debugger: &Host, #[case] family: &str) {
    let codes: Vec<Vec<u8>> = match family {
        "loads" => (0x40..=0x7f)
            .filter(|&op| op != 0x76)
            .map(|op| vec![op])
            .collect(),
        "immediates" => (0..8)
            .flat_map(|r| [vec![0x06 + r * 8, 0], vec![0x06 + r * 8, 0xa5]])
            .collect(),
        "alu" => (0x80..=0xbf).map(|op| vec![op]).collect(),
        "inc_dec" => (0..8)
            .flat_map(|r| [vec![0x04 + r * 8], vec![0x05 + r * 8]])
            .collect(),
        "rotate_shift" => (0x00..=0x3f).map(|op| vec![0xcb, op]).collect(),
        "bit" => (0x40..=0x7f).map(|op| vec![0xcb, op]).collect(),
        "reset" => (0x80..=0xbf).map(|op| vec![0xcb, op]).collect(),
        "set" => (0xc0..=0xff).map(|op| vec![0xcb, op]).collect(),
        "control_memory" => vec![
            vec![0xc9],
            vec![0xcd, 0x55, 0x31],
            vec![0xc3, 0x55, 0x31],
            vec![0x18, 2],
            vec![0x20, 2],
            vec![0x76],
            vec![0xf1],
            vec![0xf5],
            vec![0xd9],
            vec![0xf3],
            vec![0xfb],
            vec![0xfb, 0],
            vec![0x00],
            vec![0x2a],
            vec![0x32],
            vec![0xe0, 0x40],
            vec![0xf0, 0x40],
            vec![0xea, 0x20, 0xc0],
            vec![0xf2],
            vec![0xe2],
            vec![0xfa, 0x20, 0xc0],
            vec![0xc6, 0xa5],
            vec![0x19],
            vec![0x23],
            vec![0xc2, 0x55, 0x31],
            vec![0xca, 0x55, 0x31],
            vec![0xd2, 0x55, 0x31],
            vec![0xda, 0x55, 0x31],
        ],
        _ => panic!("Unknown ISA family {family}"),
    };
    let directory = tempfile::tempdir().unwrap();
    let mut data = Vec::new();
    for code in &codes {
        assert!(code.len() <= 3);
        data.extend_from_slice(code);
        data.resize(data.len() + 4 - code.len(), 0);
    }
    let (rom, _) = instruction_rom(directory.path(), &data);
    let mut requests = Vec::new();
    let mut expectations = Vec::new();
    for (index, code) in codes.iter().enumerate() {
        let mut patterns = vec![
            [0x0f, 0x10, 0x7f, 0x80, 0xc0, 0x20, 0x55, 0x81],
            [0xff, 1, 0, 0xff, 0xc0, 0x20, 0x80, 0],
        ];
        if code[0] == 0x19 {
            for de in [0x0fe0u16, 0xffe0] {
                let mut r = patterns[0];
                [r[2], r[3]] = de.to_be_bytes();
                patterns.push(r);
            }
        }
        if code[0] == 0x23 {
            let mut r = patterns[0];
            r[4..6].fill(0xff);
            patterns.push(r);
        }
        for r in patterns {
            for high_flags in 0..16 {
                let mut state = State {
                    r,
                    f: high_flags * 16,
                    pc: 0x3000 + index as u16 * 4,
                    sp: 0xd010,
                    ime: !matches!(code[0], 0xfb | 0xd9),
                    halted: false,
                    io: 0,
                    stack: [0xa6, 0xb7, 0x55, 0x31],
                    events: vec![],
                };
                if matches!(code[0], 0xf2 | 0xe2) {
                    state.r[1] = 0x40;
                }
                let steps = if code.as_slice() == [0xfb, 0] { 2 } else { 1 };
                requests.push(
                    json!({"command":"machine","arguments":{
                    "program":rom,"r":state.r,"f":state.f,"pc":state.pc,
                    "sp":state.sp,"ime":state.ime,"steps":steps,
                    "writes":[[0xc020,state.r[6] as u16],[0xff40,0],[0xffff,0],[0xff0f,0],
                        [0xd00e,0xa6],[0xd00f,0xb7],[0xd010,0x55],[0xd011,0x31]]}})
                    .to_string(),
                );
                expectations.push((code.clone(), state.clone(), expected(code, &state).result()));
            }
        }
    }
    let replies = debugger_replies(debugger, &requests);
    assert_eq!(
        replies.len(),
        codes.len() * 32 + if family == "control_memory" { 48 } else { 0 }
    );
    for (actual, (code, initial, expected)) in replies.iter().zip(expectations) {
        assert_eq!(
            actual, &expected,
            "{family} {code:02X?}, r={:02X?}, F={:02X}",
            initial.r, initial.f
        );
    }
}
