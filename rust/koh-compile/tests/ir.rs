use koh_compile::ir::*;
use koh_compile::resolve::Definition;
use rstest::rstest;

fn instruction(id: u32, ty: Type, op: Op) -> Instruction {
    Instruction {
        result: Some((id, ty)),
        op,
        il_offset: id,
    }
}

fn diamond() -> Function {
    Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![Type::I32],
        returns: Some(Type::I32),
        locals: vec![Type::I32],
        blocks: vec![
            Block {
                parameters: vec![(0, Type::I32)],
                instructions: vec![],
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
                instructions: vec![instruction(1, Type::I32, Op::Constant(7))],
                terminator: Terminator {
                    il_offset: 1,
                    kind: Control::Jump(Edge {
                        block: 3,
                        arguments: vec![1],
                    }),
                },
            },
            Block {
                parameters: vec![],
                instructions: vec![instruction(2, Type::I32, Op::Constant(9))],
                terminator: Terminator {
                    il_offset: 2,
                    kind: Control::Jump(Edge {
                        block: 3,
                        arguments: vec![2],
                    }),
                },
            },
            Block {
                parameters: vec![(3, Type::I32)],
                instructions: vec![],
                terminator: Terminator {
                    il_offset: 3,
                    kind: Control::Return(Some(3)),
                },
            },
        ],
    }
}

#[test]
fn typed_join_is_valid() {
    verify(&diamond()).unwrap();
}

#[test]
fn loop_block_arguments_are_simultaneous_phi_values() {
    let mut f = diamond();
    f.blocks[3].terminator.kind = Control::Branch {
        condition: 3,
        yes: Edge {
            block: 3,
            arguments: vec![3],
        },
        no: Edge {
            block: 4,
            arguments: vec![3],
        },
    };
    f.blocks.push(Block {
        parameters: vec![(4, Type::I32)],
        instructions: vec![],
        terminator: Terminator {
            il_offset: 4,
            kind: Control::Return(Some(4)),
        },
    });
    verify(&f).unwrap();
}

#[rstest]
#[case("missing-block")]
#[case("duplicate-value")]
#[case("undefined-value")]
#[case("wrong-edge-count")]
#[case("wrong-edge-type")]
#[case("nondominating-value")]
#[case("forward-use")]
#[case("wrong-return")]
#[case("entry-edge")]
#[case("unreachable")]
#[case("wrong-entry")]
#[case("constant-width")]
#[case("bad-local")]
#[case("missing-result")]
#[case("uninitialized-local")]
fn malformed_ir_is_diagnostic(#[case] fault: &str) {
    let mut f = diamond();
    match fault {
        "missing-block" => {
            f.blocks[1].terminator.kind = Control::Jump(Edge {
                block: 99,
                arguments: vec![],
            })
        }
        "duplicate-value" => f.blocks[2].instructions[0].result = Some((1, Type::I32)),
        "undefined-value" => f.blocks[3].terminator.kind = Control::Return(Some(99)),
        "wrong-edge-count" => {
            f.blocks[1].terminator.kind = Control::Jump(Edge {
                block: 3,
                arguments: vec![],
            })
        }
        "wrong-edge-type" => f.blocks[1].instructions[0].result = Some((1, Type::I64)),
        "nondominating-value" => f.blocks[3].terminator.kind = Control::Return(Some(1)),
        "forward-use" => {
            f.blocks[1].instructions.insert(
                0,
                instruction(
                    5,
                    Type::I32,
                    Op::Binary {
                        op: Binary::Add,
                        left: 1,
                        right: 0,
                    },
                ),
            );
        }
        "wrong-return" => f.returns = None,
        "entry-edge" => {
            f.blocks[1].terminator.kind = Control::Jump(Edge {
                block: 0,
                arguments: vec![1],
            })
        }
        "unreachable" => f.blocks.push(Block {
            parameters: vec![],
            instructions: vec![],
            terminator: Terminator {
                il_offset: 42,
                kind: Control::Return(Some(0)),
            },
        }),
        "wrong-entry" => f.parameters = vec![Type::I64],
        "constant-width" => f.blocks[1].instructions[0].op = Op::Constant(u64::MAX),
        "bad-local" => f.blocks[1].instructions[0].op = Op::LoadLocal(1),
        "missing-result" => f.blocks[1].instructions[0].result = None,
        "uninitialized-local" => f.blocks[1].instructions[0].op = Op::LoadLocal(0),
        _ => unreachable!(),
    }
    let error = verify(&f).unwrap_err();
    assert!(error.contains("06000001"), "{fault}: {error}");
}

#[rstest]
#[case(Type::I32)]
#[case(Type::I64)]
#[case(Type::Native)]
fn local_storage_and_integer_operations_are_typed(#[case] ty: Type) {
    let mut f = diamond();
    f.parameters = vec![ty];
    f.returns = Some(ty);
    f.locals = vec![ty];
    f.blocks = vec![Block {
        parameters: vec![(0, ty)],
        instructions: vec![
            Instruction {
                result: None,
                op: Op::StoreLocal { local: 0, value: 0 },
                il_offset: 1,
            },
            instruction(1, ty, Op::LoadLocal(0)),
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
                Type::I32,
                Op::Compare {
                    op: Comparison::LessUnsigned,
                    left: 1,
                    right: 2,
                },
            ),
            instruction(
                4,
                ty,
                Op::Binary {
                    op: Binary::ShiftRightSigned,
                    left: 2,
                    right: 3,
                },
            ),
        ],
        terminator: Terminator {
            il_offset: 5,
            kind: Control::Return(Some(4)),
        },
    }];
    verify(&f).unwrap();
    f.blocks[0].instructions[3].result = Some((3, Type::I64));
    assert!(verify(&f).is_err());
}
