use super::optimizer_contracts::conversion_opcode;
use koh_compile::{
    ir::*,
    lower::{Method, lower},
    resolve::Definition,
};

fn operands(il: &mut Vec<u8>, bits: u16, signed: bool) {
    let conversion = conversion_opcode(bits, signed);
    il.extend([0x02, conversion, 0x03, conversion]);
}

pub fn function(bits: u16, predicate: Comparison, shape: &str) -> Function {
    let mut il = Vec::new();
    operands(
        &mut il,
        bits,
        predicate == Comparison::LessSigned && shape != "mixed",
    );
    il.extend([
        0xfe,
        match predicate {
            Comparison::Equal => 0x01,
            Comparison::LessUnsigned => 0x05,
            Comparison::LessSigned => 0x04,
            _ => panic!("invalid fixture predicate"),
        },
    ]);
    match shape {
        "single" => {}
        "shared" => il.extend([0x25, 0x1f, 10, 0x58, 0x0a]),
        "barrier" => {
            il.push(0x0a);
            il.push(0x20);
            il.extend(0xff43i32.to_le_bytes());
            il.extend([0xe0, 0x17, 0x52, 0x06]);
        }
        "mixed" => {
            il.extend([0x0a, 0x06, 0x17, 0x58, 0x0b]);
            operands(&mut il, bits, false);
            il.extend([0xfe, 0x05]);
        }
        _ => panic!("invalid fixture shape"),
    }
    let branch = il.len();
    il.extend([0x2c, 0]);
    match shape {
        "single" | "barrier" => il.extend([0x1f, 111, 0x2a]),
        "shared" => il.extend([0x06, 0x2a]),
        "mixed" => il.extend([0x07, 0x06, 0x58, 0x2a]),
        _ => unreachable!(),
    }
    il[branch + 1] = u8::try_from(il.len() - branch - 2).unwrap();
    match shape {
        "single" | "barrier" => {
            il.push(0x20);
            il.extend(222i32.to_le_bytes());
            il.push(0x2a);
        }
        "shared" => il.extend([0x06, 0x1f, 89, 0x58, 0x2a]),
        "mixed" => il.extend([0x16, 0x2a]),
        _ => unreachable!(),
    }
    lower(Method {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        il: &il,
        parameters: vec![Type::I64, Type::I64],
        returns: Some(Type::I32),
        locals: vec![Type::I32; 2],
        init_locals: true,
        max_stack: 3,
    })
    .unwrap()
}

pub fn inputs(bits: u16) -> Vec<(Comparison, u64, u64, bool)> {
    let (maximum, negative, equal) = match bits {
        8 => (255, -5i64, 7),
        16 => (65535, -1000, 300),
        32 => (u64::from(u32::MAX), -100000, 100000),
        64 => (u64::MAX, i64::MIN, 1u64 << 40),
        _ => panic!("invalid fixture width"),
    };
    let mut inputs = vec![
        (Comparison::LessUnsigned, 1, maximum, true),
        (Comparison::LessUnsigned, maximum, 1, false),
        (
            Comparison::LessSigned,
            negative as u64,
            if bits == 8 { 3 } else { 5 },
            true,
        ),
        (Comparison::LessSigned, 5, negative as u64, false),
        (Comparison::Equal, equal, equal, true),
        (Comparison::Equal, equal, equal + 1, false),
        (Comparison::Equal, 42, 42, true),
        (Comparison::Equal, 42, 43, false),
    ];
    if bits == 64 {
        inputs.extend([
            (Comparison::Equal, 1 << 40, 0, false),
            (Comparison::LessUnsigned, 1 << 40, 1 << 48, true),
            (Comparison::LessUnsigned, 1 << 48, 1 << 40, false),
            (Comparison::LessSigned, i64::MIN as u64, u64::MAX, true),
            (Comparison::LessSigned, u64::MAX, i64::MIN as u64, false),
        ]);
    }
    inputs
}
