use koh_compile::{ir::*, optimize::simplify, resolve::Definition};
use rstest::rstest;

#[path = "../support/recursion.rs"]
mod fixture;
use fixture::{instruction, sum};

fn calls(f: &Function) -> usize {
    f.blocks
        .iter()
        .flat_map(|b| &b.instructions)
        .filter(|i| matches!(i.op, Op::Call { .. }))
        .count()
}

#[test]
fn debug_shared_local_return_keeps_entry_initialization() {
    let mut function = sum(Type::I32, 8, false);
    function.locals.push(Type::I32);
    function.blocks[0].instructions.push(Instruction {
        result: None,
        op: Op::StoreLocal { local: 0, value: 2 },
        il_offset: 2,
    });
    for (block, value) in [(1, 4), (2, 10)] {
        function.blocks[block].instructions.push(Instruction {
            result: None,
            op: Op::StoreLocal { local: 0, value },
            il_offset: value,
        });
        function.blocks[block].terminator.kind = Control::Jump(Edge {
            block: 3,
            arguments: vec![],
        });
    }
    function.blocks.push(Block {
        parameters: vec![],
        instructions: vec![
            instruction(11, Type::I32, Op::LoadLocal(0)),
            instruction(
                12,
                Type::I32,
                Op::Convert {
                    value: 11,
                    bits: 8,
                    signed: false,
                },
            ),
        ],
        terminator: Terminator {
            kind: Control::Return(Some(12)),
            il_offset: 12,
        },
    });
    verify(&function).unwrap();
    simplify(&mut function).unwrap();
    verify(&function).unwrap();
    assert_eq!(calls(&function), 0);
}

#[test]
fn signed_widening_continuation_constant_keeps_its_original_type() {
    let mut function = sum(Type::I64, 64, false);
    function.blocks[2]
        .instructions
        .insert(4, instruction(11, Type::I32, Op::Constant(0xffff_ffff)));
    function.blocks[2].instructions.insert(
        5,
        instruction(
            12,
            Type::I64,
            Op::Convert {
                value: 11,
                bits: 64,
                signed: true,
            },
        ),
    );
    function.blocks[2].instructions[6].op = Op::Binary {
        op: Binary::Add,
        left: 8,
        right: 12,
    };
    verify(&function).unwrap();
    simplify(&mut function).unwrap();
    assert_eq!(calls(&function), 1);
}

#[rstest]
#[case(Type::I32, 8, false)]
#[case(Type::I32, 8, true)]
#[case(Type::I32, 16, false)]
#[case(Type::I32, 16, true)]
#[case(Type::I32, 32, false)]
#[case(Type::I64, 64, false)]
fn pure_modular_recursion_becomes_verified_loop(
    #[case] ty: Type,
    #[case] bits: u8,
    #[case] signed: bool,
) {
    let mut function = sum(ty, bits, signed);
    verify(&function).unwrap();
    simplify(&mut function).unwrap();
    verify(&function).unwrap();
    assert_eq!(calls(&function), 0);
    assert!(
        function
            .blocks
            .iter()
            .any(|b| matches!(&b.terminator.kind, Control::Jump(e) if e.block == 1))
    );
}

#[rstest]
#[case("base-width")]
#[case("call-width")]
#[case("subtract")]
#[case("divide")]
#[case("address")]
#[case("memory")]
#[case("other-call")]
#[case("two-self-calls")]
#[case("fault")]
fn unsafe_accumulation_shapes_remain_recursive(#[case] shape: &str) {
    let mut function = sum(Type::I32, if shape == "call-width" { 16 } else { 8 }, false);
    match shape {
        "base-width" => {
            function.blocks[1].instructions[0].op = Op::Convert {
                value: 2,
                bits: 16,
                signed: false,
            }
        }
        "call-width" => {
            function.blocks[2].instructions[3].op = Op::Convert {
                value: 7,
                bits: 8,
                signed: false,
            }
        }
        "subtract" => {
            function.blocks[2].instructions[4].op = Op::Binary {
                op: Binary::Subtract,
                left: 8,
                right: 1,
            }
        }
        "divide" => {
            function.blocks[2].instructions[1].op = Op::Binary {
                op: Binary::DivideSigned,
                left: 0,
                right: 5,
            }
        }
        "address" => {
            function.locals.push(Type::I32);
            function.blocks[0].instructions.push(Instruction {
                result: None,
                op: Op::StoreLocal { local: 0, value: 2 },
                il_offset: 11,
            });
            function.blocks[0].instructions.push(instruction(
                11,
                Type::ByRef(Scalar::I32),
                Op::LocalAddress {
                    local: 0,
                    storage: FieldStorage::Value(Storage::Scalar(Scalar::I32)),
                },
            ));
        }
        "memory" => {
            function.blocks[0].instructions.push(instruction(
                11,
                Type::Native,
                Op::Constant(0xc000),
            ));
            function.blocks[0].instructions.push(Instruction {
                result: None,
                op: Op::StoreMemory {
                    address: 11,
                    value: 1,
                    bits: 32,
                },
                il_offset: 11,
            });
        }
        "other-call" => function.blocks[0].instructions.push(instruction(
            11,
            Type::I32,
            Op::Call {
                target: Definition {
                    assembly: 0,
                    token: 0x06000002,
                },
                signature: Signature {
                    parameters: vec![],
                    returns: Some(Type::I32),
                },
                arguments: vec![],
            },
        )),
        "two-self-calls" => function.blocks[2].instructions.push(instruction(
            11,
            Type::I32,
            Op::Call {
                target: function.identity,
                signature: Signature {
                    parameters: function.parameters.clone(),
                    returns: function.returns,
                },
                arguments: vec![6, 1],
            },
        )),
        "fault" => function.blocks[1].terminator.kind = Control::TypeFault,
        _ => unreachable!(),
    }
    verify(&function).unwrap();
    simplify(&mut function).unwrap();
    assert!(calls(&function) != 0);
}
