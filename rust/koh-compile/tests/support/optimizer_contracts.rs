use koh_compile::{
    ir::*,
    lower::{Method, lower},
    resolve::Definition,
};

fn conversion_opcode(bits: u16, signed: bool) -> u8 {
    match (bits, signed) {
        (8, false) => 0xd2,
        (8, true) => 0x67,
        (16, false) => 0xd1,
        (16, true) => 0x68,
        (32, false) => 0x6d,
        (32, true) => 0x69,
        (64, false) => 0x6e,
        (64, true) => 0x6a,
        _ => panic!("Invalid test width"),
    }
}

fn function(il: &[u8], parameters: Vec<Type>, returns: Type) -> Function {
    lower(Method {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        il,
        parameters,
        returns: Some(returns),
        locals: vec![],
        init_locals: true,
        max_stack: 3,
    })
    .unwrap()
}

pub fn comparison(bits: u16, shape: &str) -> Function {
    let mut il = vec![0x02, conversion_opcode(bits, shape == "eq-one")];
    if shape == "nonboolean" {
        if bits == 64 {
            il.push(0x21);
            il.extend(0u64.to_le_bytes());
        } else {
            il.push(0x16);
        }
        il.extend([0xfe, 0x01]);
    } else {
        il.extend([
            0x03,
            conversion_opcode(bits, shape == "eq-one"),
            0xfe,
            match shape {
                "double-negation" => 0x01,
                "eq-one" => 0x02,
                "ne-zero" | "eq-zero" => 0x05,
                _ => panic!("Invalid test shape"),
            },
        ]);
        match shape {
            "double-negation" => il.extend([0x16, 0xfe, 0x01, 0x16, 0xfe, 0x03]),
            "ne-zero" => il.extend([0x16, 0xfe, 0x03]),
            "eq-zero" => il.extend([0x16, 0xfe, 0x01]),
            "eq-one" => il.extend([0x17, 0xfe, 0x01]),
            _ => unreachable!(),
        }
    }
    il.push(0x2a);
    function(&il, vec![Type::I64, Type::I64], Type::I32)
}

pub fn conversion(bits: u16, shape: &str) -> Function {
    let mut il = vec![0x02];
    let mut returns = Type::I32;
    let wide = if bits == 32 { 64 } else { 32 };
    match shape {
        "zero-roundtrip" => il.extend([
            conversion_opcode(bits, false),
            conversion_opcode(wide, false),
            conversion_opcode(bits, false),
        ]),
        "sign-roundtrip" => il.extend([
            conversion_opcode(bits, false),
            conversion_opcode(bits, true),
            conversion_opcode(wide, true),
            conversion_opcode(bits, false),
        ]),
        "different-width" => {
            il.extend([
                conversion_opcode(bits, false),
                conversion_opcode(wide, false),
                conversion_opcode(bits * 2, false),
            ]);
            if bits == 32 {
                returns = Type::I64;
            }
        }
        "reverse" => {
            il.extend([
                conversion_opcode(if bits == 32 { 64 } else { 32 }, false),
                conversion_opcode(bits, false),
                conversion_opcode(wide, false),
            ]);
            if bits == 32 {
                returns = Type::I64;
            } else {
                il.push(0x6d);
            }
        }
        "increment" => {
            il.extend([
                conversion_opcode(bits, false),
                conversion_opcode(wide, false),
            ]);
            if wide == 64 {
                il.push(0x21);
                il.extend(1u64.to_le_bytes());
            } else {
                il.push(0x17);
            }
            il.extend([
                0x58,
                conversion_opcode(bits, false),
                conversion_opcode(wide, false),
                conversion_opcode(bits, false),
            ]);
        }
        _ => panic!("Invalid test shape"),
    }
    il.push(0x2a);
    function(&il, vec![Type::I64], returns)
}

pub fn comparison_expected(bits: u16, shape: &str, a: u64, b: u64) -> u64 {
    let mask = u64::MAX >> (64 - bits);
    let a = a & mask;
    let b = b & mask;
    let signed = |value: u64| {
        if value & (1u64 << (bits - 1)) != 0 {
            value as i128 - (1i128 << bits)
        } else {
            value as i128
        }
    };
    u64::from(match shape {
        "double-negation" => a != b,
        "ne-zero" => a < b,
        "eq-zero" => a >= b,
        "eq-one" => signed(a) > signed(b),
        "nonboolean" => a == 0,
        _ => panic!("Invalid test shape"),
    })
}

pub fn conversion_expected(bits: u16, shape: &str, value: u64) -> u64 {
    let mask = u64::MAX >> (64 - bits);
    match shape {
        "increment" => value.wrapping_add(1) & mask,
        "zero-roundtrip" | "sign-roundtrip" | "different-width" | "reverse" => value & mask,
        _ => panic!("Invalid test shape"),
    }
}

pub fn comparison_pairs(bits: u16) -> [(u64, u64); 14] {
    let mask = u64::MAX >> (64 - bits);
    let sign = 1u64 << (bits - 1);
    [
        (0, 0),
        (0, 1),
        (1, 0),
        (1, 1),
        (0, 2),
        (2, 0),
        (127, 128),
        (sign - 1, sign),
        (sign, sign - 1),
        (mask, 0),
        (0, mask),
        (mask, mask),
        (2, 1),
        (1, 2),
    ]
}

pub const CONVERSION_VALUES: [u64; 12] = [
    0,
    1,
    0x7f,
    0x80,
    0xff,
    0x100,
    0x7fff,
    0x8000,
    0xffff,
    0x10000,
    0x1234_5678_9abc_def0,
    u64::MAX,
];
