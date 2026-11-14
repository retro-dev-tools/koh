use koh_compile::{
    ir::*,
    lower::{Method, lower},
    resolve::Definition,
};
use rstest::rstest;

fn method(il: &[u8]) -> Method<'_> {
    Method {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        il,
        parameters: vec![],
        returns: Some(Type::I32),
        locals: vec![],
        init_locals: true,
        max_stack: 8,
    }
}

#[test]
fn cil_stack_join_becomes_typed_block_arguments() {
    let f = lower(method(&[0x17, 0x2d, 3, 0x1d, 0x2b, 1, 0x1f, 9, 0x2a]));
    assert!(f.is_err(), "branch into the operand must fail");
    let f = lower(method(&[0x17, 0x2d, 3, 0x1d, 0x2b, 2, 0x1f, 9, 0x2a])).unwrap();
    verify(&f).unwrap();
    assert_eq!(execute(&f), 9);
}

#[test]
fn backward_edges_preserve_mutable_locals() {
    let mut m = method(&[
        0x16, 0x0a, 0x06, 0x17, 0x58, 0x0a, 0x06, 0x19, 0x32, 0xf8, 0x06, 0x2a,
    ]);
    m.locals = vec![Type::I32];
    let f = lower(m).unwrap();
    assert_eq!(execute(&f), 3);
}

#[rstest]
#[case(false)]
#[case(true)]
fn cfg_simplification_preserves_loops_across_block_order(#[case] reverse: bool) {
    let mut m = method(&[
        0x16, 0x0a, 0x06, 0x17, 0x58, 0x0a, 0x06, 0x19, 0x32, 0xf8, 0x06, 0x2a,
    ]);
    m.locals = vec![Type::I32];
    let mut f = lower(m).unwrap();
    if reverse {
        let order: Vec<_> = std::iter::once(0)
            .chain((1..f.blocks.len()).rev())
            .collect();
        let mut inverse = vec![0; order.len()];
        for (new, &old) in order.iter().enumerate() {
            inverse[old] = new;
        }
        f.blocks = order.iter().map(|&index| f.blocks[index].clone()).collect();
        for block in &mut f.blocks {
            match &mut block.terminator.kind {
                Control::Jump(edge) => edge.block = inverse[edge.block],
                Control::Branch { yes, no, .. } => {
                    yes.block = inverse[yes.block];
                    no.block = inverse[no.block];
                }
                Control::Return(_) => {}
            }
        }
    }
    verify(&f).unwrap();
    assert_eq!(execute(&f), 3);
    let before = f.blocks.len();
    koh_compile::optimize::simplify(&mut f).unwrap();
    assert!(f.blocks.len() < before);
    assert_eq!(execute(&f), 3);
    let once = format!("{f:?}");
    koh_compile::optimize::simplify(&mut f).unwrap();
    assert_eq!(format!("{f:?}"), once);
}

#[rstest]
#[case(&[0x58, 0x2a], "underflow")]
#[case(&[0x16], "fallthrough")]
#[case(&[0x21, 0,0,0,0,0,0,0,0, 0x2a], "return")]
#[case(&[0x16, 0x17, 0x2a], "return")]
#[case(&[0x06, 0x2a], "local")]
#[case(&[0x28, 1,0,0,6, 0x2a], "opcode")]
#[case(&[0x17,0x2d,3,0x16,0x2b,9,0x21,0,0,0,0,0,0,0,0,0x2a], "merge")]
fn invalid_cil_has_method_and_offset_diagnostics(#[case] il: &[u8], #[case] purpose: &str) {
    let error = lower(method(il)).unwrap_err();
    assert!(error.contains("06000001"), "{error}");
    assert!(error.contains("IL_"), "{error}");
    assert!(error.contains(purpose), "{error}");
}

#[test]
fn unreachable_unsupported_bytes_are_not_lowered() {
    let f = lower(method(&[0x17, 0x2a, 0x28, 1, 0, 0, 6, 0x2a])).unwrap();
    assert_eq!(execute(&f), 1);
}

#[rstest]
#[case(&[0x17, 0x1f, 32, 0x62, 0x2a], Type::I32, 1)]
#[case(&[0x15, 0x17, 0x63, 0x2a], Type::I32, 0xffff_ffff)]
#[case(&[0x21, 0,0,0,0,1,0,0,0, 0x16,0x6a, 0xfe,2, 0x2a], Type::I32, 1)]
#[case(&[0x15, 0x6a, 0x17, 0x6a, 0x58, 0x2a], Type::I64, 0)]
fn integer_width_semantics(#[case] il: &[u8], #[case] ty: Type, #[case] expected: u64) {
    let mut m = method(il);
    m.returns = Some(ty);
    let f = lower(m).unwrap();
    assert_eq!(execute(&f), expected);
}

fn execute(f: &Function) -> u64 {
    execute_with(f, &[])
}

fn execute_with(f: &Function, arguments: &[u64]) -> u64 {
    let mut values = std::collections::HashMap::new();
    for (&(id, _), &value) in f.blocks[0].parameters.iter().zip(arguments) {
        values.insert(id, value);
    }
    let mut locals = vec![0; f.locals.len()];
    let types: std::collections::HashMap<_, _> = f
        .blocks
        .iter()
        .flat_map(|b| {
            b.parameters
                .iter()
                .copied()
                .chain(b.instructions.iter().filter_map(|i| i.result))
        })
        .collect();
    let mut block = 0;
    for _ in 0..1000 {
        for i in &f.blocks[block].instructions {
            let v = |id| values[&id];
            let signed = |id| {
                let shift = 64 - types[&id].bits();
                ((v(id) << shift) as i64) >> shift
            };
            let count = |left, right| (v(right) & u64::from(types[&left].bits() - 1)) as u32;
            let bits = match i.op {
                Op::Constant(bits) => bits,
                Op::LoadLocal(index) => locals[index as usize],
                Op::StoreLocal { local, value } => {
                    locals[local as usize] = v(value);
                    0
                }
                Op::Binary {
                    op: Binary::Add,
                    left,
                    right,
                } => v(left).wrapping_add(v(right)),
                Op::Binary {
                    op: Binary::Xor,
                    left,
                    right,
                } => v(left) ^ v(right),
                Op::Binary {
                    op: Binary::ShiftLeft,
                    left,
                    right,
                } => v(left).wrapping_shl(count(left, right)),
                Op::Binary {
                    op: Binary::ShiftRightSigned,
                    left,
                    right,
                } => (signed(left) >> count(left, right)) as u64,
                Op::Compare {
                    op: Comparison::Equal,
                    left,
                    right,
                } => u64::from(v(left) == v(right)),
                Op::Compare {
                    op: Comparison::LessSigned,
                    left,
                    right,
                } => u64::from(signed(left) < signed(right)),
                Op::Compare {
                    op: Comparison::GreaterSigned,
                    left,
                    right,
                } => u64::from(signed(left) > signed(right)),
                Op::Convert {
                    value,
                    bits,
                    signed,
                } => {
                    let bits = bits.min(types[&value].bits());
                    if signed {
                        (((v(value) << (64 - bits)) as i64) >> (64 - bits)) as u64
                    } else if bits == 64 {
                        v(value)
                    } else {
                        v(value) & ((1u64 << bits) - 1)
                    }
                }
                _ => panic!("test interpreter missing {:?}", i.op),
            };
            if let Some((id, ty)) = i.result {
                values.insert(
                    id,
                    if ty.bits() == 64 {
                        bits
                    } else {
                        bits & ((1 << ty.bits()) - 1)
                    },
                );
            }
        }
        let edge = match &f.blocks[block].terminator.kind {
            Control::Return(value) => return value.map(|id| values[&id]).unwrap_or(0),
            Control::Jump(edge) => edge,
            Control::Branch { condition, yes, no } => {
                if values[condition] != 0 {
                    yes
                } else {
                    no
                }
            }
        };
        let arguments: Vec<_> = edge.arguments.iter().map(|id| values[id]).collect();
        block = edge.block;
        for (&(id, _), value) in f.blocks[block].parameters.iter().zip(arguments) {
            values.insert(id, value);
        }
    }
    panic!("test execution exceeded its bound");
}

#[rstest::fixture]
#[once]
fn assemblies() -> [Vec<u8>; 2] {
    let directory = tempfile::Builder::new()
        .prefix("koh lowering fixture ")
        .tempdir()
        .unwrap();
    let fixture = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Integers");
    for name in ["Integers.csproj", "Arithmetic.cs"] {
        std::fs::copy(fixture.join(name), directory.path().join(name)).unwrap();
    }
    ["Debug", "Release"].map(|configuration| {
        let output =
            std::process::Command::new(std::env::var_os("KOH_DOTNET").unwrap_or("dotnet".into()))
                .arg("build")
                .arg(directory.path().join("Integers.csproj"))
                .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
                .output()
                .unwrap();
        assert!(
            output.status.success(),
            "{}\n{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
        std::fs::read(
            directory
                .path()
                .join(format!("bin/{configuration}/net10.0/Integers.dll")),
        )
        .unwrap()
    })
}

#[rstest]
#[case(0)]
#[case(1)]
fn real_roslyn_integer_control_flow(assemblies: &[Vec<u8>; 2], #[case] configuration: usize) {
    let resolver =
        koh_compile::resolve::Resolver::new(&[assemblies[configuration].as_slice()]).unwrap();
    let assembly = resolver.assembly(0).unwrap();
    for (name, arguments, expected) in [
        ("Sum", vec![], 55),
        ("Choose", vec![u32::MAX as u64], 7),
        ("Choose", vec![0], 9),
        ("Wrap", vec![0x7fff_ffff], 0x8000_0000),
        ("Wide", vec![3], 0x60_0000_0000),
        ("Truncate", vec![255], 0xffff_ffff),
        ("Extend", vec![u32::MAX as u64], u64::MAX),
        ("Small", vec![255], 0xffff_ffff),
    ] {
        let token = assembly
            .metadata()
            .method_defs
            .iter()
            .enumerate()
            .find(|(_, row)| assembly.metadata().strings.get(row.name).unwrap() == name)
            .map(|(i, _)| 0x06000001 + i as u32)
            .unwrap();
        let function =
            koh_compile::lower::assembly_method(&resolver, Definition { assembly: 0, token })
                .unwrap();
        assert_eq!(
            execute_with(&function, &arguments),
            expected,
            "{configuration}: {name}"
        );
        assert!(function.blocks.iter().any(|b| b.terminator.il_offset > 0));
        let mut optimized = function.clone();
        koh_compile::optimize::simplify(&mut optimized).unwrap();
        assert_eq!(
            execute_with(&optimized, &arguments),
            expected,
            "optimized {configuration}: {name}"
        );
        assert!(optimized.blocks.len() < function.blocks.len());
        let once = format!("{optimized:?}");
        koh_compile::optimize::simplify(&mut optimized).unwrap();
        assert_eq!(format!("{optimized:?}"), once);
    }
}
