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

fn static_address(token: u32, bytes: u8, ty: Type) -> Function {
    let mut f = diamond();
    f.parameters.clear();
    f.locals.clear();
    f.returns = Some(ty);
    f.blocks.truncate(1);
    f.blocks[0].parameters.clear();
    f.blocks[0].instructions = vec![instruction(
        0,
        ty,
        Op::StaticAddress {
            field: Definition { assembly: 0, token },
            bytes,
        },
    )];
    f.blocks[0].terminator.kind = Control::Return(Some(0));
    f
}

#[rstest]
#[case(0x04000001, 1, Type::Native, true)]
#[case(0x04000001, 2, Type::Native, true)]
#[case(0x04000001, 4, Type::Native, true)]
#[case(0x04000001, 8, Type::Native, true)]
#[case(0x04000001, 0, Type::Native, false)]
#[case(0x04000001, 3, Type::Native, false)]
#[case(0x04000000, 1, Type::Native, false)]
#[case(0x06000001, 1, Type::Native, false)]
#[case(0x04000001, 1, Type::I32, false)]
#[case(0x04000001, 1, Type::ByRef(Scalar::U8), true)]
#[case(0x04000001, 1, Type::ByRef(Scalar::I32), false)]
fn static_addresses_require_valid_identity_width_and_result(
    #[case] token: u32,
    #[case] bytes: u8,
    #[case] ty: Type,
    #[case] valid: bool,
) {
    assert_eq!(verify(&static_address(token, bytes, ty)).is_ok(), valid);
}

#[test]
fn static_field_widths_must_agree_across_functions() {
    let first = static_address(0x04000001, 1, Type::Native);
    let mut second = static_address(0x04000001, 2, Type::Native);
    second.identity.token += 1;
    verify(&first).unwrap();
    verify(&second).unwrap();
    assert!(
        verify_program(&[first.clone(), second])
            .unwrap_err()
            .contains("Conflicting static field widths")
    );
    let mut second = first.clone();
    second.identity.token += 1;
    verify_program(&[first, second]).unwrap();
}

#[rstest]
#[case(Scalar::U8, true)]
#[case(Scalar::I8, false)]
#[case(Scalar::I32, false)]
#[case(Scalar::U32, false)]
fn managed_reference_calls_preserve_the_storage_type(
    #[case] parameter: Scalar,
    #[case] valid: bool,
) {
    let mut caller = static_address(0x04000001, 1, Type::ByRef(Scalar::U8));
    caller.returns = None;
    caller.blocks[0].terminator.kind = Control::Return(None);
    let identity = Definition {
        assembly: 0,
        token: 0x06000002,
    };
    caller.blocks[0].instructions.push(Instruction {
        result: None,
        op: Op::Call {
            target: identity,
            signature: Signature {
                parameters: vec![Type::ByRef(parameter)],
                returns: None,
            },
            arguments: vec![0],
        },
        il_offset: 1,
    });
    let callee = Function {
        identity,
        parameters: vec![Type::ByRef(parameter)],
        returns: None,
        locals: vec![],
        blocks: vec![Block {
            parameters: vec![(0, Type::ByRef(parameter))],
            instructions: vec![],
            terminator: Terminator {
                kind: Control::Return(None),
                il_offset: 0,
            },
        }],
    };
    assert_eq!(verify_program(&[caller, callee]).is_ok(), valid);
}

#[rstest]
#[case(Type::Native, Type::I32, 8, true)]
#[case(Type::Native, Type::I32, 16, true)]
#[case(Type::Native, Type::I32, 32, true)]
#[case(Type::Native, Type::I64, 64, true)]
#[case(Type::Native, Type::Native, 16, true)]
#[case(Type::I32, Type::I32, 8, false)]
#[case(Type::Native, Type::I32, 24, false)]
#[case(Type::Native, Type::I32, 64, false)]
#[case(Type::Native, Type::I64, 32, false)]
#[case(Type::Native, Type::Native, 8, false)]
#[case(Type::ByRef(Scalar::U8), Type::I32, 8, true)]
#[case(Type::ByRef(Scalar::I8), Type::I32, 8, true)]
#[case(Type::ByRef(Scalar::I32), Type::I32, 32, true)]
#[case(Type::ByRef(Scalar::I32), Type::I32, 8, false)]
#[case(Type::ByRef(Scalar::U8), Type::I32, 32, false)]
fn memory_widths_are_verified(
    #[case] address: Type,
    #[case] value: Type,
    #[case] bits: u8,
    #[case] valid: bool,
) {
    let mut f = diamond();
    f.parameters = vec![address, value];
    f.returns = Some(value);
    f.locals.clear();
    f.blocks.truncate(1);
    f.blocks[0].parameters = vec![(0, address), (1, value)];
    f.blocks[0].instructions = vec![
        Instruction {
            result: None,
            op: Op::StoreMemory {
                address: 0,
                value: 1,
                bits,
            },
            il_offset: 0,
        },
        instruction(
            2,
            value,
            Op::LoadMemory {
                address: 0,
                bits,
                signed: false,
            },
        ),
    ];
    f.blocks[0].terminator.kind = Control::Return(Some(2));
    assert_eq!(verify(&f).is_ok(), valid);
    f.blocks[0].instructions.remove(0);
    assert_eq!(verify(&f).is_ok(), valid);
}

fn recursive_call() -> Function {
    let mut f = diamond();
    f.blocks.truncate(1);
    f.blocks[0].instructions = vec![instruction(
        1,
        Type::I32,
        Op::Call {
            target: f.identity,
            arguments: vec![0],
            signature: Signature {
                parameters: vec![Type::I32],
                returns: Some(Type::I32),
            },
        },
    )];
    f.blocks[0].terminator.kind = Control::Return(Some(1));
    f
}

#[test]
fn recursive_calls_have_explicit_typed_signatures() {
    verify_program(&[recursive_call()]).unwrap();
}

#[rstest]
#[case("count")]
#[case("type")]
#[case("target")]
#[case("signature")]
fn invalid_call_contracts_are_rejected(#[case] fault: &str) {
    let mut f = recursive_call();
    let mut other = recursive_call();
    other.identity.token += 1;
    if let Op::Call {
        target,
        arguments,
        signature,
    } = &mut f.blocks[0].instructions[0].op
    {
        match fault {
            "count" => arguments.clear(),
            "type" => signature.parameters[0] = Type::I64,
            "target" => target.token += 2,
            "signature" => {
                target.token += 1;
                other.returns = None;
                other.blocks[0].instructions.clear();
                other.blocks[0].terminator.kind = Control::Return(None);
            }
            _ => unreachable!(),
        }
    }
    assert!(verify_program(&[f, other]).is_err());
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
