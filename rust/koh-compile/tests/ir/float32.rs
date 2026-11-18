use koh_compile::{ir::*, optimize::simplify, resolve::Definition, sm83};
use rstest::rstest;

fn constant(float: Type, bits: u64) -> Function {
    let raw = if float == Type::F64 {
        Type::I64
    } else {
        Type::I32
    };
    Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![],
        returns: Some(raw),
        locals: vec![],
        blocks: vec![Block {
            parameters: vec![],
            instructions: vec![
                Instruction {
                    result: Some((0, float)),
                    op: Op::Constant(bits),
                    il_offset: 0,
                },
                Instruction {
                    result: Some((1, raw)),
                    op: Op::Reinterpret(0),
                    il_offset: 1,
                },
            ],
            terminator: Terminator {
                kind: Control::Return(Some(1)),
                il_offset: 2,
            },
        }],
    }
}

#[rstest]
#[case(Type::F32, 0x80000001)]
#[case(Type::F64, 0x8000000000000000)]
#[case(Type::F64, 0x7ff0123456789abc)]
#[case(Type::F64, 1)]
fn float_bits_are_distinct_and_backend_legal(#[case] float: Type, #[case] bits: u64) {
    let mut function = constant(float, bits);
    verify(&function).unwrap();
    simplify(&mut function).unwrap();
    sm83::emit(&function).unwrap();
}

#[rstest]
#[case("integer-add")]
#[case("integer-compare")]
#[case("integer-convert")]
#[case("integer-negate")]
#[case("wrong-bitcast")]
#[case("wrong-width")]
fn float_integer_confusion_is_rejected(
    #[case] fault: &str,
    #[values(Type::F32, Type::F64)] float: Type,
) {
    let mut function = constant(float, 0x80000001);
    match fault {
        "integer-add" => {
            function.blocks[0].instructions[1].op = Op::Binary {
                op: Binary::Add,
                left: 0,
                right: 0,
            }
        }
        "integer-compare" => {
            function.blocks[0].instructions[1].result = Some((1, Type::I32));
            function.returns = Some(Type::I32);
            function.blocks[0].instructions[1].op = Op::Compare {
                op: Comparison::Equal,
                left: 0,
                right: 0,
            }
        }
        "integer-convert" => {
            function.blocks[0].instructions[1].op = Op::Convert {
                value: 0,
                bits: float.bits() as u8,
                signed: false,
            }
        }
        "integer-negate" => function.blocks[0].instructions[1].op = Op::Negate(0),
        "wrong-bitcast" => {
            function.blocks[0].instructions[1].result = Some((1, Type::Native));
            function.returns = Some(Type::Native);
        }
        "wrong-width" => {
            if float == Type::F32 {
                function.blocks[0].instructions[0].op = Op::Constant(1u64 << 32);
            } else {
                function.blocks[0].instructions[1].result = Some((1, Type::I32));
                function.returns = Some(Type::I32);
            }
        }
        _ => unreachable!(),
    }
    assert!(verify(&function).is_err());
    assert!(sm83::emit(&function).is_err());
}
