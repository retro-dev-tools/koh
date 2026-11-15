use koh_compile::ir::*;
use koh_compile::resolve::Definition;
use rstest::rstest;

#[rstest]
#[case(CpuControl::DisableInterrupts)]
#[case(CpuControl::EnableInterrupts)]
#[case(CpuControl::Halt)]
#[case(CpuControl::Nop)]
#[case(CpuControl::Stop)]
fn cpu_control_has_no_result_and_survives_optimization(#[case] control: CpuControl) {
    let mut f = diamond();
    f.parameters.clear();
    f.locals.clear();
    f.returns = None;
    f.blocks = vec![Block {
        parameters: vec![],
        instructions: vec![Instruction {
            result: None,
            op: Op::CpuControl(control),
            il_offset: 0,
        }],
        terminator: Terminator {
            il_offset: 1,
            kind: Control::Return(None),
        },
    }];
    koh_compile::optimize::simplify(&mut f).unwrap();
    assert_eq!(f.blocks[0].instructions.len(), 1);
    koh_compile::sm83::emit(&f).unwrap();
    f.blocks[0].instructions[0].result = Some((0, Type::I32));
    assert!(verify(&f).unwrap_err().contains("result type mismatch"));
}

#[test]
fn cpu_control_emits_ordered_sm83_encodings() {
    let mut f = diamond();
    f.parameters.clear();
    f.locals.clear();
    f.returns = None;
    f.blocks = vec![Block {
        parameters: vec![],
        instructions: [
            CpuControl::DisableInterrupts,
            CpuControl::EnableInterrupts,
            CpuControl::Nop,
            CpuControl::Halt,
            CpuControl::Stop,
        ]
        .into_iter()
        .enumerate()
        .map(|(i, control)| Instruction {
            result: None,
            op: Op::CpuControl(control),
            il_offset: i as u32,
        })
        .collect(),
        terminator: Terminator {
            il_offset: 5,
            kind: Control::Return(None),
        },
    }];
    koh_compile::optimize::simplify(&mut f).unwrap();
    let object = koh_compile::sm83::emit(&f).unwrap();
    assert!(object.sections.iter().any(|s| {
        s.data
            .windows(6)
            .any(|bytes| bytes == [0xf3, 0xfb, 0, 0x76, 0x10, 0])
    }));
}

#[rstest]
#[case(Type::I32, true)]
#[case(Type::I64, false)]
#[case(Type::Native, false)]
#[case(Type::String, false)]
#[case(Type::Array(Storage::Scalar(Scalar::U8)), false)]
fn oam_dma_is_typed_and_observable(#[case] ty: Type, #[case] valid: bool) {
    let mut f = diamond();
    f.parameters = vec![ty];
    f.locals.clear();
    f.returns = Some(Type::I32);
    f.blocks = vec![Block {
        parameters: vec![(0, ty)],
        instructions: vec![
            Instruction {
                result: None,
                op: Op::OamDma(0),
                il_offset: 0,
            },
            instruction(1, Type::I32, Op::Constant(42)),
        ],
        terminator: Terminator {
            il_offset: 1,
            kind: Control::Return(Some(1)),
        },
    }];
    if !valid {
        assert!(
            verify(&f)
                .unwrap_err()
                .contains("OAM DMA page requires int32")
        );
        return;
    }
    koh_compile::optimize::simplify(&mut f).unwrap();
    assert!(
        f.blocks[0]
            .instructions
            .iter()
            .any(|i| matches!(i.op, Op::OamDma(0)))
    );
    koh_compile::sm83::emit(&f).unwrap();
    f.blocks[0].instructions[0].result = Some((2, Type::I32));
    assert!(verify(&f).unwrap_err().contains("result type mismatch"));
}

#[rstest]
#[case(0)]
#[case(1)]
#[case(2)]
#[case(3)]
fn local_forwarding_preserves_effects_and_traps(#[case] barrier: u8) {
    let mut f = diamond();
    f.parameters = vec![Type::Native];
    f.locals = vec![Type::I32];
    f.returns = Some(Type::I32);
    let mut instructions = vec![
        instruction(1, Type::I32, Op::Constant(7)),
        Instruction {
            result: None,
            op: Op::StoreLocal { local: 0, value: 1 },
            il_offset: 0,
        },
        instruction(2, Type::I32, Op::LoadLocal(0)),
        instruction(
            8,
            Type::ByRef(Scalar::I32),
            Op::LocalAddress {
                local: 0,
                storage: Storage::Scalar(Scalar::I32),
            },
        ),
    ];
    match barrier {
        0 => instructions.push(Instruction {
            result: None,
            op: Op::StoreMemory {
                address: 8,
                value: 2,
                bits: 32,
            },
            il_offset: 1,
        }),
        1 => instructions.push(Instruction {
            result: None,
            op: Op::Call {
                target: Definition {
                    assembly: 0,
                    token: 0x06000002,
                },
                signature: Signature {
                    parameters: vec![Type::ByRef(Scalar::I32)],
                    returns: None,
                },
                arguments: vec![8],
            },
            il_offset: 1,
        }),
        2 => instructions.extend([
            instruction(6, Type::I32, Op::Constant(0)),
            instruction(
                7,
                Type::I32,
                Op::Binary {
                    op: Binary::DivideSigned,
                    left: 2,
                    right: 6,
                },
            ),
        ]),
        3 => instructions.push(instruction(
            6,
            Type::Array(Storage::Scalar(Scalar::U8)),
            Op::NewArray {
                element: Storage::Scalar(Scalar::U8),
                length: 2,
                alignment: 1,
            },
        )),
        _ => unreachable!(),
    }
    instructions.extend([
        instruction(3, Type::I32, Op::LoadLocal(0)),
        instruction(4, Type::I32, Op::Constant(7)),
        instruction(
            5,
            Type::I32,
            Op::Binary {
                op: Binary::Add,
                left: 3,
                right: 4,
            },
        ),
    ]);
    f.blocks = vec![Block {
        parameters: vec![(0, Type::Native)],
        instructions,
        terminator: Terminator {
            il_offset: 2,
            kind: Control::Return(Some(5)),
        },
    }];
    verify(&f).unwrap();
    koh_compile::optimize::simplify(&mut f).unwrap();
    let instructions = &f.blocks[0].instructions;
    assert!(
        !instructions
            .iter()
            .any(|i| i.result.is_some_and(|(id, _)| id == 2 || id == 4))
    );
    assert!(
        instructions
            .iter()
            .any(|i| i.result == Some((3, Type::I32)))
    );
    assert!(instructions.iter().any(|i| match barrier {
        0 => matches!(i.op, Op::StoreMemory { .. }),
        1 => matches!(i.op, Op::Call { .. }),
        2 => matches!(
            i.op,
            Op::Binary {
                op: Binary::DivideSigned,
                ..
            }
        ),
        _ => matches!(i.op, Op::NewArray { .. }),
    }));
    let once = f.clone();
    koh_compile::optimize::simplify(&mut f).unwrap();
    assert_eq!(f.blocks, once.blocks);
    assert_eq!(f.locals, once.locals);
}

#[rstest]
#[case(0)]
#[case(1)]
#[case(2)]
#[case(3)]
#[case(4)]
#[case(5)]
#[case(6)]
#[case(7)]
#[case(8)]
#[case(9)]
fn string_ir_preserves_reference_and_index_types(#[case] mutation: u8) {
    let mut f = diamond();
    f.locals.clear();
    f.parameters = vec![Type::String, Type::I32];
    f.returns = Some(Type::I32);
    f.blocks = vec![Block {
        parameters: vec![(0, Type::String), (1, Type::I32)],
        instructions: vec![instruction(
            2,
            Type::I32,
            Op::StringChar {
                string: 0,
                index: 1,
            },
        )],
        terminator: Terminator {
            il_offset: 1,
            kind: Control::Return(Some(2)),
        },
    }];
    verify(&f).unwrap();
    koh_compile::optimize::simplify(&mut f).unwrap();
    let i = &mut f.blocks[0].instructions[0];
    match mutation {
        0 => i.op = Op::ArrayLength(0),
        1 => i.op = Op::StringLength(1),
        2 => {
            i.op = Op::StringChar {
                string: 0,
                index: 0,
            }
        }
        3 => i.result = Some((2, Type::Native)),
        4 => {
            i.op = Op::Convert {
                value: 0,
                bits: 16,
                signed: false,
            }
        }
        5 => {
            i.op = Op::Constant(1);
            i.result = Some((2, Type::String));
        }
        6 => {
            i.op = Op::StringLiteral(vec![0; 8192]);
            i.result = Some((2, Type::String));
        }
        7 => {
            i.op = Op::LoadMemory {
                address: 0,
                bits: 16,
                signed: false,
            }
        }
        8 => {
            i.op = Op::Compare {
                op: Comparison::LessSigned,
                left: 0,
                right: 0,
            }
        }
        9 => {
            i.op = Op::Compare {
                op: Comparison::GreaterUnsigned,
                left: 0,
                right: 0,
            }
        }
        _ => unreachable!(),
    }
    f.returns = i.result.map(|(_, ty)| ty);
    let expected = [
        "Array length requires",
        "String length requires",
        "String indexing requires",
        "result type mismatch",
        "Managed reference conversion",
        "Nonzero managed reference",
        "String literal exceeds",
        "Memory address must",
        "String references support",
        "String references support",
    ];
    let error = verify(&f).unwrap_err();
    assert!(error.contains(expected[mutation as usize]), "{error}");
}

#[rstest]
#[case(vec![])]
#[case(vec![0, 0xd800, 0xdc00])]
#[case(vec![0; 8191])]
fn string_literals_preserve_units_through_optimization(#[case] units: Vec<u16>) {
    let mut f = diamond();
    f.locals.clear();
    f.parameters.clear();
    f.returns = Some(Type::String);
    f.blocks = vec![Block {
        parameters: vec![],
        instructions: vec![instruction(
            0,
            Type::String,
            Op::StringLiteral(units.clone()),
        )],
        terminator: Terminator {
            il_offset: 1,
            kind: Control::Return(Some(0)),
        },
    }];
    verify(&f).unwrap();
    koh_compile::optimize::simplify(&mut f).unwrap();
    assert_eq!(f.blocks[0].instructions[0].op, Op::StringLiteral(units));
    koh_compile::sm83::emit(&f).unwrap();
}

#[rstest]
#[case(1)]
#[case(16)]
#[case(64)]
#[case(4096)]
fn aggregate_widths_preserve_full_storage(#[case] bytes: u16) {
    let identity = Definition {
        assembly: 0,
        token: 0x02000001,
    };
    let value = ValueType::new(identity, bytes).unwrap();
    assert_eq!((value.identity(), value.bytes()), (identity, bytes));
    assert_eq!(Type::Struct(value).bits(), bytes * 8);
    assert_eq!(Type::StructRef(value).bits(), 16);
    let mut f = static_address(0x04000001, 2, Type::Native);
    f.returns = Some(Type::Struct(value));
    f.blocks[0].instructions[0] = instruction(0, Type::Struct(value), Op::Constant(0));
    assert!(verify(&f).unwrap_err().contains("Aggregate constants"));
}

#[rstest]
#[case(0x02000001, 0)]
#[case(0x02000001, 4097)]
#[case(0x02000001, u16::MAX)]
#[case(0x02000000, 1)]
#[case(0x01000001, 1)]
#[case(0x04000001, 1)]
fn invalid_aggregate_descriptors_diagnose(#[case] token: u32, #[case] bytes: u16) {
    assert!(ValueType::new(Definition { assembly: 0, token }, bytes).is_err());
}

#[rstest]
#[case(false, false)]
#[case(true, false)]
#[case(false, true)]
#[case(true, true)]
fn aggregate_identity_requires_consistent_storage(
    #[case] different_assembly: bool,
    #[case] array: bool,
) {
    let representation = |value| {
        if array {
            Type::Array(Storage::Struct(value))
        } else {
            Type::Struct(value)
        }
    };
    let mut f = diamond();
    let ty = representation(
        ValueType::new(
            Definition {
                assembly: 0,
                token: 0x02000001,
            },
            64,
        )
        .unwrap(),
    );
    f.parameters = vec![ty];
    f.locals.clear();
    f.returns = None;
    f.blocks = vec![Block {
        parameters: vec![(0, ty)],
        instructions: vec![],
        terminator: Terminator {
            kind: Control::Return(None),
            il_offset: 0,
        },
    }];
    verify(&f).unwrap();
    let mut other = f.clone();
    other.identity.token += 1;
    let ty = representation(
        ValueType::new(
            Definition {
                assembly: usize::from(different_assembly),
                token: 0x02000001,
            },
            128,
        )
        .unwrap(),
    );
    other.parameters[0] = ty;
    other.blocks[0].parameters[0].1 = ty;
    assert_eq!(verify_program(&[f, other]).is_ok(), different_assembly);
}

#[rstest]
#[case(3)]
#[case(5)]
#[case(64)]
#[case(4096)]
fn aggregate_array_operations_preserve_identity(#[case] bytes: u16) {
    let shape = ValueType::new(
        Definition {
            assembly: 0,
            token: 0x02000001,
        },
        bytes,
    )
    .unwrap();
    let other = ValueType::new(
        Definition {
            assembly: 0,
            token: 0x02000002,
        },
        bytes,
    )
    .unwrap();
    let mut f = diamond();
    f.parameters.clear();
    f.locals.clear();
    f.returns = Some(Type::StructRef(shape));
    f.blocks = vec![Block {
        parameters: vec![],
        instructions: vec![
            instruction(0, Type::I32, Op::Constant(1)),
            instruction(
                1,
                Type::Array(Storage::Struct(shape)),
                Op::NewArray {
                    element: Storage::Struct(shape),
                    length: 0,
                    alignment: 1,
                },
            ),
            instruction(
                2,
                Type::StructRef(shape),
                Op::ArrayAddress { array: 1, index: 0 },
            ),
        ],
        terminator: Terminator {
            kind: Control::Return(Some(2)),
            il_offset: 3,
        },
    }];
    verify(&f).unwrap();
    koh_compile::optimize::simplify(&mut f).unwrap();
    let valid = f.clone();
    f.returns = Some(Type::StructRef(other));
    f.blocks[0].instructions[2].result = Some((2, Type::StructRef(other)));
    assert!(verify(&f).unwrap_err().contains("result type mismatch"));
    f = valid;
    let Op::NewArray { element, .. } = &mut f.blocks[0].instructions[1].op else {
        unreachable!()
    };
    *element = Storage::Struct(other);
    assert!(verify(&f).unwrap_err().contains("result type mismatch"));
}

#[test]
fn aggregate_rva_initialization_requires_an_explicit_contract() {
    let shape = ValueType::new(
        Definition {
            assembly: 0,
            token: 0x02000001,
        },
        3,
    )
    .unwrap();
    assert!(
        verify(&initializer(
            Type::Array(Storage::Struct(shape)),
            vec![1, 2, 3]
        ))
        .unwrap_err()
        .contains("primitive array initializer")
    );
}

#[rstest]
#[case(false)]
#[case(true)]
fn pinned_struct_references_keep_storage_identity(#[case] different: bool) {
    let shape = ValueType::new(
        Definition {
            assembly: 0,
            token: 0x02000001,
        },
        3,
    )
    .unwrap();
    let argument = ValueType::new(
        Definition {
            assembly: 0,
            token: 0x02000001 + u32::from(different),
        },
        3,
    )
    .unwrap();
    let mut f = pinned_reference();
    f.locals[0] = Type::StructRef(shape);
    f.parameters[0] = Type::StructRef(argument);
    f.blocks[0].parameters[0].1 = Type::StructRef(argument);
    f.blocks[0].instructions[0].result.as_mut().unwrap().1 = Type::StructRef(shape);
    if different {
        assert!(
            verify(&f)
                .unwrap_err()
                .contains("Pinned store type mismatch")
        );
    } else {
        verify(&f).unwrap();
        f.blocks[0].instructions.extend([
            instruction(2, Type::Native, Op::Constant(0)),
            Instruction {
                result: None,
                op: Op::StorePinned { local: 0, value: 2 },
                il_offset: 4,
            },
        ]);
        koh_compile::optimize::simplify(&mut f).unwrap();
    }
}

fn aggregate_field(native: bool, nested: bool, offset: u16) -> Function {
    let owner = ValueType::new(
        Definition {
            assembly: 0,
            token: 0x02000001,
        },
        64,
    )
    .unwrap();
    let storage = if nested {
        Storage::Struct(
            ValueType::new(
                Definition {
                    assembly: 0,
                    token: 0x02000002,
                },
                8,
            )
            .unwrap(),
        )
    } else {
        Storage::Scalar(Scalar::U8)
    };
    let input = if native {
        Type::Native
    } else {
        Type::StructRef(owner)
    };
    let output = if native {
        Type::Native
    } else {
        storage.reference_type()
    };
    let mut f = diamond();
    f.parameters = vec![input];
    f.locals.clear();
    f.returns = Some(output);
    f.blocks = vec![Block {
        parameters: vec![(0, input)],
        instructions: vec![instruction(
            1,
            output,
            Op::FieldAddress {
                address: 0,
                owner,
                offset,
                storage,
            },
        )],
        terminator: Terminator {
            kind: Control::Return(Some(1)),
            il_offset: 2,
        },
    }];
    f
}

#[rstest]
#[case(false, false)]
#[case(false, true)]
#[case(true, false)]
#[case(true, true)]
fn field_references_preserve_native_bank_provenance(#[case] native: bool, #[case] nested: bool) {
    let mut f = aggregate_field(native, nested, 8);
    verify(&f).unwrap();
    let changed = if native {
        Type::ByRef(Scalar::U8)
    } else {
        Type::Native
    };
    f.returns = Some(changed);
    f.blocks[0].instructions[0].result.as_mut().unwrap().1 = changed;
    assert!(verify(&f).is_err());
}

#[rstest]
#[case(56, true, true)]
#[case(57, true, false)]
#[case(63, false, true)]
#[case(64, false, false)]
#[case(u16::MAX, true, false)]
fn aggregate_field_extents_are_checked(
    #[case] offset: u16,
    #[case] nested: bool,
    #[case] valid: bool,
) {
    assert_eq!(
        verify(&aggregate_field(false, nested, offset)).is_ok(),
        valid
    );
}

#[rstest]
#[case(false)]
#[case(true)]
fn derived_struct_field_references_cannot_escape_frame(#[case] nested: bool) {
    let mut f = aggregate_field(false, nested, 8);
    let Type::StructRef(owner) = f.parameters[0] else {
        unreachable!()
    };
    f.parameters.clear();
    f.locals = vec![Type::Struct(owner)];
    let field = f.blocks[0].instructions[0].clone();
    f.blocks[0].parameters.clear();
    f.blocks[0].instructions = vec![
        instruction(2, Type::Struct(owner), Op::ZeroStruct),
        Instruction {
            result: None,
            op: Op::StoreLocal { local: 0, value: 2 },
            il_offset: 0,
        },
        instruction(
            0,
            Type::StructRef(owner),
            Op::LocalAddress {
                local: 0,
                storage: Storage::Struct(owner),
            },
        ),
        field,
    ];
    assert!(verify(&f).unwrap_err().contains("current frame may escape"));
}

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

#[rstest]
#[case(false, false, false)]
#[case(true, false, true)]
#[case(false, true, true)]
fn local_forwarding_requires_all_paths_and_unaliased_storage(
    #[case] disagree: bool,
    #[case] addressed: bool,
    #[case] retained: bool,
) {
    let mut f = diamond();
    let store = |value| Instruction {
        result: None,
        op: Op::StoreLocal { local: 0, value },
        il_offset: 0,
    };
    f.blocks[0].instructions.push(store(0));
    if addressed {
        f.blocks[0].instructions.push(instruction(
            6,
            Type::ByRef(Scalar::I32),
            Op::LocalAddress {
                local: 0,
                storage: Storage::Scalar(Scalar::I32),
            },
        ));
    }
    f.blocks[1]
        .instructions
        .push(store(if disagree { 1 } else { 0 }));
    f.blocks[2].instructions.push(store(0));
    if addressed {
        for (block, value) in [(1, 1), (2, 2)] {
            f.blocks[block].instructions.push(Instruction {
                result: None,
                op: Op::StoreMemory {
                    address: 6,
                    value,
                    bits: 32,
                },
                il_offset: 1,
            });
        }
    }
    f.blocks[3]
        .instructions
        .push(instruction(4, Type::I32, Op::LoadLocal(0)));
    f.blocks[3].terminator.kind = Control::Return(Some(4));
    verify(&f).unwrap();
    koh_compile::optimize::simplify(&mut f).unwrap();
    assert_eq!(
        f.blocks
            .iter()
            .flat_map(|b| &b.instructions)
            .any(|i| matches!(i.op, Op::LoadLocal(0))),
        retained
    );
    verify(&f).unwrap();
}

#[test]
fn typed_join_is_valid() {
    verify(&diamond()).unwrap();
}

fn pinned_reference() -> Function {
    Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![Type::ByRef(Scalar::U8)],
        returns: None,
        locals: vec![Type::ByRef(Scalar::U8)],
        blocks: vec![Block {
            parameters: vec![(0, Type::ByRef(Scalar::U8))],
            instructions: vec![
                instruction(1, Type::ByRef(Scalar::U8), Op::Constant(0)),
                Instruction {
                    result: None,
                    op: Op::StoreLocal { local: 0, value: 1 },
                    il_offset: 1,
                },
                Instruction {
                    result: None,
                    op: Op::StorePinned { local: 0, value: 0 },
                    il_offset: 2,
                },
            ],
            terminator: Terminator {
                il_offset: 3,
                kind: Control::Return(None),
            },
        }],
    }
}

#[test]
fn pinned_reference_initialization_and_cleanup_verify() {
    let mut f = pinned_reference();
    verify(&f).unwrap();
    f.blocks[0].instructions.extend([
        instruction(2, Type::Native, Op::Constant(0)),
        Instruction {
            result: None,
            op: Op::StorePinned { local: 0, value: 2 },
            il_offset: 4,
        },
    ]);
    verify(&f).unwrap();
    koh_compile::optimize::simplify(&mut f).unwrap();
    verify(&f).unwrap();
}

#[rstest]
#[case("type", "Pinned storage requires")]
#[case("value", "Pinned store type mismatch")]
#[case("initialization", "Pinned local lacks entry initialization")]
#[case("bypass", "Pinned local must use a pin store")]
#[case("index", "Invalid local")]
#[case("initial-index", "Invalid local")]
#[case("result", "Operation result type mismatch")]
fn malformed_pinned_storage_is_rejected(#[case] mutation: &str, #[case] expected: &str) {
    let mut f = pinned_reference();
    match mutation {
        "type" => {
            f.locals[0] = Type::I32;
            f.blocks[0].instructions[0].result = Some((1, Type::I32));
        }
        "value" => {
            f.blocks[0]
                .instructions
                .insert(2, instruction(2, Type::I32, Op::Constant(1)));
            f.blocks[0].instructions[3].op = Op::StorePinned { local: 0, value: 2 };
        }
        "initialization" => {
            f.blocks[0].instructions.remove(1);
        }
        "bypass" => f.blocks[0].instructions.push(Instruction {
            result: None,
            op: Op::StoreLocal { local: 0, value: 1 },
            il_offset: 4,
        }),
        "index" => {
            f.blocks[0].instructions[2].op = Op::StorePinned {
                local: 99,
                value: 0,
            }
        }
        "initial-index" => {
            f.blocks[0].instructions[1].op = Op::StoreLocal {
                local: 99,
                value: 1,
            };
            f.blocks[0].instructions[2].op = Op::StorePinned {
                local: 99,
                value: 0,
            };
        }
        "result" => f.blocks[0].instructions[2].result = Some((2, Type::I32)),
        _ => unreachable!(),
    }
    let error = verify(&f).unwrap_err();
    assert!(error.contains(expected), "{error}");
}

#[test]
fn pinning_cannot_hide_a_managed_frame_reference_escape() {
    let mut f = pinned_reference();
    f.parameters.clear();
    f.blocks[0].parameters.clear();
    f.locals.push(Type::I32);
    f.returns = Some(Type::ByRef(Scalar::U8));
    f.blocks[0].instructions.splice(
        0..0,
        [
            instruction(3, Type::I32, Op::Constant(0)),
            Instruction {
                result: None,
                op: Op::StoreLocal { local: 1, value: 3 },
                il_offset: 0,
            },
            instruction(
                0,
                Type::ByRef(Scalar::U8),
                Op::LocalAddress {
                    local: 1,
                    storage: Storage::Scalar(Scalar::U8),
                },
            ),
        ],
    );
    f.blocks[0]
        .instructions
        .push(instruction(2, Type::ByRef(Scalar::U8), Op::LoadLocal(0)));
    f.blocks[0].terminator.kind = Control::Return(Some(2));
    let error = verify(&f).unwrap_err();
    assert!(error.contains("current frame may escape"), "{error}");
}

#[rstest]
#[case("type", "Switch selector must be int32")]
#[case("missing", "Undefined value")]
#[case("arguments", "Edge argument count")]
#[case("capacity", "Switch case count exceeds limit")]
fn malformed_switches_are_rejected(#[case] mutation: &str, #[case] expected: &str) {
    let mut f = diamond();
    let mut selector = 0;
    let mut cases = vec![Edge {
        block: 1,
        arguments: vec![],
    }];
    match mutation {
        "type" => {
            f.parameters[0] = Type::I64;
            f.blocks[0].parameters[0].1 = Type::I64;
        }
        "missing" => selector = 999,
        "arguments" => cases[0].arguments.push(0),
        "capacity" => cases.resize(16_385, cases[0].clone()),
        _ => unreachable!(),
    }
    f.blocks[0].terminator.kind = Control::Switch {
        selector,
        cases,
        default: Edge {
            block: 2,
            arguments: vec![],
        },
    };
    let error = verify(&f).unwrap_err();
    assert!(error.contains(expected), "{error}");
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
    let mut function = initializer(Type::Array(Storage::Scalar(Scalar::U8)), vec![1]);
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
#[case(Type::Array(Storage::Scalar(Scalar::U8)), 3, true)]
#[case(Type::Array(Storage::Scalar(Scalar::I64)), 24, true)]
#[case(Type::Array(Storage::Scalar(Scalar::U8)), 0, false)]
#[case(Type::Array(Storage::Scalar(Scalar::U8)), 4097, false)]
#[case(Type::Array(Storage::Scalar(Scalar::NativeSigned)), 2, false)]
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
    let mut function = initializer(Type::Array(Storage::Scalar(Scalar::U8)), vec![1; 4096]);
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
    let mut function = initializer(Type::Array(Storage::Scalar(Scalar::U8)), vec![1]);
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
            instruction(
                1,
                Type::Array(Storage::Scalar(element)),
                Op::NewArray {
                    element: Storage::Scalar(element),
                    length: 0,
                    alignment: 1,
                },
            ),
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
#[case(Op::NewArray { element: Storage::Scalar(Scalar::I32), length: 0, alignment: 1 }, Type::Array(Storage::Scalar(Scalar::I32)))]
#[case(Op::ArrayAddress { array: 0, index: 0 }, Type::ByRef(Scalar::I32))]
#[case(Op::Negate(0), Type::Array(Storage::Scalar(Scalar::I32)))]
#[case(Op::Convert { value: 0, bits: 16, signed: false }, Type::Native)]
#[case(Op::Constant(1), Type::Array(Storage::Scalar(Scalar::I32)))]
fn invalid_array_ir_is_rejected(#[case] op: Op, #[case] ty: Type) {
    let mut function = diamond();
    function.locals.clear();
    function.parameters = vec![Type::Array(Storage::Scalar(Scalar::I32))];
    function.returns = Some(ty);
    function.blocks = vec![Block {
        parameters: vec![(0, Type::Array(Storage::Scalar(Scalar::I32)))],
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
                        storage: Storage::Scalar(Scalar::I32),
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
    f.blocks[0].instructions.last_mut().unwrap().op = Op::LocalAddress {
        local,
        storage: Storage::Scalar(scalar),
    };
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

fn static_address(token: u32, bytes: u16, ty: Type) -> Function {
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
            alignment: 1,
        },
    )];
    f.blocks[0].terminator.kind = Control::Return(Some(0));
    f
}

#[rstest]
#[case(0, false)]
#[case(1, true)]
#[case(3, false)]
#[case(256, true)]
#[case(4096, true)]
#[case(4097, false)]
fn storage_alignment_is_bounded_power_of_two(#[case] alignment: u16, #[case] valid: bool) {
    let mut f = static_address(0x04000001, 2, Type::Native);
    if let Op::StaticAddress {
        alignment: value, ..
    } = &mut f.blocks[0].instructions[0].op
    {
        *value = alignment;
    }
    assert_eq!(verify(&f).is_ok(), valid);
    f.returns = Some(Type::Array(Storage::Scalar(Scalar::U8)));
    f.blocks[0].instructions = vec![
        instruction(0, Type::I32, Op::Constant(1)),
        instruction(
            1,
            Type::Array(Storage::Scalar(Scalar::U8)),
            Op::NewArray {
                element: Storage::Scalar(Scalar::U8),
                length: 0,
                alignment,
            },
        ),
    ];
    f.blocks[0].terminator.kind = Control::Return(Some(1));
    assert_eq!(verify(&f).is_ok(), valid);
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
    #[case] bytes: u16,
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
    #[case] bits: u16,
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
