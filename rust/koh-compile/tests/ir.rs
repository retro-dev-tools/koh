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

fn stack_buffer(bytes: u64) -> Function {
    let mut f = diamond();
    f.parameters.clear();
    f.locals.clear();
    f.returns = None;
    f.blocks = vec![Block {
        parameters: vec![],
        instructions: vec![
            instruction(0, Type::I32, Op::Constant(bytes)),
            instruction(1, Type::Native, Op::StackAllocate { bytes: 0 }),
        ],
        terminator: Terminator {
            il_offset: 2,
            kind: Control::Return(None),
        },
    }];
    f
}

#[rstest]
#[case(0)]
#[case(4)]
#[case(65)]
#[case(3840)]
fn fixed_stack_buffers_verify(#[case] bytes: u64) {
    verify(&stack_buffer(bytes)).unwrap();
}

#[rstest]
#[case(3841)]
#[case(4096)]
#[case(0xffff_ffff)]
fn oversized_stack_buffers_are_rejected(#[case] bytes: u64) {
    let error = verify(&stack_buffer(bytes)).unwrap_err();
    assert!(error.contains("IL_0001"), "{error}");
    assert!(error.contains("capacity"), "{error}");
}

#[test]
fn dynamic_stack_buffer_is_rejected() {
    let mut f = stack_buffer(4);
    f.parameters = vec![Type::I32];
    f.blocks[0].parameters = vec![(0, Type::I32)];
    f.blocks[0].instructions.remove(0);
    let error = verify(&f).unwrap_err();
    assert!(error.contains("statically known size"), "{error}");
}

#[test]
fn repeated_stack_allocation_site_is_rejected() {
    let mut f = stack_buffer(4);
    f.blocks.insert(
        0,
        Block {
            parameters: vec![],
            instructions: vec![],
            terminator: Terminator {
                il_offset: 0,
                kind: Control::Jump(Edge {
                    block: 1,
                    arguments: vec![],
                }),
            },
        },
    );
    f.blocks[1].terminator.kind = Control::Jump(Edge {
        block: 1,
        arguments: vec![],
    });
    let error = verify(&f).unwrap_err();
    assert!(error.contains("control-flow cycle"), "{error}");
}

fn initializer(ty: Type, bytes: Vec<u8>) -> Function {
    Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![ty],
        returns: None,
        locals: vec![],
        blocks: vec![Block {
            parameters: vec![(0, ty)],
            instructions: vec![Instruction {
                result: None,
                op: Op::InitializeArray { array: 0, bytes },
                il_offset: 7,
            }],
            terminator: Terminator {
                il_offset: 8,
                kind: Control::Return(None),
            },
        }],
    }
}

#[rstest]
#[case(Type::Null, false)]
#[case(Type::Null, true)]
#[case(Type::FieldHandle(Definition { assembly: 0, token: 0x04000001 }), false)]
#[case(Type::FieldHandle(Definition { assembly: 0, token: 0x04000001 }), true)]
fn metadata_values_cannot_cross_machine_calls(#[case] ty: Type, #[case] parameter: bool) {
    let mut function = initializer(Type::Array(Scalar::U8), vec![1]);
    let source = match ty {
        Type::FieldHandle(field) => Op::FieldHandle(field),
        _ => Op::Constant(0),
    };
    function.blocks[0]
        .instructions
        .insert(0, instruction(1, ty, source));
    function.blocks[0].instructions.insert(
        1,
        Instruction {
            result: if parameter { None } else { Some((2, ty)) },
            op: Op::Call {
                target: Definition {
                    assembly: 0,
                    token: 0x06000002,
                },
                signature: Signature {
                    parameters: if parameter { vec![ty] } else { vec![] },
                    returns: if parameter { None } else { Some(ty) },
                },
                arguments: if parameter { vec![1] } else { vec![] },
            },
            il_offset: 1,
        },
    );
    assert!(verify(&function).unwrap_err().contains("machine call ABI"));
    assert!(koh_compile::sm83::emit(&function).is_err());
}

#[rstest]
#[case(Type::Array(Scalar::U8), 3, true)]
#[case(Type::Array(Scalar::I64), 24, true)]
#[case(Type::Array(Scalar::U8), 0, false)]
#[case(Type::Array(Scalar::U8), 4097, false)]
#[case(Type::Array(Scalar::NativeSigned), 2, false)]
#[case(Type::I32, 3, false)]
fn initializer_ir_requires_bounded_primitive_storage(
    #[case] ty: Type,
    #[case] bytes: usize,
    #[case] valid: bool,
) {
    let mut function = initializer(ty, vec![0x42; bytes]);
    assert_eq!(verify(&function).is_ok(), valid);
    if valid {
        koh_compile::optimize::simplify(&mut function).unwrap();
        verify(&function).unwrap();
    }
}

#[rstest]
#[case(16, true)]
#[case(17, false)]
fn initializer_ir_data_budget_is_bounded(#[case] copies: usize, #[case] valid: bool) {
    let mut function = initializer(Type::Array(Scalar::U8), vec![1; 4096]);
    function.blocks[0].instructions = vec![function.blocks[0].instructions[0].clone(); copies];
    assert_eq!(verify(&function).is_ok(), valid);
}

#[rstest]
#[case(0, false)]
#[case(0x04000000, false)]
#[case(0x06000001, false)]
#[case(0x04000001, true)]
fn field_handles_require_metadata_identity_and_have_no_runtime_abi(
    #[case] token: u32,
    #[case] valid: bool,
) {
    let field = Definition { assembly: 0, token };
    let mut function = initializer(Type::Array(Scalar::U8), vec![1]);
    function.blocks[0].instructions.insert(
        0,
        instruction(1, Type::FieldHandle(field), Op::FieldHandle(field)),
    );
    assert_eq!(verify(&function).is_ok(), valid);
    if valid {
        function.blocks[0].instructions[0].op = Op::Constant(0);
        assert!(verify(&function).unwrap_err().contains("metadata identity"));
        function.blocks[0].instructions[0].op = Op::FieldHandle(field);
        function.returns = Some(Type::FieldHandle(field));
        function.blocks[0].terminator.kind = Control::Return(Some(1));
        assert!(verify(&function).unwrap_err().contains("declared storage"));
    }
}

#[rstest]
#[case(Scalar::Bool)]
#[case(Scalar::Char)]
#[case(Scalar::I8)]
#[case(Scalar::U8)]
#[case(Scalar::I16)]
#[case(Scalar::U16)]
#[case(Scalar::I32)]
#[case(Scalar::U32)]
#[case(Scalar::I64)]
#[case(Scalar::U64)]
#[case(Scalar::NativeSigned)]
#[case(Scalar::NativeUnsigned)]
fn array_operations_preserve_element_identity(#[case] element: Scalar) {
    let mut function = diamond();
    function.parameters.clear();
    function.locals.clear();
    function.returns = Some(Type::ByRef(element));
    function.blocks = vec![Block {
        parameters: vec![],
        instructions: vec![
            instruction(0, Type::I32, Op::Constant(3)),
            instruction(1, Type::Array(element), Op::NewArray { element, length: 0 }),
            instruction(2, Type::Native, Op::ArrayLength(1)),
            instruction(
                3,
                Type::ByRef(element),
                Op::ArrayAddress { array: 1, index: 0 },
            ),
        ],
        terminator: Terminator {
            il_offset: 4,
            kind: Control::Return(Some(3)),
        },
    }];
    verify(&function).unwrap();
    koh_compile::optimize::simplify(&mut function).unwrap();
    function.blocks[0].instructions[3].result = Some((
        3,
        Type::ByRef(if element == Scalar::I32 {
            Scalar::I64
        } else {
            Scalar::I32
        }),
    ));
    assert!(
        verify(&function)
            .unwrap_err()
            .contains("result type mismatch")
    );
}

#[rstest]
#[case(Op::NewArray { element: Scalar::I32, length: 0 }, Type::Array(Scalar::I32))]
#[case(Op::ArrayAddress { array: 0, index: 0 }, Type::ByRef(Scalar::I32))]
#[case(Op::Negate(0), Type::Array(Scalar::I32))]
#[case(Op::Convert { value: 0, bits: 16, signed: false }, Type::Native)]
#[case(Op::Constant(1), Type::Array(Scalar::I32))]
fn invalid_array_ir_is_rejected(#[case] op: Op, #[case] ty: Type) {
    let mut function = diamond();
    function.locals.clear();
    function.parameters = vec![Type::Array(Scalar::I32)];
    function.returns = Some(ty);
    function.blocks = vec![Block {
        parameters: vec![(0, Type::Array(Scalar::I32))],
        instructions: vec![instruction(1, ty, op)],
        terminator: Terminator {
            il_offset: 2,
            kind: Control::Return(Some(1)),
        },
    }];
    assert!(verify(&function).is_err());
}

fn frame_reference() -> Function {
    Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![],
        returns: Some(Type::ByRef(Scalar::I32)),
        locals: vec![Type::I32, Type::ByRef(Scalar::I32)],
        blocks: vec![Block {
            parameters: vec![],
            instructions: vec![
                instruction(0, Type::I32, Op::Constant(0)),
                Instruction {
                    result: None,
                    op: Op::StoreLocal { local: 0, value: 0 },
                    il_offset: 0,
                },
                instruction(1, Type::ByRef(Scalar::I32), Op::Constant(0)),
                Instruction {
                    result: None,
                    op: Op::StoreLocal { local: 1, value: 1 },
                    il_offset: 0,
                },
                instruction(
                    2,
                    Type::ByRef(Scalar::I32),
                    Op::LocalAddress {
                        local: 0,
                        scalar: Scalar::I32,
                    },
                ),
            ],
            terminator: Terminator {
                kind: Control::Return(Some(2)),
                il_offset: 9,
            },
        }],
    }
}

#[test]
fn local_addresses_require_initialized_storage() {
    let mut f = frame_reference();
    f.returns = None;
    f.blocks[0].terminator.kind = Control::Return(None);
    f.blocks[0].instructions.remove(1);
    assert!(
        verify(&f)
            .unwrap_err()
            .contains("initialized compatible primitive storage")
    );
}

#[rstest]
#[case(0)]
#[case(1)]
#[case(2)]
#[case(3)]
fn references_to_the_current_frame_cannot_escape(#[case] shape: u8) {
    let mut f = frame_reference();
    match shape {
        1 => {
            f.blocks[0].instructions.push(Instruction {
                result: None,
                op: Op::StoreLocal { local: 1, value: 2 },
                il_offset: 4,
            });
            f.blocks[0].instructions.push(instruction(
                3,
                Type::ByRef(Scalar::I32),
                Op::LoadLocal(1),
            ));
            f.blocks[0].terminator.kind = Control::Return(Some(3));
        }
        2 => {
            f.blocks[0].instructions.push(instruction(
                3,
                Type::ByRef(Scalar::I32),
                Op::Call {
                    target: Definition {
                        assembly: 0,
                        token: 0x06000002,
                    },
                    signature: Signature {
                        parameters: vec![Type::ByRef(Scalar::I32)],
                        returns: Some(Type::ByRef(Scalar::I32)),
                    },
                    arguments: vec![2],
                },
            ));
            f.blocks[0].terminator.kind = Control::Return(Some(3));
        }
        3 => {
            f.blocks[0].terminator.kind = Control::Jump(Edge {
                block: 1,
                arguments: vec![2],
            });
            f.blocks.push(Block {
                parameters: vec![(3, Type::ByRef(Scalar::I32))],
                instructions: vec![],
                terminator: Terminator {
                    kind: Control::Branch {
                        condition: 0,
                        yes: Edge {
                            block: 1,
                            arguments: vec![3],
                        },
                        no: Edge {
                            block: 2,
                            arguments: vec![3],
                        },
                    },
                    il_offset: 6,
                },
            });
            f.blocks.push(Block {
                parameters: vec![(4, Type::ByRef(Scalar::I32))],
                instructions: vec![],
                terminator: Terminator {
                    kind: Control::Return(Some(4)),
                    il_offset: 7,
                },
            });
        }
        _ => {}
    }
    let error = verify(&f).unwrap_err();
    assert!(
        error.contains("current frame may escape") && error.contains("IL_"),
        "{error}"
    );
}

#[rstest]
#[case(0, Scalar::I32, Type::ByRef(Scalar::I32), true)]
#[case(0, Scalar::U8, Type::ByRef(Scalar::U8), true)]
#[case(0, Scalar::I64, Type::ByRef(Scalar::I64), false)]
#[case(2, Scalar::I32, Type::ByRef(Scalar::I32), false)]
#[case(0, Scalar::I32, Type::Native, false)]
#[case(0, Scalar::I32, Type::ByRef(Scalar::U32), false)]
fn local_addresses_validate_index_and_storage(
    #[case] local: u32,
    #[case] scalar: Scalar,
    #[case] result: Type,
    #[case] valid: bool,
) {
    let mut f = frame_reference();
    f.returns = None;
    f.blocks[0].terminator.kind = Control::Return(None);
    f.blocks[0].instructions.last_mut().unwrap().op = Op::LocalAddress { local, scalar };
    f.blocks[0].instructions.last_mut().unwrap().result = Some((2, result));
    assert_eq!(verify(&f).is_ok(), valid);
}

#[rstest]
#[case(0x06000001, Type::Native, true)]
#[case(0x06000000, Type::Native, false)]
#[case(0x04000001, Type::Native, false)]
#[case(0x06000001, Type::I32, false)]
#[case(0x06000001, Type::ByRef(Scalar::U8), false)]
fn initializer_state_addresses_are_native_method_storage(
    #[case] token: u32,
    #[case] ty: Type,
    #[case] valid: bool,
) {
    let mut f = static_address(0x04000001, 1, ty);
    f.blocks[0].instructions[0].op = Op::InitializerAddress(Definition { assembly: 0, token });
    assert_eq!(verify(&f).is_ok(), valid);
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
