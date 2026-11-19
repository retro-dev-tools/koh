use koh_emulator::machine::Machine;

pub fn decode(m: &Machine, pc: u16) -> (String, u16) {
    let op = m.read_debug(pc);
    let next = m.read_debug(pc.wrapping_add(1));
    let word = u16::from_le_bytes([next, m.read_debug(pc.wrapping_add(2))]);
    let byte = format!("${next:02X}");
    let word = format!("${word:04X}");
    let signed = format!("{:+}", next as i8);
    let r = ["B", "C", "D", "E", "H", "L", "(HL)", "A"];
    let rp = ["BC", "DE", "HL", "SP"];
    let rp2 = ["BC", "DE", "HL", "AF"];
    let cc = ["NZ", "Z", "NC", "C"];
    let x = op >> 6;
    let y = ((op >> 3) & 7) as usize;
    let z = (op & 7) as usize;
    let p = y / 2;
    let q = y & 1;
    let alu = ["ADD", "ADC", "SUB", "SBC", "AND", "XOR", "OR", "CP"];
    if op == 0xcb {
        let y = ((next >> 3) & 7) as usize;
        let z = (next & 7) as usize;
        return (
            match next >> 6 {
                0 => format!(
                    "{} {}",
                    ["RLC", "RRC", "RL", "RR", "SLA", "SRA", "SWAP", "SRL"][y],
                    r[z]
                ),
                1 => format!("BIT {y},{}", r[z]),
                2 => format!("RES {y},{}", r[z]),
                _ => format!("SET {y},{}", r[z]),
            },
            2,
        );
    }
    let (text, len) = match x {
        0 => match z {
            0 => match y {
                0 => ("NOP".into(), 1),
                1 => (format!("LD ({word}),SP"), 3),
                2 => ("STOP".into(), 2),
                3 => (format!("JR {signed}"), 2),
                _ => (format!("JR {},{signed}", cc[y - 4]), 2),
            },
            1 if q == 0 => (format!("LD {},{word}", rp[p]), 3),
            1 => (format!("ADD HL,{}", rp[p]), 1),
            2 => {
                let dest = ["(BC)", "(DE)", "(HL+)", "(HL-)"][p];
                (
                    if q == 0 {
                        format!("LD {dest},A")
                    } else {
                        format!("LD A,{dest}")
                    },
                    1,
                )
            }
            3 => (
                format!("{} {}", if q == 0 { "INC" } else { "DEC" }, rp[p]),
                1,
            ),
            4 => (format!("INC {}", r[y]), 1),
            5 => (format!("DEC {}", r[y]), 1),
            6 => (format!("LD {},{byte}", r[y]), 2),
            _ => (
                ["RLCA", "RRCA", "RLA", "RRA", "DAA", "CPL", "SCF", "CCF"][y].into(),
                1,
            ),
        },
        1 => {
            if op == 0x76 {
                ("HALT".into(), 1)
            } else {
                (format!("LD {},{}", r[y], r[z]), 1)
            }
        }
        2 => (format!("{} A,{}", alu[y], r[z]), 1),
        _ => match z {
            0 if y < 4 => (format!("RET {}", cc[y]), 1),
            0 => match y {
                4 => (format!("LDH ({byte}),A"), 2),
                5 => (format!("ADD SP,{signed}"), 2),
                6 => (format!("LDH A,({byte})"), 2),
                _ => (format!("LD HL,SP{signed}"), 2),
            },
            1 if q == 0 => (format!("POP {}", rp2[p]), 1),
            1 => match p {
                0 => ("RET".into(), 1),
                1 => ("RETI".into(), 1),
                2 => ("JP (HL)".into(), 1),
                _ => ("LD SP,HL".into(), 1),
            },
            2 if y < 4 => (format!("JP {},{word}", cc[y]), 3),
            2 => match y {
                4 => ("LDH ($FF00+C),A".into(), 1),
                5 => (format!("LD ({word}),A"), 3),
                6 => ("LDH A,($FF00+C)".into(), 1),
                _ => (format!("LD A,({word})"), 3),
            },
            3 => match y {
                0 => (format!("JP {word}"), 3),
                6 => ("DI".into(), 1),
                7 => ("EI".into(), 1),
                _ => (format!("?? ${op:02X}"), 1),
            },
            4 if y < 4 => (format!("CALL {},{word}", cc[y]), 3),
            5 if q == 0 => (format!("PUSH {}", rp2[p]), 1),
            5 if p == 0 => (format!("CALL {word}"), 3),
            6 => (format!("{} A,{byte}", alu[y]), 2),
            7 => (format!("RST ${:02X}", y * 8), 1),
            _ => (format!("?? ${op:02X}"), 1),
        },
    };
    (text, len)
}
