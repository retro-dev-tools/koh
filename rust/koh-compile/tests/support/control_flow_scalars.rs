use koh_compile::ir::*;

fn binary(bits: u16, operation: Binary) -> Function {
    let ty = if bits == 16 { Type::Native } else { Type::I32 };
    let mut f = super::function(
        ty,
        Op::Binary {
            op: operation,
            left: 0,
            right: 1,
        },
    );
    if matches!(
        operation,
        Binary::ShiftLeft | Binary::ShiftRightSigned | Binary::ShiftRightUnsigned
    ) {
        f.parameters[1] = Type::I32;
        f.blocks[0].parameters[1].1 = Type::I32;
    }
    if bits == 8 {
        f.blocks[0].instructions = vec![
            instruction(
                3,
                Type::I32,
                Op::Convert {
                    value: 0,
                    bits: 8,
                    signed: operation == Binary::ShiftRightSigned,
                },
            ),
            instruction(
                4,
                Type::I32,
                Op::Convert {
                    value: 1,
                    bits: 8,
                    signed: false,
                },
            ),
            instruction(
                2,
                Type::I32,
                Op::Binary {
                    op: operation,
                    left: 3,
                    right: 4,
                },
            ),
            instruction(
                5,
                Type::I32,
                Op::Convert {
                    value: 2,
                    bits: 8,
                    signed: false,
                },
            ),
        ];
        f.blocks[0].terminator.kind = Control::Return(Some(5));
    }
    f
}

fn instruction(id: u32, ty: Type, op: Op) -> Instruction {
    Instruction {
        result: Some((id, ty)),
        op,
        il_offset: id,
    }
}

pub fn cases(bits: u16) -> Vec<(Function, Vec<u64>, u64)> {
    let mut cases = Vec::new();
    for (width, operation, left, right, expected) in [
        (16, Binary::Add, 300, 250, 550),
        (16, Binary::Subtract, 300, 150, 150),
        (8, Binary::Multiply, 6, 7, 42),
        (16, Binary::Multiply, 300, 4, 1200),
        (8, Binary::Multiply, 0xfd, 4, 0xf4),
        (16, Binary::DivideUnsigned, 1000, 7, 142),
        (16, Binary::RemainderUnsigned, 1000, 7, 6),
        (8, Binary::DivideUnsigned, 100, 9, 11),
        (16, Binary::DivideSigned, 0xfc18, 7, 0xff72),
        (16, Binary::RemainderSigned, 0xfc18, 7, 0xfffa),
        (16, Binary::DivideSigned, 0xfc18, 0xfff9, 142),
        (8, Binary::ShiftLeft, 1, 4, 16),
        (8, Binary::ShiftRightUnsigned, 0x80, 3, 0x10),
        (8, Binary::ShiftRightSigned, 0xf8, 1, 0xfc),
        (16, Binary::ShiftLeft, 1, 12, 4096),
        (16, Binary::ShiftRightUnsigned, 0x8000, 15, 1),
        (16, Binary::ShiftRightSigned, 0xfff0, 2, 0xfffc),
        (8, Binary::ShiftLeft, 3, 4, 48),
        (32, Binary::Add, 70000, 100, 70100),
        (32, Binary::Subtract, 100000, 1, 99999),
        (32, Binary::And, 0x00ff00ff, 0x0f0f0f0f, 0x000f000f),
        (32, Binary::Add, 0xffff, 1, 0x10000),
        (32, Binary::Add, 0xffffff, 1, 0x1000000),
        (32, Binary::Add, 0xffffffff, 1, 0),
        (32, Binary::Subtract, 0x10000, 1, 0xffff),
        (32, Binary::Subtract, 0x1000000, 1, 0xffffff),
        (32, Binary::Subtract, 0, 1, 0xffffffff),
    ] {
        if width == bits {
            cases.push((binary(bits, operation), vec![left, right], expected));
        }
    }
    for (width, operation, left, right, expected) in [
        (16, Comparison::LessUnsigned, 5, 10, 1),
        (16, Comparison::GreaterUnsigned, 10, 5, 1),
        (16, Comparison::Equal, 7, 8, 0),
        (8, Comparison::LessSigned, 0xfb, 3, 1),
        (8, Comparison::GreaterSigned, 0xff, 0xfe, 1),
        (8, Comparison::LessSigned, 0xff, 1, 1),
        (8, Comparison::LessUnsigned, 0xff, 1, 0),
        (16, Comparison::LessSigned, 0xfc18, 5, 1),
        (16, Comparison::LessSigned, 299, 300, 1),
        (16, Comparison::LessSigned, 300, 299, 0),
        (16, Comparison::LessSigned, 0xfed4, 0xfed5, 1),
        (32, Comparison::GreaterSigned, 100000, 99999, 1),
        (32, Comparison::LessSigned, 0x80000000, 0x7fffffff, 1),
        (32, Comparison::GreaterSigned, 0x7fffffff, 0x80000000, 1),
        (32, Comparison::GreaterSigned, 0xffffffff, 0, 0),
    ] {
        if width != bits {
            continue;
        }
        let ty = if bits == 16 { Type::Native } else { Type::I32 };
        let mut f = super::function(
            ty,
            Op::Compare {
                op: operation,
                left: 0,
                right: 1,
            },
        );
        f.returns = Some(Type::I32);
        f.blocks[0].instructions[0].result = Some((2, Type::I32));
        if bits == 8 {
            let signed = matches!(
                operation,
                Comparison::LessSigned | Comparison::GreaterSigned
            );
            f.blocks[0].instructions = vec![
                instruction(
                    3,
                    Type::I32,
                    Op::Convert {
                        value: 0,
                        bits: 8,
                        signed,
                    },
                ),
                instruction(
                    4,
                    Type::I32,
                    Op::Convert {
                        value: 1,
                        bits: 8,
                        signed,
                    },
                ),
                instruction(
                    2,
                    Type::I32,
                    Op::Compare {
                        op: operation,
                        left: 3,
                        right: 4,
                    },
                ),
            ];
        }
        cases.push((f, vec![left, right], expected));
    }
    for (source, target, signed, value, expected) in [
        (8, 32, false, 255, 255),
        (8, 16, false, 200, 200),
        (8, 16, true, 200, 0xffc8),
        (16, 8, false, 0x1234, 0x34),
    ] {
        if target != bits {
            continue;
        }
        let from = if source == 16 {
            Type::Native
        } else {
            Type::I32
        };
        let to = if target == 16 {
            Type::Native
        } else {
            Type::I32
        };
        let mut f = super::function(
            from,
            Op::Convert {
                value: 0,
                bits: target as u8,
                signed,
            },
        );
        f.parameters.truncate(1);
        f.blocks[0].parameters.truncate(1);
        f.returns = Some(to);
        f.blocks[0].instructions[0].result = Some((2, to));
        if source == 8 {
            f.blocks[0].instructions.insert(
                0,
                instruction(
                    3,
                    Type::I32,
                    Op::Convert {
                        value: 0,
                        bits: 8,
                        signed,
                    },
                ),
            );
            f.blocks[0].instructions[1].op = Op::Convert {
                value: 3,
                bits: target as u8,
                signed,
            };
        }
        cases.push((f, vec![value], expected));
    }
    cases
}

pub fn constant_inputs(mut f: Function, arguments: &[u64]) -> Function {
    let mut prefix = f.blocks[0]
        .parameters
        .iter()
        .zip(arguments)
        .map(|(&(id, ty), &value)| instruction(id, ty, Op::Constant(value)))
        .collect::<Vec<_>>();
    prefix.append(&mut f.blocks[0].instructions);
    f.blocks[0].instructions = prefix;
    f.blocks[0].parameters.clear();
    f.parameters.clear();
    f
}

pub fn subtraction_chain() -> Function {
    let mut f = super::function(Type::Native, Op::Constant(0));
    f.parameters.clear();
    f.blocks[0].parameters.clear();
    f.blocks[0].instructions = vec![
        instruction(0, Type::Native, Op::Constant(200)),
        instruction(1, Type::Native, Op::Constant(100)),
        instruction(
            2,
            Type::Native,
            Op::Binary {
                op: Binary::Add,
                left: 0,
                right: 1,
            },
        ),
        instruction(3, Type::Native, Op::Constant(100)),
        instruction(4, Type::Native, Op::Constant(50)),
        instruction(
            5,
            Type::Native,
            Op::Binary {
                op: Binary::Add,
                left: 3,
                right: 4,
            },
        ),
        instruction(
            6,
            Type::Native,
            Op::Binary {
                op: Binary::Subtract,
                left: 2,
                right: 5,
            },
        ),
    ];
    f.blocks[0].terminator.kind = Control::Return(Some(6));
    f
}

pub fn dynamic_subtraction_chain() -> Function {
    let mut f = super::function(Type::Native, Op::Constant(0));
    f.parameters = vec![Type::Native; 3];
    f.blocks[0].parameters = vec![(0, Type::Native), (1, Type::Native), (2, Type::Native)];
    f.blocks[0].instructions = vec![
        instruction(
            3,
            Type::Native,
            Op::Binary {
                op: Binary::Add,
                left: 0,
                right: 1,
            },
        ),
        instruction(
            4,
            Type::Native,
            Op::Binary {
                op: Binary::Add,
                left: 1,
                right: 2,
            },
        ),
        instruction(
            5,
            Type::Native,
            Op::Binary {
                op: Binary::Subtract,
                left: 3,
                right: 4,
            },
        ),
    ];
    f.blocks[0].terminator.kind = Control::Return(Some(5));
    f
}
