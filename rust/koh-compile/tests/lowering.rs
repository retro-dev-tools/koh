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

#[rstest]
#[case(Type::F32, 0x80000000)]
#[case(Type::F32, 0x7fa12345)]
#[case(Type::F32, 0xffc12345)]
#[case(Type::F32, 1)]
#[case(Type::F64, 0x8000000000000000)]
#[case(Type::F64, 0x7ff0123456789abc)]
#[case(Type::F64, 0xfff8123456789abc)]
#[case(Type::F64, 1)]
fn floating_identity_conversion_preserves_raw_bits(#[case] float: Type, #[case] bits: u64) {
    let mut il = vec![if float == Type::F64 { 0x23 } else { 0x22 }];
    if float == Type::F64 {
        il.extend(bits.to_le_bytes());
    } else {
        il.extend((bits as u32).to_le_bytes());
    }
    il.extend([if float == Type::F64 { 0x6c } else { 0x6b }, 0x2a]);
    let mut input = method(&il);
    input.returns = Some(float);
    let mut function = lower(input).unwrap();
    verify(&function).unwrap();
    assert_eq!(execute(&function), bits);
    koh_compile::optimize::simplify(&mut function).unwrap();
    verify(&function).unwrap();
    assert_eq!(execute(&function), bits);
}

#[rstest]
#[case(0x16, 7)]
#[case(0x17, 9)]
fn cil_stack_join_becomes_typed_block_arguments(#[case] condition: u8, #[case] expected: u64) {
    let f = lower(method(&[0x17, 0x2d, 3, 0x1d, 0x2b, 1, 0x1f, 9, 0x2a]));
    assert!(f.is_err(), "branch into the operand must fail");
    let f = lower(method(&[condition, 0x2d, 3, 0x1d, 0x2b, 2, 0x1f, 9, 0x2a])).unwrap();
    verify(&f).unwrap();
    assert_eq!(
        f.blocks
            .last()
            .unwrap()
            .parameters
            .iter()
            .map(|&(_, ty)| ty)
            .collect::<Vec<_>>(),
        vec![Type::I32]
    );
    assert_eq!(execute(&f), expected);
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
#[case(0, 41)]
#[case(1, 42)]
#[case(2, 43)]
#[case(3, 49)]
#[case(256, 49)]
#[case(65536, 49)]
#[case(0xffff_ffff, 49)]
fn cil_switch_preserves_the_remaining_stack(#[case] selector: u64, #[case] expected: u64) {
    let il = [
        0x1f, 40, 0x02, 0x45, 3, 0, 0, 0, 4, 0, 0, 0, 7, 0, 0, 0, 10, 0, 0, 0, 0x1f, 9, 0x58, 0x2a,
        0x17, 0x58, 0x2a, 0x18, 0x58, 0x2a, 0x19, 0x58, 0x2a,
    ];
    let mut m = method(&il);
    m.parameters = vec![Type::I32];
    let mut f = lower(m).unwrap();
    assert_eq!(execute_with(&f, &[selector]), expected);
    koh_compile::optimize::simplify(&mut f).unwrap();
    assert_eq!(execute_with(&f, &[selector]), expected);
}

#[test]
fn empty_switch_falls_through_without_consuming_the_rest_of_the_stack() {
    let il = [0x1f, 40, 0x02, 0x45, 0, 0, 0, 0, 0x58, 0x2a];
    let mut m = method(&il);
    m.parameters = vec![Type::I32];
    assert!(lower(m).unwrap_err().contains("underflow"));
    let il = [0x1f, 40, 0x02, 0x45, 0, 0, 0, 0, 0x2a];
    let mut m = method(&il);
    m.parameters = vec![Type::I32];
    let mut f = lower(m).unwrap();
    assert_eq!(execute_with(&f, &[0xffffffff]), 40);
    koh_compile::optimize::simplify(&mut f).unwrap();
    assert_eq!(execute_with(&f, &[0]), 40);
}

#[rstest]
#[case(Type::I64)]
#[case(Type::Native)]
fn switch_rejects_non_int32_selectors(#[case] ty: Type) {
    let il = [0x02, 0x45, 0, 0, 0, 0, 0x16, 0x2a];
    let mut m = method(&il);
    m.parameters = vec![ty];
    let error = lower(m).unwrap_err();
    assert!(
        error.contains("IL_0001") && error.contains("Switch selector must be int32"),
        "{error}"
    );
}

#[test]
fn switch_argument_budget_is_checked_before_cloning_the_stack() {
    let mut il = vec![0x16; 129];
    il.push(0x45);
    il.extend_from_slice(&16384u32.to_le_bytes());
    il.resize(il.len() + 16384 * 4, 0);
    il.extend(std::iter::repeat_n(0x26, 128));
    il.extend_from_slice(&[0x16, 0x2a]);
    let mut m = method(&il);
    m.max_stack = 129;
    let error = lower(m).unwrap_err();
    assert!(
        error.contains("IL_0081")
            && error.contains("Switch evaluation stack edge arguments exceed limit"),
        "{error}"
    );
}

#[rstest]
#[case(&[0x12, 0, 0x2a], false)]
#[case(&[0x0f, 0, 0x2a], true)]
fn cil_cannot_return_a_reference_to_its_own_storage(#[case] il: &[u8], #[case] argument: bool) {
    let mut m = method(il);
    m.returns = Some(Type::ByRef(Scalar::I32));
    if argument {
        m.parameters = vec![Type::I32];
    } else {
        m.locals = vec![Type::I32];
    }
    let error = lower(m).unwrap_err();
    assert!(
        error.contains("current frame may escape") && error.contains("IL_"),
        "{error}"
    );
}

#[test]
fn cil_can_return_an_incoming_reference() {
    let mut m = method(&[0x02, 0x2a]);
    m.parameters = vec![Type::ByRef(Scalar::I32)];
    m.returns = Some(Type::ByRef(Scalar::I32));
    verify(&lower(m).unwrap()).unwrap();
}

#[rstest]
#[case(false)]
#[case(true)]
fn array_null_joins_are_typed_in_both_orders(#[case] reverse: bool) {
    let mut il = vec![0x02, 0x2d, 3, 0x14, 0x2b, 1, 0x03, 0x2a];
    if reverse {
        il[3] = 0x03;
        il[6] = 0x14;
    }
    let mut m = method(&il);
    m.parameters = vec![
        Type::I32,
        Type::Array(Storage::Scalar(Scalar::I32), ArrayKind::Vector),
    ];
    m.returns = Some(Type::Array(Storage::Scalar(Scalar::I32), ArrayKind::Vector));
    let mut function = lower(m).unwrap();
    verify(&function).unwrap();
    koh_compile::optimize::simplify(&mut function).unwrap();
    verify(&function).unwrap();
}

#[rstest]
#[case(Scalar::I16, 0x97, Type::Native)]
#[case(Scalar::Char, 0x97, Type::Native)]
#[case(Scalar::NativeSigned, 0x92, Type::I32)]
#[case(Scalar::NativeUnsigned, 0x93, Type::I32)]
fn native_and_int16_array_loads_are_distinct(
    #[case] element: Scalar,
    #[case] opcode: u8,
    #[case] result: Type,
) {
    let il = [0x02, 0x16, opcode, 0x2a];
    let mut m = method(&il);
    m.parameters = vec![Type::Array(Storage::Scalar(element), ArrayKind::Vector)];
    m.returns = Some(result);
    assert!(lower(m).unwrap_err().contains("element storage type"));
}

#[rstest]
#[case(Scalar::I16, 0x9b, Type::Native)]
#[case(Scalar::NativeSigned, 0x9d, Type::I32)]
fn native_and_int16_array_stores_are_distinct(
    #[case] element: Scalar,
    #[case] opcode: u8,
    #[case] value: Type,
) {
    let il = [0x02, 0x16, 0x03, opcode, 0x2a];
    let mut m = method(&il);
    m.parameters = vec![
        Type::Array(Storage::Scalar(element), ArrayKind::Vector),
        value,
    ];
    m.returns = None;
    assert!(lower(m).unwrap_err().contains("element storage type"));
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
                Control::Return(_) | Control::TypeFault => {}
                Control::Switch { cases, default, .. } => {
                    for edge in cases.iter_mut().chain(std::iter::once(default)) {
                        edge.block = inverse[edge.block];
                    }
                }
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
#[case(&[0x17, 0xd3, 0x15, 0x58, 0x2a], Type::Native, 0)]
#[case(&[0x15, 0x17, 0xd3, 0x58, 0x2a], Type::Native, 0)]
#[case(&[0x16, 0xd3, 0x17, 0x59, 0x2a], Type::Native, 0xffff)]
#[case(&[0x17, 0x18, 0xd3, 0x59, 0x2a], Type::Native, 0xffff)]
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
                Op::StoreLocal { local, value } | Op::StorePinned { local, value } => {
                    locals[local as usize] = v(value);
                    0
                }
                Op::Binary {
                    op: Binary::Add,
                    left,
                    right,
                } => v(left).wrapping_add(v(right)),
                Op::Binary {
                    op: Binary::Subtract,
                    left,
                    right,
                } => v(left).wrapping_sub(v(right)),
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
                    let bits = u16::from(bits).min(types[&value].bits());
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
            Control::TypeFault => panic!("Unexpected runtime type fault in integer interpreter"),
            Control::Return(value) => return value.map(|id| values[&id]).unwrap_or(0),
            Control::Jump(edge) => edge,
            Control::Switch {
                selector,
                cases,
                default,
            } => cases.get(values[selector] as usize).unwrap_or(default),
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
