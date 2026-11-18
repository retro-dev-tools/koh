use koh_compile::{ir::*, resolve::Definition};
use rstest::rstest;

#[rstest]
#[case(4, false, true)]
#[case(5, false, false)]
#[case(4, true, false)]
fn span_payloads_share_the_ir_literal_budget(
    #[case] count: u32,
    #[case] string: bool,
    #[case] accepted: bool,
) {
    let identity = Definition {
        assembly: 0,
        token: 0x02000001,
    };
    let shape = ValueType::new(identity, 6).unwrap();
    let mut instructions = vec![Instruction {
        result: Some((0, Type::I32)),
        op: Op::Constant(0),
        il_offset: 0,
    }];
    for id in 1..=count {
        instructions.push(Instruction {
            result: Some((id, Type::Struct(shape))),
            op: Op::ByteSpanLiteral {
                bytes: vec![0; 0x4000],
                length: 0,
            },
            il_offset: id,
        });
    }
    if string {
        instructions.push(Instruction {
            result: Some((count + 1, Type::String)),
            op: Op::StringLiteral(vec![]),
            il_offset: count + 1,
        });
    }
    let function = Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![],
        returns: None,
        locals: vec![],
        blocks: vec![Block {
            parameters: vec![],
            instructions,
            terminator: Terminator {
                kind: Control::Return(None),
                il_offset: count + 1,
            },
        }],
    };
    let result = verify(&function);
    if accepted {
        result.unwrap();
    } else {
        let error =
            result.expect_err("IR literals above64KiB must reject before optimization or emission");
        assert!(
            error.contains("initializer data exceeds 64 KiB budget"),
            "{error}"
        );
        assert!(koh_compile::sm83::emit(&function).is_err());
    }
}

#[rstest]
#[case("load", None)]
#[case("write", Some("readonly ROM reference"))]
#[case("convert", Some("Managed reference conversion"))]
#[case("index", Some("int32 index"))]
#[case("shape", Some("byte-span literal"))]
#[case("size", Some("byte-span literal"))]
#[case("definition", Some("defined"))]
#[case("constant", Some("checked span indexing"))]
#[case("discard", None)]
fn byte_span_ir_preserves_readonly_storage_and_typed_operands(
    #[case] action: &str,
    #[case] expected: Option<&str>,
) {
    let shape = ValueType::new(
        Definition {
            assembly: 0,
            token: 0x02000001,
        },
        if action == "shape" { 5 } else { 6 },
    )
    .unwrap();
    let value = |id, ty, op| Instruction {
        result: Some((id, ty)),
        op,
        il_offset: id,
    };
    let mut instructions = vec![
        value(0, Type::I32, Op::Constant(4)),
        value(
            1,
            Type::Struct(shape),
            Op::ByteSpanLiteral {
                bytes: if action == "size" {
                    vec![0; 0x4001]
                } else {
                    b"Koh!\0".to_vec()
                },
                length: if action == "definition" { 99 } else { 0 },
            },
        ),
        value(
            2,
            if action == "index" {
                Type::Native
            } else {
                Type::I32
            },
            Op::Constant(0),
        ),
        value(
            3,
            Type::RomByteRef,
            if action == "constant" {
                Op::Constant(0)
            } else {
                Op::ByteSpanElement { span: 1, index: 2 }
            },
        ),
    ];
    match action {
        "discard" => {}
        "write" => {
            instructions.push(value(4, Type::I32, Op::Constant(42)));
            instructions.push(Instruction {
                result: None,
                op: Op::StoreMemory {
                    address: 3,
                    value: 4,
                    bits: 8,
                },
                il_offset: 5,
            });
        }
        "convert" => instructions.push(value(
            4,
            Type::Native,
            Op::Convert {
                value: 3,
                bits: 16,
                signed: false,
            },
        )),
        _ => instructions.push(value(
            4,
            Type::I32,
            Op::LoadMemory {
                address: 3,
                bits: 8,
                signed: false,
            },
        )),
    }
    let mut function = Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![],
        returns: None,
        locals: vec![],
        blocks: vec![Block {
            parameters: vec![],
            instructions,
            terminator: Terminator {
                kind: Control::Return(None),
                il_offset: 6,
            },
        }],
    };
    if let Some(expected) = expected {
        let error = verify(&function).expect_err("Invalid readonly span IR must reject");
        assert!(error.contains(expected), "{action}: {error}");
    } else {
        verify(&function).unwrap();
        koh_compile::optimize::simplify(&mut function).unwrap();
        verify(&function).unwrap();
        assert!(
            function
                .blocks
                .iter()
                .flat_map(|block| &block.instructions)
                .any(|i| matches!(i.op, Op::ByteSpanElement { .. })),
            "Indexing must retain its bounds fault"
        );
    }
}
