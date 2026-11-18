use koh_compile::{ir::*, resolve::Definition};

pub fn instruction(id: u32, ty: Type, op: Op) -> Instruction {
    Instruction {
        result: Some((id, ty)),
        op,
        il_offset: id,
    }
}

pub fn sum(ty: Type, bits: u8, signed: bool) -> Function {
    let identity = Definition {
        assembly: 0,
        token: 0x06000001,
    };
    Function {
        identity,
        parameters: vec![ty, ty],
        returns: Some(ty),
        locals: vec![],
        blocks: vec![
            Block {
                parameters: vec![(0, ty), (1, ty)],
                instructions: vec![
                    instruction(2, ty, Op::Constant(0)),
                    instruction(
                        3,
                        Type::I32,
                        Op::Compare {
                            op: Comparison::Equal,
                            left: 0,
                            right: 2,
                        },
                    ),
                ],
                terminator: Terminator {
                    kind: Control::Branch {
                        condition: 3,
                        yes: Edge {
                            block: 1,
                            arguments: vec![],
                        },
                        no: Edge {
                            block: 2,
                            arguments: vec![],
                        },
                    },
                    il_offset: 3,
                },
            },
            Block {
                parameters: vec![],
                instructions: vec![instruction(
                    4,
                    ty,
                    Op::Convert {
                        value: 2,
                        bits,
                        signed,
                    },
                )],
                terminator: Terminator {
                    kind: Control::Return(Some(4)),
                    il_offset: 4,
                },
            },
            Block {
                parameters: vec![],
                instructions: vec![
                    instruction(5, ty, Op::Constant(1)),
                    instruction(
                        6,
                        ty,
                        Op::Binary {
                            op: Binary::Subtract,
                            left: 0,
                            right: 5,
                        },
                    ),
                    instruction(
                        7,
                        ty,
                        Op::Call {
                            target: identity,
                            signature: Signature {
                                parameters: vec![ty, ty],
                                returns: Some(ty),
                            },
                            arguments: vec![6, 1],
                        },
                    ),
                    instruction(
                        8,
                        ty,
                        Op::Convert {
                            value: 7,
                            bits,
                            signed,
                        },
                    ),
                    instruction(
                        9,
                        ty,
                        Op::Binary {
                            op: Binary::Add,
                            left: 8,
                            right: 1,
                        },
                    ),
                    instruction(
                        10,
                        ty,
                        Op::Convert {
                            value: 9,
                            bits,
                            signed,
                        },
                    ),
                ],
                terminator: Terminator {
                    kind: Control::Return(Some(10)),
                    il_offset: 10,
                },
            },
        ],
    }
}
