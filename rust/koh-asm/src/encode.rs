#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OperandKind {
    Unsigned8,
    Unsigned16,
    Signed8,
    High8,
    Relative8,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OperandPatch {
    pub offset: usize,
    pub width: u8,
    pub relative: bool,
    pub expression: String,
    pub kind: OperandKind,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Encoded {
    pub bytes: Vec<u8>,
    pub patches: Vec<OperandPatch>,
}

fn normalized(s: &str) -> String {
    s.chars()
        .filter(|c| !c.is_whitespace())
        .flat_map(char::to_lowercase)
        .collect()
}

fn indirect(s: &str) -> Option<&str> {
    s.strip_prefix('[')
        .and_then(|s| s.strip_suffix(']'))
        .or_else(|| s.strip_prefix('(').and_then(|s| s.strip_suffix(')')))
}

fn reg(s: &str) -> Option<u8> {
    match s {
        "b" => Some(0),
        "c" => Some(1),
        "d" => Some(2),
        "e" => Some(3),
        "h" => Some(4),
        "l" => Some(5),
        "[hl]" | "(hl)" => Some(6),
        "a" => Some(7),
        _ => None,
    }
}

fn pair(s: &str) -> Option<u8> {
    match s {
        "bc" => Some(0),
        "de" => Some(1),
        "hl" => Some(2),
        "sp" => Some(3),
        _ => None,
    }
}

fn condition(s: &str) -> Option<u8> {
    let n = s.bytes().take_while(|c| *c == b'!').count();
    let condition = match &s[n..] {
        "nz" => Some(0),
        "z" => Some(1),
        "nc" => Some(2),
        "c" => Some(3),
        _ => None,
    };
    condition.map(|c| c ^ (n as u8 & 1))
}

fn expression(s: &str) -> Result<crate::expr::Expr, String> {
    if reg(&normalized(s)).is_some()
        || pair(&normalized(s)).is_some()
        || matches!(normalized(s).as_str(), "af" | "f" | "nz" | "z" | "nc")
        || indirect(s.trim()).is_some()
    {
        return Err(format!("expected expression, got {s}"));
    }
    crate::expr::parse(s)
}

fn constant(s: &str) -> Result<i32, String> {
    let parsed = expression(s)?;
    if has_pc(&parsed) {
        return Err("instruction operand must be constant".into());
    }
    crate::expr::eval(&parsed, &|_| None, 0)
}

fn has_pc(e: &crate::expr::Expr) -> bool {
    use crate::expr::Expr;
    match e {
        Expr::Pc => true,
        Expr::Unary(_, a) => has_pc(a),
        Expr::Binary(_, a, b) => has_pc(a) || has_pc(b),
        Expr::Call(_, args) => args.iter().any(has_pc),
        _ => false,
    }
}

fn immediate(op: u8, s: &str, kind: OperandKind) -> Result<Encoded, String> {
    let parsed = expression(s)?;
    let width = if kind == OperandKind::Unsigned16 {
        2
    } else {
        1
    };
    let mut result = Encoded {
        bytes: vec![op],
        patches: Vec::new(),
    };
    result.bytes.resize(1 + width as usize, 0);
    if kind != OperandKind::Relative8 && !has_pc(&parsed) {
        if let Ok(value) = crate::expr::eval(&parsed, &|_| None, 0) {
            let valid = match kind {
                OperandKind::Unsigned8 => (-128..=255).contains(&value),
                OperandKind::Unsigned16 => (-32768..=65535).contains(&value),
                OperandKind::Signed8 => (-128..=127).contains(&value),
                OperandKind::High8 => (0xff00..=0xffff).contains(&value),
                OperandKind::Relative8 => unreachable!(),
            };
            if !valid {
                return Err(format!("operand {s} out of range for {kind:?}"));
            }
            result.bytes[1] = value as u8;
            if width == 2 {
                result.bytes[2] = (value >> 8) as u8;
            }
            return Ok(result);
        }
    }
    result.patches.push(OperandPatch {
        offset: 1,
        width,
        relative: kind == OperandKind::Relative8,
        expression: s.to_owned(),
        kind,
    });
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn instruction(s: &str) -> Result<Encoded, String> {
        let (mnemonic, args) = s.split_once(' ').unwrap_or((s, ""));
        let args = if args.is_empty() {
            vec![]
        } else {
            args.split(',').map(|s| s.trim().to_owned()).collect()
        };
        encode(mnemonic, &args)
    }

    fn check(s: &str, expected: &[u8]) {
        assert_eq!(instruction(s).unwrap().bytes, expected, "{s}");
    }

    #[test]
    fn register_opcode_families() {
        let registers = ["b", "c", "d", "e", "h", "l", "[hl]", "a"];
        for (i, a) in registers.iter().enumerate() {
            for (j, b) in registers.iter().enumerate() {
                if i == 6 && j == 6 {
                    continue;
                }
                check(&format!("ld {a},{b}"), &[0x40 + i as u8 * 8 + j as u8]);
            }
            check(&format!("ld {a},42"), &[6 + i as u8 * 8, 42]);
            check(&format!("inc {a}"), &[4 + i as u8 * 8]);
            check(&format!("dec {a}"), &[5 + i as u8 * 8]);
            for (j, op) in ["add", "adc", "sub", "sbc", "and", "xor", "or", "cp"]
                .iter()
                .enumerate()
            {
                check(&format!("{op} a,{a}"), &[0x80 + j as u8 * 8 + i as u8]);
                check(&format!("{op} {a}"), &[0x80 + j as u8 * 8 + i as u8]);
            }
            for (j, op) in ["rlc", "rrc", "rl", "rr", "sla", "sra", "swap", "srl"]
                .iter()
                .enumerate()
            {
                check(&format!("{op} {a}"), &[0xcb, j as u8 * 8 + i as u8]);
            }
            for (j, op) in ["bit", "res", "set"].iter().enumerate() {
                for bit in 0..8 {
                    check(
                        &format!("{op} {bit},{a}"),
                        &[0xcb, 0x40 + j as u8 * 0x40 + bit * 8 + i as u8],
                    );
                }
            }
        }
    }

    #[test]
    fn remaining_opcode_families() {
        for (i, r) in ["bc", "de", "hl", "sp"].iter().enumerate() {
            check(&format!("ld {r},$1234"), &[1 + i as u8 * 16, 0x34, 0x12]);
            check(&format!("inc {r}"), &[3 + i as u8 * 16]);
            check(&format!("dec {r}"), &[0x0b + i as u8 * 16]);
            check(&format!("add hl,{r}"), &[9 + i as u8 * 16]);
        }
        for (i, r) in ["bc", "de", "hl", "af"].iter().enumerate() {
            check(&format!("pop {r}"), &[0xc1 + i as u8 * 16]);
            check(&format!("push {r}"), &[0xc5 + i as u8 * 16]);
        }
        for (i, c) in ["nz", "z", "nc", "c"].iter().enumerate() {
            check(&format!("ret {c}"), &[0xc0 + i as u8 * 8]);
            check(&format!("jp {c},$1234"), &[0xc2 + i as u8 * 8, 0x34, 0x12]);
            check(
                &format!("call {c},$1234"),
                &[0xc4 + i as u8 * 8, 0x34, 0x12],
            );
            let e = instruction(&format!("jr {c},target")).unwrap();
            assert_eq!(e.bytes, [0x20 + i as u8 * 8, 0]);
            assert_eq!(e.patches[0].kind, OperandKind::Relative8);
        }
        for (i, op) in ["add", "adc", "sub", "sbc", "and", "xor", "or", "cp"]
            .iter()
            .enumerate()
        {
            check(&format!("{op} 42"), &[0xc6 + i as u8 * 8, 42]);
        }
        for vector in (0..=56).step_by(8) {
            check(&format!("rst {vector}"), &[0xc7 | vector]);
        }
        for (s, b) in [
            ("nop", 0),
            ("halt", 0x76),
            ("di", 0xf3),
            ("ei", 0xfb),
            ("daa", 0x27),
            ("cpl", 0x2f),
            ("scf", 0x37),
            ("ccf", 0x3f),
            ("rlca", 7),
            ("rrca", 15),
            ("rla", 23),
            ("rra", 31),
            ("ret", 0xc9),
            ("reti", 0xd9),
            ("jp hl", 0xe9),
            ("ld sp,hl", 0xf9),
        ] {
            check(s, &[b]);
        }
        check("stop", &[0x10, 0]);
        check("stop $42", &[0x10, 0x42]);
        check("jp $1234", &[0xc3, 0x34, 0x12]);
        check("call $1234", &[0xcd, 0x34, 0x12]);
        for (s, b) in [
            ("ld [bc],a", 2),
            ("ld a,[bc]", 10),
            ("ld [de],a", 18),
            ("ld a,[de]", 26),
            ("ld [hl+],a", 34),
            ("ld a,[hl+]", 42),
            ("ld [hl-],a", 50),
            ("ld a,[hl-]", 58),
            ("ldh [c],a", 0xe2),
            ("ldh a,[c]", 0xf2),
        ] {
            check(s, &[b]);
        }
        check("ld [$1234],sp", &[8, 0x34, 0x12]);
        check("ld [$1234],a", &[0xea, 0x34, 0x12]);
        check("ld a,[$1234]", &[0xfa, 0x34, 0x12]);
        check("ldh [$ff80],a", &[0xe0, 0x80]);
        check("ldh a,[$ffff]", &[0xf0, 0xff]);
        check("add sp,-128", &[0xe8, 0x80]);
        check("ld hl,sp+127", &[0xf8, 0x7f]);
    }

    #[test]
    fn expressions_aliases_and_deferred_operands() {
        check("ret !nz", &[0xc8]);
        check("ret !!z", &[0xc8]);
        check("jp !!!c,$1234", &[0xd2, 0x34, 0x12]);
        check("rst 8*3", &[0xdf]);
        check("bit 1+2,a", &[0xcb, 0x5f]);
        check("LD A,(HL)", &[0x7e]);
        check("ldi (hl),a", &[0x22]);
        check("ldd a,[hl]", &[0x3a]);
        check("ld [$ff00+c],a", &[0xe2]);
        check("ld hl,SP - 1", &[0xf8, 0xff]);
        for (s, kind, width) in [
            ("ld a,label", OperandKind::Unsigned8, 1),
            ("ld bc,label", OperandKind::Unsigned16, 2),
            ("add sp,offset", OperandKind::Signed8, 1),
            ("ldh a,[label]", OperandKind::High8, 1),
            ("jr label", OperandKind::Relative8, 1),
        ] {
            let e = instruction(s).unwrap();
            assert_eq!(e.patches.len(), 1);
            assert_eq!(e.patches[0].offset, 1);
            assert_eq!(e.patches[0].width, width);
            assert_eq!(e.patches[0].kind, kind);
        }
        assert_eq!(instruction("ld a,@").unwrap().patches.len(), 1);
    }

    #[test]
    fn invalid_operands_and_ranges() {
        for s in [
            "ld [hl],[hl]",
            "ld a,bc",
            "ld bc,a",
            "ld b,[bc]",
            "ld [sp],a",
            "ldh a,[$80]",
            "ldh [$10000],a",
            "ld a,256",
            "ld a,-129",
            "ld bc,65536",
            "ld bc,-32769",
            "add sp,128",
            "ld hl,sp-129",
            "bit 8,a",
            "res -1,b",
            "rst 7",
            "rst 64",
            "push sp",
            "pop f",
            "jp z",
            "jr hl",
            "inc af",
            "nop a",
            "rlc bc",
            "bit 1,bc",
            "ldh [hl],a",
            "ld a,",
            "unknown",
        ] {
            assert!(instruction(s).is_err(), "accepted {s}");
        }
    }
}

fn bytes(bytes: &[u8]) -> Result<Encoded, String> {
    Ok(Encoded {
        bytes: bytes.to_vec(),
        patches: Vec::new(),
    })
}

pub fn encode(mnemonic: &str, operands: &[String]) -> Result<Encoded, String> {
    use OperandKind::*;
    let mnemonic = mnemonic.to_ascii_lowercase();
    let o: Vec<String> = operands.iter().map(|s| normalized(s)).collect();
    let args: Vec<&str> = o.iter().map(String::as_str).collect();
    let invalid = || {
        Err(format!(
            "invalid operands for {mnemonic}: {}",
            operands.join(", ")
        ))
    };
    match (mnemonic.as_str(), args.as_slice()) {
        ("nop", []) => bytes(&[0]),
        ("halt", []) => bytes(&[0x76]),
        ("stop", []) => bytes(&[0x10, 0]),
        ("stop", [_]) => immediate(0x10, &operands[0], Unsigned8),
        ("di", []) => bytes(&[0xf3]),
        ("ei", []) => bytes(&[0xfb]),
        ("daa", []) => bytes(&[0x27]),
        ("cpl", []) => bytes(&[0x2f]),
        ("scf", []) => bytes(&[0x37]),
        ("ccf", []) => bytes(&[0x3f]),
        ("rlca", []) => bytes(&[7]),
        ("rrca", []) => bytes(&[0x0f]),
        ("rla", []) => bytes(&[0x17]),
        ("rra", []) => bytes(&[0x1f]),
        ("reti", []) => bytes(&[0xd9]),
        ("ret", []) => bytes(&[0xc9]),
        ("ret", [c]) => condition(c).map_or_else(invalid, |c| bytes(&[0xc0 + c * 8])),
        ("jp", ["hl" | "[hl]" | "(hl)"]) => bytes(&[0xe9]),
        ("jp" | "jr" | "call", [_]) => immediate(
            match mnemonic.as_str() {
                "jp" => 0xc3,
                "jr" => 0x18,
                _ => 0xcd,
            },
            &operands[0],
            if mnemonic == "jr" {
                Relative8
            } else {
                Unsigned16
            },
        ),
        ("jp" | "jr" | "call", [c, _]) => match condition(c) {
            Some(c) => immediate(
                match mnemonic.as_str() {
                    "jp" => 0xc2,
                    "jr" => 0x20,
                    _ => 0xc4,
                } + c * 8,
                &operands[1],
                if mnemonic == "jr" {
                    Relative8
                } else {
                    Unsigned16
                },
            ),
            None => invalid(),
        },
        ("rst", [_]) => {
            let n = constant(&operands[0])?;
            if !(0..=0x38).contains(&n) || n % 8 != 0 {
                return Err("RST vector must be $00, $08, ..., $38".into());
            }
            bytes(&[0xc7 | n as u8])
        }
        ("push" | "pop", [r]) => {
            let p = if *r == "af" {
                Some(3)
            } else {
                pair(r).filter(|p| *p != 3)
            };
            p.map_or_else(invalid, |p| {
                bytes(&[if mnemonic == "push" { 0xc5 } else { 0xc1 } + 16 * p])
            })
        }
        ("inc" | "dec", [r]) => {
            let dec = u8::from(mnemonic == "dec");
            if let Some(r) = reg(r) {
                bytes(&[4 + dec + r * 8])
            } else if let Some(p) = pair(r) {
                bytes(&[3 + dec * 8 + p * 16])
            } else {
                invalid()
            }
        }
        ("rlc" | "rrc" | "rl" | "rr" | "sla" | "sra" | "swap" | "srl", [r]) => {
            let group = ["rlc", "rrc", "rl", "rr", "sla", "sra", "swap", "srl"]
                .iter()
                .position(|s| *s == mnemonic)
                .unwrap() as u8;
            reg(r).map_or_else(invalid, |r| bytes(&[0xcb, group * 8 + r]))
        }
        ("bit" | "res" | "set", [_, r]) => {
            let n = constant(&operands[0])?;
            if !(0..=7).contains(&n) {
                return Err("bit index must be 0 through 7".into());
            }
            reg(r).map_or_else(invalid, |r| {
                bytes(&[
                    0xcb,
                    match mnemonic.as_str() {
                        "bit" => 0x40,
                        "res" => 0x80,
                        _ => 0xc0,
                    } + n as u8 * 8
                        + r,
                ])
            })
        }
        ("add", ["hl", r]) => pair(r).map_or_else(invalid, |p| bytes(&[9 + p * 16])),
        ("add", ["sp", _]) => immediate(0xe8, &operands[1], Signed8),
        ("add" | "adc" | "sub" | "sbc" | "and" | "xor" | "or" | "cp", [_])
        | ("add" | "adc" | "sub" | "sbc" | "and" | "xor" | "or" | "cp", ["a", _]) => {
            let group = ["add", "adc", "sub", "sbc", "and", "xor", "or", "cp"]
                .iter()
                .position(|s| *s == mnemonic)
                .unwrap() as u8;
            let i = operands.len() - 1;
            if let Some(r) = reg(&o[i]) {
                bytes(&[0x80 + group * 8 + r])
            } else {
                immediate(0xc6 + group * 8, &operands[i], Unsigned8)
            }
        }
        ("ldi" | "ldd", [a, b]) => {
            let opcode = match (*a, *b) {
                ("[hl]" | "(hl)", "a") => 0x22,
                ("a", "[hl]" | "(hl)") => 0x2a,
                _ => return invalid(),
            };
            bytes(&[opcode + if mnemonic == "ldd" { 0x10 } else { 0 }])
        }
        ("ld" | "ldh", [a, b]) => {
            let ia = indirect(a);
            let ib = indirect(b);
            if mnemonic == "ld" {
                if let (Some(a), Some(b)) = (reg(a), reg(b)) {
                    return if a == 6 && b == 6 {
                        invalid()
                    } else {
                        bytes(&[0x40 + a * 8 + b])
                    };
                }
                if *a == "sp" && *b == "hl" {
                    return bytes(&[0xf9]);
                }
                if *a == "hl" && (b.starts_with("sp+") || b.starts_with("sp-")) {
                    let original = operands[1].trim();
                    let offset = original.get(2..).unwrap().trim();
                    return immediate(0xf8, offset, Signed8);
                }
                for (memory, other, store) in [(ia, *b, true), (ib, *a, false)] {
                    if other != "a" {
                        continue;
                    }
                    let op = match memory {
                        Some("bc") => Some(2),
                        Some("de") => Some(0x12),
                        Some("hl+" | "hli") => Some(0x22),
                        Some("hl-" | "hld") => Some(0x32),
                        _ => None,
                    };
                    if let Some(op) = op {
                        return bytes(&[op + if store { 0 } else { 8 }]);
                    }
                }
                if let Some(p) = pair(a) {
                    return immediate(1 + p * 16, &operands[1], Unsigned16);
                }
                if let Some(r) = reg(a) {
                    if ib.is_none() {
                        return immediate(6 + r * 8, &operands[1], Unsigned8);
                    }
                }
                if ia.is_some() && *b == "sp" {
                    return immediate(8, indirect(operands[0].trim()).unwrap(), Unsigned16);
                }
            }
            let (memory, original, store) = if ia.is_some() && *b == "a" {
                (ia.unwrap(), &operands[0], true)
            } else if ib.is_some() && *a == "a" {
                (ib.unwrap(), &operands[1], false)
            } else {
                return invalid();
            };
            if matches!(memory, "c" | "$ff00+c" | "0xff00+c") {
                return bytes(&[if store { 0xe2 } else { 0xf2 }]);
            }
            if pair(memory).is_some()
                || reg(memory).is_some()
                || matches!(memory, "hl+" | "hl-" | "hli" | "hld" | "af")
            {
                return invalid();
            }
            immediate(
                if mnemonic == "ldh" {
                    if store { 0xe0 } else { 0xf0 }
                } else if store {
                    0xea
                } else {
                    0xfa
                },
                indirect(original.trim()).unwrap(),
                if mnemonic == "ldh" { High8 } else { Unsigned16 },
            )
        }
        _ => invalid(),
    }
}
