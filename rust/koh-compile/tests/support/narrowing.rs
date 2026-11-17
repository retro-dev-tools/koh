use koh_compile::{ir::*, resolve::Definition};

pub const INPUTS: &[[u64; 3]] = &[
    [0, 0, 0],
    [1, 2, 3],
    [3, 4, 5],
    [200, 100, 50],
    [200, 50, 3],
    [200, 200, 1],
    [127, 128, 255],
    [128, 127, 1],
    [255, 255, 255],
    [65535, 1, 2],
    [40000, 1000, 100],
    [65535, 65535, 65535],
    [u64::MAX, 0, 32],
    [256, 255, 8],
    [32768, 32767, 40],
    [0xff00ffff, 0x10001, 64],
    [1, 1, 10],
    [200, 50, 10],
    [1, 2, 16],
    [1, 3, 31],
    [1, 3, 33],
    [128, 4, u64::MAX],
];

pub fn expression(bits: u8, shape: &str) -> Function {
    let mut instructions = Vec::new();
    let mut value = |ty, op| {
        let id = instructions.len() as u32 + 3;
        instructions.push(Instruction {
            result: Some((id, ty)),
            op,
            il_offset: id,
        });
        id
    };
    let a = value(
        Type::I32,
        Op::Convert {
            value: 0,
            bits,
            signed: matches!(shape, "slt" | "sult" | "seq"),
        },
    );
    let b = value(
        Type::I32,
        Op::Convert {
            value: 1,
            bits,
            signed: matches!(shape, "mixed" | "slt" | "sult" | "seq" | "mixed-eq"),
        },
    );
    let c = value(
        Type::I32,
        Op::Convert {
            value: 2,
            bits,
            signed: false,
        },
    );
    let constant = value(Type::I32, Op::Constant(300));
    let mut result = match shape {
        "zlt" | "zult" | "slt" | "zge" | "sult" | "zeq" | "seq" | "mixed-eq" | "constant-lt" => {
            let right = if shape == "constant-lt" { constant } else { b };
            let op = match shape {
                "zeq" | "seq" | "mixed-eq" => Comparison::Equal,
                "zult" | "sult" | "constant-lt" => Comparison::LessUnsigned,
                _ => Comparison::LessSigned,
            };
            let compared = value(Type::I32, Op::Compare { op, left: a, right });
            if shape == "zge" {
                let zero = value(Type::I32, Op::Constant(0));
                value(
                    Type::I32,
                    Op::Compare {
                        op: Comparison::Equal,
                        left: compared,
                        right: zero,
                    },
                )
            } else {
                compared
            }
        }
        "shift" => {
            let count = value(
                Type::I32,
                Op::Convert {
                    value: 2,
                    bits: 32,
                    signed: false,
                },
            );
            let shifted = value(
                Type::I32,
                Op::Binary {
                    op: Binary::ShiftLeft,
                    left: a,
                    right: count,
                },
            );
            value(
                Type::I32,
                Op::Convert {
                    value: shifted,
                    bits,
                    signed: false,
                },
            )
        }
        "divide" => {
            let quotient = value(
                Type::I32,
                Op::Binary {
                    op: Binary::DivideUnsigned,
                    left: a,
                    right: b,
                },
            );
            value(
                Type::I32,
                Op::Convert {
                    value: quotient,
                    bits,
                    signed: false,
                },
            )
        }
        "sum" | "mixed" | "constant" | "chain" | "wide-sum" | "wide-chain" | "two-widths" => {
            let sum = value(
                Type::I32,
                Op::Binary {
                    op: Binary::Add,
                    left: a,
                    right: if shape == "constant" { constant } else { b },
                },
            );
            let outer = if matches!(shape, "chain" | "wide-chain") {
                value(
                    Type::I32,
                    Op::Binary {
                        op: Binary::Add,
                        left: sum,
                        right: c,
                    },
                )
            } else {
                sum
            };
            let narrow = value(
                Type::I32,
                Op::Convert {
                    value: outer,
                    bits,
                    signed: false,
                },
            );
            if matches!(shape, "wide-sum" | "wide-chain" | "two-widths") {
                let wide = if shape == "two-widths" {
                    value(Type::I64, Op::Constant(0))
                } else {
                    value(
                        Type::I64,
                        Op::Convert {
                            value: sum,
                            bits: 64,
                            signed: false,
                        },
                    )
                };
                let shift = value(Type::I32, Op::Constant(32));
                let wide = value(
                    Type::I64,
                    Op::Binary {
                        op: Binary::ShiftLeft,
                        left: wide,
                        right: shift,
                    },
                );
                let byte = value(
                    Type::I32,
                    Op::Convert {
                        value: outer,
                        bits: 8,
                        signed: false,
                    },
                );
                let byte = value(
                    Type::I64,
                    Op::Convert {
                        value: byte,
                        bits: 64,
                        signed: false,
                    },
                );
                let word = value(
                    Type::I32,
                    Op::Convert {
                        value: outer,
                        bits: 16,
                        signed: false,
                    },
                );
                let word = value(
                    Type::I64,
                    Op::Convert {
                        value: word,
                        bits: 64,
                        signed: false,
                    },
                );
                let shift = value(Type::I32, Op::Constant(8));
                let word = value(
                    Type::I64,
                    Op::Binary {
                        op: Binary::ShiftLeft,
                        left: word,
                        right: shift,
                    },
                );
                let low = value(
                    Type::I64,
                    Op::Binary {
                        op: Binary::Or,
                        left: word,
                        right: byte,
                    },
                );
                let packed = value(
                    Type::I64,
                    Op::Binary {
                        op: Binary::Or,
                        left: wide,
                        right: low,
                    },
                );
                return finish(instructions, Type::I64, packed);
            }
            narrow
        }
        _ => panic!("Unknown narrowing shape: {shape}"),
    };
    result = value(
        Type::I64,
        Op::Convert {
            value: result,
            bits: 64,
            signed: false,
        },
    );
    finish(instructions, Type::I64, result)
}

fn finish(instructions: Vec<Instruction>, returns: Type, result: u32) -> Function {
    Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![Type::I64; 3],
        returns: Some(returns),
        locals: vec![],
        blocks: vec![Block {
            parameters: vec![(0, Type::I64), (1, Type::I64), (2, Type::I64)],
            instructions,
            terminator: Terminator {
                kind: Control::Return(Some(result)),
                il_offset: 100,
            },
        }],
    }
}

pub fn expected(bits: u8, shape: &str, inputs: [u64; 3]) -> Result<u64, u8> {
    let mask = (1u32 << bits) - 1;
    let [a, b, c] = inputs.map(|v| v as u32 & mask);
    let signed = |v: u32| ((v << (32 - bits)) as i32) >> (32 - bits);
    let result = match shape {
        "zlt" | "zult" => u64::from(a < b),
        "slt" => u64::from(signed(a) < signed(b)),
        "zge" => u64::from(a >= b),
        "sult" => u64::from((signed(a) as u32) < (signed(b) as u32)),
        "zeq" | "seq" => u64::from(a == b),
        "mixed-eq" => u64::from(a == signed(b) as u32),
        "constant-lt" => u64::from(a < 300),
        "shift" => u64::from(a.wrapping_shl(inputs[2] as u32) & mask),
        "divide" if b == 0 => return Err(2),
        "divide" => u64::from((a / b) & mask),
        "sum" | "mixed" | "constant" | "chain" | "wide-sum" | "wide-chain" | "two-widths" => {
            let right = match shape {
                "mixed" => signed(b) as u32,
                "constant" => 300,
                _ => b,
            };
            let sum = a.wrapping_add(right);
            let outer = if matches!(shape, "chain" | "wide-chain") {
                sum.wrapping_add(c)
            } else {
                sum
            };
            if matches!(shape, "wide-sum" | "wide-chain" | "two-widths") {
                let high = if shape == "two-widths" {
                    0
                } else {
                    u64::from(sum) << 32
                };
                high | (u64::from(outer & 65535) << 8) | u64::from(outer & 255)
            } else {
                u64::from(outer & mask)
            }
        }
        _ => panic!("Unknown narrowing oracle shape: {shape}"),
    };
    Ok(result)
}

pub fn counter(direct: bool) -> Function {
    let value = |id, ty, op| Instruction {
        result: Some((id, ty)),
        op,
        il_offset: id,
    };
    let edge = |block, arguments| Edge { block, arguments };
    let terminator = |kind| Terminator { kind, il_offset: 0 };
    Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![Type::I64, Type::I32],
        returns: Some(Type::I64),
        locals: vec![],
        blocks: vec![
            Block {
                parameters: vec![(0, Type::I64), (1, Type::I32)],
                instructions: vec![
                    value(
                        2,
                        Type::I32,
                        Op::Convert {
                            value: 0,
                            bits: 8,
                            signed: false,
                        },
                    ),
                    value(3, Type::I32, Op::Constant(0)),
                ],
                terminator: terminator(Control::Jump(edge(1, vec![2, 3]))),
            },
            Block {
                parameters: vec![(4, Type::I32), (5, Type::I32)],
                instructions: vec![value(
                    6,
                    Type::I32,
                    Op::Compare {
                        op: if direct {
                            Comparison::LessSigned
                        } else {
                            Comparison::LessUnsigned
                        },
                        left: if direct { 4 } else { 5 },
                        right: 1,
                    },
                )],
                terminator: terminator(Control::Branch {
                    condition: 6,
                    yes: edge(2, vec![]),
                    no: edge(3, vec![4, 5]),
                }),
            },
            Block {
                parameters: vec![],
                instructions: vec![
                    value(7, Type::I32, Op::Constant(1)),
                    value(
                        8,
                        Type::I32,
                        Op::Binary {
                            op: Binary::Add,
                            left: 4,
                            right: 7,
                        },
                    ),
                    value(
                        9,
                        Type::I32,
                        Op::Convert {
                            value: 8,
                            bits: 8,
                            signed: false,
                        },
                    ),
                    value(
                        10,
                        Type::I32,
                        Op::Binary {
                            op: Binary::Add,
                            left: 5,
                            right: 7,
                        },
                    ),
                ],
                terminator: terminator(Control::Jump(edge(1, vec![9, 10]))),
            },
            Block {
                parameters: vec![(11, Type::I32), (12, Type::I32)],
                instructions: vec![
                    value(
                        13,
                        Type::I64,
                        Op::Convert {
                            value: 12,
                            bits: 64,
                            signed: false,
                        },
                    ),
                    value(14, Type::I32, Op::Constant(32)),
                    value(
                        15,
                        Type::I64,
                        Op::Binary {
                            op: Binary::ShiftLeft,
                            left: 13,
                            right: 14,
                        },
                    ),
                    value(
                        16,
                        Type::I64,
                        Op::Convert {
                            value: 11,
                            bits: 64,
                            signed: false,
                        },
                    ),
                    value(
                        17,
                        Type::I64,
                        Op::Binary {
                            op: Binary::Or,
                            left: 15,
                            right: 16,
                        },
                    ),
                ],
                terminator: terminator(Control::Return(Some(17))),
            },
        ],
    }
}
