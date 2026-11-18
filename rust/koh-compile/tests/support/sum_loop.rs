use koh_compile::{ir::*, resolve::Definition};

fn instruction(id: u32, ty: Type, op: Op) -> Instruction {
    Instruction {
        result: Some((id, ty)),
        op,
        il_offset: id,
    }
}

pub fn sum(ty: Type) -> Function {
    Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![ty],
        returns: Some(ty),
        locals: vec![],
        blocks: vec![
            Block {
                parameters: vec![(0, ty)],
                instructions: vec![
                    instruction(1, ty, Op::Constant(0)),
                    instruction(2, ty, Op::Constant(1)),
                ],
                terminator: Terminator {
                    il_offset: 0,
                    kind: Control::Jump(Edge {
                        block: 1,
                        arguments: vec![1, 1],
                    }),
                },
            },
            Block {
                parameters: vec![(3, ty), (4, ty)],
                instructions: vec![instruction(
                    5,
                    Type::I32,
                    Op::Compare {
                        op: Comparison::LessUnsigned,
                        left: 3,
                        right: 0,
                    },
                )],
                terminator: Terminator {
                    il_offset: 1,
                    kind: Control::Branch {
                        condition: 5,
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
                    instruction(
                        6,
                        ty,
                        Op::Binary {
                            op: Binary::Add,
                            left: 4,
                            right: 3,
                        },
                    ),
                    instruction(
                        7,
                        ty,
                        Op::Binary {
                            op: Binary::Add,
                            left: 3,
                            right: 2,
                        },
                    ),
                ],
                terminator: Terminator {
                    il_offset: 2,
                    kind: Control::Jump(Edge {
                        block: 1,
                        arguments: vec![7, 6],
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
