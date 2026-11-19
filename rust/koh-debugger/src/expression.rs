use koh_emulator::machine::Machine;

pub fn literal(token: &str, binary: bool) -> Option<i32> {
    if let Some(hex) = token
        .strip_prefix('$')
        .or_else(|| token.strip_prefix("0x"))
        .or_else(|| token.strip_prefix("0X"))
    {
        return u32::from_str_radix(hex.trim(), 16).ok().map(|v| v as i32);
    }
    if binary && let Some(bits) = token.strip_prefix('%') {
        return u32::from_str_radix(bits, 2).ok().map(|v| v as i32);
    }
    token.parse().ok()
}

pub fn address(token: &str) -> Option<u16> {
    u16::try_from(literal(token.trim(), false)?).ok()
}

pub fn hex_address(token: &str, dollar: bool) -> Option<u16> {
    let token = token.trim();
    let token = token
        .strip_prefix("0x")
        .or_else(|| token.strip_prefix("0X"))
        .unwrap_or(token);
    let token = if dollar {
        token.strip_prefix('$').unwrap_or(token)
    } else {
        token
    };
    u16::from_str_radix(token, 16).ok()
}

pub fn register(m: &Machine, name: &str, hardware: bool) -> Option<i32> {
    let r = &m.cpu.registers;
    Some(match name.to_ascii_uppercase().as_str() {
        "A" => r.a as i32,
        "F" => r.f as i32,
        "B" => r.b as i32,
        "C" => r.c as i32,
        "D" => r.d as i32,
        "E" => r.e as i32,
        "H" => r.h as i32,
        "L" => r.l as i32,
        "AF" => r.af() as i32,
        "BC" => r.bc() as i32,
        "DE" => r.de() as i32,
        "HL" => r.hl() as i32,
        "SP" => r.sp as i32,
        "PC" => r.pc as i32,
        "IME" if hardware => m.cpu.ime as i32,
        "IF" if hardware => m.interrupts.iflag as i32,
        "IE" if hardware => m.interrupts.ie as i32,
        "LY" if hardware => m.ppu.ly as i32,
        _ => return None,
    })
}

fn operand(m: &mut Machine, text: &str) -> Option<i32> {
    let token = text.trim();
    if let Some(inner) = token.strip_prefix('[').and_then(|s| s.strip_suffix(']')) {
        let inner = inner.trim();
        let addr = match inner.to_ascii_uppercase().as_str() {
            "HL" | "BC" | "DE" => register(m, inner, false)?,
            _ => literal(inner, false)?,
        };
        return Some(m.read_cpu(addr as u16) as i32);
    }
    register(m, token, false).or_else(|| literal(token, false))
}

pub fn condition(m: &mut Machine, expression: &str) -> bool {
    for op in ["==", "!=", "<=", ">=", "<", ">"] {
        if let Some(index) = expression.find(op).filter(|i| *i > 0) {
            let Some(lhs) = operand(m, &expression[..index]) else {
                return false;
            };
            let Some(rhs) = operand(m, &expression[index + op.len()..]) else {
                return false;
            };
            return match op {
                "==" => lhs == rhs,
                "!=" => lhs != rhs,
                "<=" => lhs <= rhs,
                ">=" => lhs >= rhs,
                "<" => lhs < rhs,
                _ => lhs > rhs,
            };
        }
    }
    false
}
