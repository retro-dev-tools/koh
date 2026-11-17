use koh_compile::{ir::*, optimize, sm83};
use koh_objects::{read_elf, write_elf};
use rstest::rstest;

#[path = "../support/sum_loop.rs"]
mod fixture;

#[rstest]
#[case(Type::Native)]
#[case(Type::I32)]
#[case(Type::I64)]
fn constructed_loop_survives_optimization_and_canonical_object_roundtrip(#[case] ty: Type) {
    let mut function = fixture::sum(ty);
    verify(&function).unwrap();
    optimize::simplify(&mut function).unwrap();
    verify(&function).unwrap();
    let first = write_elf(&sm83::emit(&function).unwrap()).unwrap();
    let second = write_elf(&sm83::emit(&function).unwrap()).unwrap();
    assert_eq!(first, second);
    assert_eq!(first, write_elf(&read_elf(&first).unwrap()).unwrap());
}

#[rstest]
#[case("return-type")]
#[case("return-missing")]
#[case("call-type")]
#[case("empty-function")]
#[case("invalid-successor")]
fn malformed_construction_is_rejected_before_emission(#[case] fault: &str) {
    let mut function = fixture::sum(Type::Native);
    let diagnostic = match fault {
        "return-type" => {
            function.returns = Some(Type::I32);
            "Return type"
        }
        "return-missing" => {
            function.blocks[3].terminator.kind = Control::Return(None);
            "Return type"
        }
        "call-type" => {
            function.blocks[3].instructions.push(Instruction {
                result: None,
                op: Op::Call {
                    target: function.identity,
                    signature: Signature {
                        parameters: vec![Type::I32],
                        returns: None,
                    },
                    arguments: vec![4],
                },
                il_offset: 42,
            });
            "argument"
        }
        "empty-function" => {
            function.blocks.clear();
            "block/local count"
        }
        "invalid-successor" => {
            function.blocks[0].terminator.kind = Control::Jump(Edge {
                block: 99,
                arguments: vec![],
            });
            "edge target"
        }
        _ => unreachable!(),
    };
    let error = verify(&function).unwrap_err();
    assert!(error.contains(diagnostic), "{fault}: {error}");
    assert!(sm83::emit(&function).is_err());
}
