use koh_compile::{ir::*, resolve::Definition};

pub fn pointer_loop(shape: &str) -> Function {
    let value = |id, ty, op| Instruction {
        result: Some((id, ty)),
        op,
        il_offset: id,
    };
    let mut body = vec![
        value(5, Type::Native, Op::LoadLocal(0)),
        value(6, Type::Native, Op::Constant(1)),
        value(
            7,
            Type::Native,
            Op::Binary {
                op: Binary::Add,
                left: 5,
                right: 6,
            },
        ),
    ];
    if shape == "variant" {
        body.push(Instruction {
            result: None,
            op: Op::StoreLocal { local: 0, value: 7 },
            il_offset: 7,
        });
    } else if shape == "alias" {
        body.extend([
            value(
                10,
                Type::ByRef(Scalar::NativeUnsigned),
                Op::LocalAddress {
                    local: 0,
                    storage: FieldStorage::Value(Storage::Scalar(Scalar::NativeUnsigned)),
                },
            ),
            Instruction {
                result: None,
                op: Op::StoreMemory {
                    address: 10,
                    value: 7,
                    bits: 16,
                },
                il_offset: 10,
            },
        ]);
    }
    body.extend([
        value(8, Type::I32, Op::Constant(1)),
        value(
            9,
            Type::I32,
            Op::Binary {
                op: Binary::Add,
                left: 3,
                right: 8,
            },
        ),
        Instruction {
            result: None,
            op: Op::StoreLocal { local: 1, value: 9 },
            il_offset: 9,
        },
    ]);
    Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![Type::I32, Type::Native],
        returns: Some(Type::Native),
        locals: vec![Type::Native, Type::I32],
        blocks: vec![
            Block {
                parameters: vec![(0, Type::I32), (1, Type::Native)],
                instructions: vec![
                    value(2, Type::I32, Op::Constant(0)),
                    Instruction {
                        result: None,
                        op: Op::StoreLocal { local: 0, value: 1 },
                        il_offset: 2,
                    },
                    Instruction {
                        result: None,
                        op: Op::StoreLocal { local: 1, value: 2 },
                        il_offset: 2,
                    },
                ],
                terminator: Terminator {
                    kind: Control::Jump(Edge {
                        block: 1,
                        arguments: vec![],
                    }),
                    il_offset: 2,
                },
            },
            Block {
                parameters: vec![],
                instructions: vec![
                    value(3, Type::I32, Op::LoadLocal(1)),
                    value(
                        4,
                        Type::I32,
                        Op::Compare {
                            op: Comparison::LessSigned,
                            left: 3,
                            right: 0,
                        },
                    ),
                ],
                terminator: Terminator {
                    kind: Control::Branch {
                        condition: 4,
                        yes: Edge {
                            block: 2,
                            arguments: vec![],
                        },
                        no: Edge {
                            block: 3,
                            arguments: vec![],
                        },
                    },
                    il_offset: 4,
                },
            },
            Block {
                parameters: vec![],
                instructions: body,
                terminator: Terminator {
                    kind: Control::Jump(Edge {
                        block: 1,
                        arguments: vec![],
                    }),
                    il_offset: 9,
                },
            },
            Block {
                parameters: vec![],
                instructions: vec![value(12, Type::Native, Op::LoadLocal(0))],
                terminator: Terminator {
                    kind: Control::Return(Some(12)),
                    il_offset: 12,
                },
            },
        ],
    }
}
