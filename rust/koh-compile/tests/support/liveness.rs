use koh_compile::{ir::*, resolve::Definition};

#[path = "sum_loop.rs"]
mod sum_loop;

fn instruction(id: u32, ty: Type, op: Op) -> Instruction {
    Instruction {
        result: Some((id, ty)),
        op,
        il_offset: id,
    }
}

pub fn entry(ty: Type, constant: u64) -> Function {
    Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![ty],
        returns: Some(ty),
        locals: vec![],
        blocks: vec![Block {
            parameters: vec![(0, ty)],
            instructions: vec![
                instruction(1, ty, Op::Constant(constant)),
                instruction(
                    2,
                    ty,
                    Op::Binary {
                        op: Binary::Add,
                        left: 0,
                        right: 1,
                    },
                ),
                instruction(
                    3,
                    ty,
                    Op::Binary {
                        op: Binary::Add,
                        left: 2,
                        right: 0,
                    },
                ),
            ],
            terminator: Terminator {
                il_offset: 3,
                kind: Control::Return(Some(3)),
            },
        }],
    }
}

pub fn branch(ty: Type) -> Function {
    Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![Type::I32, ty],
        returns: Some(ty),
        locals: vec![],
        blocks: vec![
            Block {
                parameters: vec![(0, Type::I32), (1, ty)],
                instructions: vec![
                    instruction(2, ty, Op::Constant(1)),
                    instruction(
                        3,
                        ty,
                        Op::Binary {
                            op: Binary::Add,
                            left: 1,
                            right: 2,
                        },
                    ),
                ],
                terminator: Terminator {
                    il_offset: 0,
                    kind: Control::Branch {
                        condition: 0,
                        yes: Edge {
                            block: 1,
                            arguments: vec![],
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
                instructions: vec![instruction(
                    4,
                    ty,
                    Op::Binary {
                        op: Binary::Add,
                        left: 3,
                        right: 2,
                    },
                )],
                terminator: Terminator {
                    il_offset: 1,
                    kind: Control::Jump(Edge {
                        block: 3,
                        arguments: vec![4],
                    }),
                },
            },
            Block {
                parameters: vec![],
                instructions: vec![
                    instruction(5, ty, Op::Constant(2)),
                    instruction(
                        6,
                        ty,
                        Op::Binary {
                            op: Binary::Add,
                            left: 3,
                            right: 5,
                        },
                    ),
                ],
                terminator: Terminator {
                    il_offset: 2,
                    kind: Control::Jump(Edge {
                        block: 3,
                        arguments: vec![6],
                    }),
                },
            },
            Block {
                parameters: vec![(7, ty)],
                instructions: vec![],
                terminator: Terminator {
                    il_offset: 3,
                    kind: Control::Return(Some(7)),
                },
            },
        ],
    }
}

pub fn loop_count(ty: Type) -> Function {
    let mut function = sum_loop::sum(ty);
    if let Op::Compare { op, .. } = &mut function.blocks[1].instructions[0].op {
        *op = Comparison::LessSigned;
    }
    function.blocks[3].terminator.kind = Control::Return(Some(3));
    function
}
