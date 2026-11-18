use koh_compile::{ir::*, resolve::Definition};

fn expression(bits: u8, count: usize, steps: &[(Binary, u32, u32)]) -> Function {
    let ty = match bits {
        16 => Type::Native,
        64 => Type::I64,
        _ => Type::I32,
    };
    let mut f = Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![ty; count],
        returns: Some(ty),
        locals: vec![],
        blocks: vec![Block {
            parameters: (0..count as u32).map(|id| (id, ty)).collect(),
            instructions: steps
                .iter()
                .enumerate()
                .map(|(index, &(op, left, right))| {
                    let id = count as u32 + index as u32;
                    Instruction {
                        result: Some((id, ty)),
                        op: Op::Binary { op, left, right },
                        il_offset: id,
                    }
                })
                .collect(),
            terminator: Terminator {
                il_offset: 100,
                kind: Control::Return(Some((count + steps.len() - 1) as u32)),
            },
        }],
    };
    for &(op, _, right) in steps {
        if op == Binary::ShiftLeft {
            f.parameters[right as usize] = Type::I32;
            f.blocks[0].parameters[right as usize].1 = Type::I32;
        }
    }
    if bits == 8 {
        let id = (count + steps.len()) as u32;
        f.blocks[0].instructions.push(Instruction {
            result: Some((id, ty)),
            op: Op::Convert {
                value: id - 1,
                bits: 8,
                signed: false,
            },
            il_offset: id,
        });
        f.blocks[0].terminator.kind = Control::Return(Some(id));
    }
    f
}

pub fn cases(bits: u8) -> Vec<(Function, Vec<u64>, u64)> {
    use Binary::*;
    let mask = u64::MAX >> (64 - bits);
    let mut cases = Vec::new();
    for (inputs, steps, expected) in [
        (vec![3, 4, 100], vec![(Add, 0, 1), (Add, 3, 2)], 107),
        (
            vec![10, 20, 5, 100],
            vec![(Add, 0, 1), (Add, 4, 2), (Add, 5, 3)],
            135,
        ),
        (vec![3, 4, 1], vec![(Add, 0, 1), (ShiftLeft, 3, 2)], 14),
        (vec![300, 40, 100], vec![(Add, 0, 1), (Add, 3, 2)], 440),
        (
            vec![3, 4, 5, 6],
            vec![(Add, 0, 1), (Add, 2, 3), (Add, 4, 5)],
            18,
        ),
        (
            vec![10, 20, 5, 7, 0],
            vec![(Add, 0, 1), (Add, 5, 2), (Add, 6, 3), (Add, 7, 4)],
            42,
        ),
        (
            vec![300, 40, 100, 200],
            vec![(Add, 0, 1), (Add, 4, 2), (Add, 5, 3)],
            640,
        ),
        (
            vec![10, 20, 1, 2, 3, 4, 5, 6],
            vec![
                (Add, 0, 1),
                (Add, 2, 3),
                (Add, 4, 5),
                (Add, 6, 7),
                (Add, 8, 9),
                (Add, 10, 11),
                (Add, 12, 13),
            ],
            51,
        ),
        (
            vec![10, 20, 5, 6],
            vec![(Add, 0, 1), (Add, 2, 3), (Subtract, 4, 5)],
            19,
        ),
        (
            vec![0xf0, 0x0f, 0x3c, 3],
            vec![(Or, 0, 1), (And, 4, 2), (Xor, 5, 3)],
            0x3f,
        ),
    ] {
        cases.push((
            expression(bits, inputs.len(), &steps),
            inputs,
            expected & mask,
        ));
    }
    let chain: Vec<_> = (0..10)
        .map(|index| (Add, if index == 0 { 0 } else { index + 1 }, 1))
        .collect();
    cases.push((expression(bits, 2, &chain), vec![0, 1], 10));
    cases.push((expression(bits, 2, &chain), vec![mask - 5, 1], 4));
    for boundary in [8, 16, 32].into_iter().filter(|&boundary| boundary < bits) {
        let next = 1u64 << boundary;
        cases.push((expression(bits, 2, &[(Add, 0, 1)]), vec![next - 1, 1], next));
        cases.push((
            expression(bits, 2, &[(Subtract, 0, 1)]),
            vec![next, 1],
            next - 1,
        ));
    }
    cases.push((expression(bits, 2, &[(Add, 0, 1)]), vec![mask, 1], 0));
    cases.push((expression(bits, 2, &[(Subtract, 0, 1)]), vec![0, 1], mask));
    if bits == 64 {
        for value in [0x0102030405060708, u64::MAX] {
            cases.push((expression(bits, 1, &[]), vec![value], value));
        }
    }
    cases
}

pub fn parameter_loop() -> Function {
    let instruction = |id, op| Instruction {
        result: Some((id, Type::I32)),
        op,
        il_offset: id,
    };
    Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![Type::I32; 2],
        returns: Some(Type::I32),
        locals: vec![],
        blocks: vec![
            Block {
                parameters: vec![(0, Type::I32), (1, Type::I32)],
                instructions: vec![],
                terminator: Terminator {
                    il_offset: 0,
                    kind: Control::Jump(Edge {
                        block: 1,
                        arguments: vec![1],
                    }),
                },
            },
            Block {
                parameters: vec![(10, Type::I32)],
                instructions: vec![
                    instruction(2, Op::Constant(1)),
                    instruction(
                        3,
                        Op::Binary {
                            op: Binary::Add,
                            left: 0,
                            right: 2,
                        },
                    ),
                    instruction(
                        4,
                        Op::Convert {
                            value: 3,
                            bits: 8,
                            signed: false,
                        },
                    ),
                    instruction(
                        5,
                        Op::Binary {
                            op: Binary::ShiftLeft,
                            left: 4,
                            right: 2,
                        },
                    ),
                    instruction(
                        6,
                        Op::Convert {
                            value: 5,
                            bits: 8,
                            signed: false,
                        },
                    ),
                    instruction(
                        7,
                        Op::Binary {
                            op: Binary::Add,
                            left: 4,
                            right: 6,
                        },
                    ),
                    instruction(
                        8,
                        Op::Convert {
                            value: 7,
                            bits: 8,
                            signed: false,
                        },
                    ),
                    instruction(
                        9,
                        Op::Binary {
                            op: Binary::Subtract,
                            left: 10,
                            right: 2,
                        },
                    ),
                ],
                terminator: Terminator {
                    il_offset: 10,
                    kind: Control::Branch {
                        condition: 9,
                        yes: Edge {
                            block: 1,
                            arguments: vec![9],
                        },
                        no: Edge {
                            block: 2,
                            arguments: vec![],
                        },
                    },
                },
            },
            Block {
                parameters: vec![],
                instructions: vec![],
                terminator: Terminator {
                    il_offset: 11,
                    kind: Control::Return(Some(8)),
                },
            },
        ],
    }
}
