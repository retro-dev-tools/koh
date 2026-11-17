use koh_compile::{ir::*, optimize, resolve::Definition, sm83};
use rstest::rstest;

fn function() -> Function {
    Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![],
        returns: Some(Type::I32),
        locals: vec![],
        blocks: vec![Block {
            parameters: vec![],
            instructions: vec![
                Instruction {
                    result: Some((0, Type::I32)),
                    op: Op::Constant(1),
                    il_offset: 0,
                },
                Instruction {
                    result: None,
                    op: Op::RequireRange(0),
                    il_offset: 1,
                },
                Instruction {
                    result: Some((1, Type::I32)),
                    op: Op::Constant(42),
                    il_offset: 2,
                },
            ],
            terminator: Terminator {
                kind: Control::Return(Some(1)),
                il_offset: 3,
            },
        }],
    }
}

#[rstest]
#[case(Type::I64)]
#[case(Type::Native)]
#[case(Type::F32)]
#[case(Type::F64)]
fn predicates_require_int32(#[case] ty: Type) {
    let mut f = function();
    f.blocks[0].instructions[0].result = Some((0, ty));
    assert!(verify(&f).unwrap_err().contains("int32 predicate"));
}

#[rstest]
#[case(false)]
#[case(true)]
fn guard_operands_and_effects_are_verified(#[case] result: bool) {
    let mut f = function();
    if result {
        f.blocks[0].instructions[1].result = Some((99, Type::I32));
    } else {
        f.blocks[0].instructions[1].op = Op::RequireRange(99);
    }
    assert!(verify(&f).is_err());
}

#[test]
fn promotion_rewrites_guard_aliases_without_losing_the_effect() {
    let mut f = function();
    f.locals = vec![Type::I32];
    f.blocks[0].instructions = vec![
        Instruction {
            result: Some((0, Type::I32)),
            op: Op::Constant(0),
            il_offset: 0,
        },
        Instruction {
            result: None,
            op: Op::StoreLocal { local: 0, value: 0 },
            il_offset: 1,
        },
        Instruction {
            result: Some((1, Type::I32)),
            op: Op::LoadLocal(0),
            il_offset: 2,
        },
        Instruction {
            result: None,
            op: Op::RequireRange(1),
            il_offset: 3,
        },
        Instruction {
            result: Some((2, Type::I32)),
            op: Op::Constant(42),
            il_offset: 4,
        },
    ];
    f.blocks[0].terminator.kind = Control::Return(Some(2));
    verify(&f).unwrap();
    optimize::simplify(&mut f).unwrap();
    verify(&f).unwrap();
    let object = sm83::emit(&f).unwrap();
    assert!(
        object
            .symbols
            .iter()
            .any(|s| s.name == "__koh_trap_arithmetic")
    );
}
