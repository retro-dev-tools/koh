use koh_compile::{ir::*, resolve::Definition};

fn value(id: u32, ty: Type, op: Op) -> Instruction {
    Instruction {
        result: Some((id, ty)),
        op,
        il_offset: id,
    }
}

pub fn swap(ty: Type) -> Function {
    Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![ty, ty, Type::I32],
        returns: Some(ty),
        locals: vec![],
        blocks: vec![
            Block {
                parameters: vec![(0, ty), (1, ty), (2, Type::I32)],
                instructions: vec![value(3, Type::I32, Op::Constant(0))],
                terminator: Terminator {
                    il_offset: 0,
                    kind: Control::Jump(Edge {
                        block: 1,
                        arguments: vec![0, 1, 3],
                    }),
                },
            },
            Block {
                parameters: vec![(4, ty), (5, ty), (6, Type::I32)],
                instructions: vec![value(
                    7,
                    Type::I32,
                    Op::Compare {
                        op: Comparison::LessUnsigned,
                        left: 6,
                        right: 2,
                    },
                )],
                terminator: Terminator {
                    il_offset: 1,
                    kind: Control::Branch {
                        condition: 7,
                        yes: Edge {
                            block: 2,
                            arguments: vec![],
                        },
                        no: Edge {
                            block: 3,
                            arguments: vec![],
                        },
                    },
                },
            },
            Block {
                parameters: vec![],
                instructions: vec![
                    value(8, Type::I32, Op::Constant(1)),
                    value(
                        9,
                        Type::I32,
                        Op::Binary {
                            op: Binary::Add,
                            left: 6,
                            right: 8,
                        },
                    ),
                ],
                terminator: Terminator {
                    il_offset: 2,
                    kind: Control::Jump(Edge {
                        block: 1,
                        arguments: vec![5, 4, 9],
                    }),
                },
            },
            Block {
                parameters: vec![],
                instructions: vec![],
                terminator: Terminator {
                    il_offset: 3,
                    kind: Control::Return(Some(4)),
                },
            },
        ],
    }
}
