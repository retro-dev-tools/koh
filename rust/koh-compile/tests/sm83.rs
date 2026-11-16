use koh_compile::{ir::*, resolve::Definition, sm83};
use koh_objects::*;
use rstest::fixture;
use std::path::Path;
mod support;
use support::{Host, dotnet};

#[fixture]
#[once]
fn host() -> Host {
    support::build_host()
}

#[rstest::rstest]
#[case(&[0x15,0xd3,0x20,255,0,0,0,0x5f,0x2a],0xff)]
#[case(&[0x20,255,0,0,0,0x15,0xd3,0x5f,0x2a],0xff)]
#[case(&[0x17,0xd3,0x1f,0x10,0x60,0x2a],0x11)]
#[case(&[0x17,0x1f,0x10,0xd3,0x61,0x2a],0x11)]
#[case(&[0x19,0x18,0xd3,0x5a,0x2a],6)]
#[case(&[0x1f,0xf9,0x18,0xd3,0x5b,0x2a],0xfffd)]
#[case(&[0x1f,0xf9,0x18,0xd3,0x5d,0x2a],0xffff)]
#[case(&[0x1d,0x18,0xd3,0x5c,0x2a],3)]
#[case(&[0x1d,0x18,0xd3,0x5e,0x2a],1)]
#[case(&[0x17,0xd3,0x18,0xd3,0x62,0x2a],4)]
fn mixed_native_integer_operations_obey_the_operand_table(
    host: &Host,
    #[case] il: &[u8],
    #[case] expected: u64,
) {
    let f = koh_compile::lower::lower(koh_compile::lower::Method {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        il,
        parameters: vec![],
        returns: Some(Type::Native),
        locals: vec![],
        init_locals: true,
        max_stack: 2,
    })
    .unwrap();
    run(host, &[(f, vec![], Ok(expected))]);
}

#[rstest::rstest]
#[case(0)]
#[case(20)]
fn hblank_stream_truncates_bytes_and_preserves_large_frames(host: &Host, #[case] locals: usize) {
    let mut f = function(Type::I32, Op::Constant(0));
    f.parameters = vec![Type::Native, Type::I32, Type::I32];
    f.locals = vec![Type::I64; locals];
    f.blocks[0].parameters = vec![(0, Type::Native), (1, Type::I32), (2, Type::I32)];
    f.blocks[0].instructions.clear();
    for (id, ty, value) in [
        (3, Type::Native, 0xff40),
        (4, Type::I32, 0x91),
        (5, Type::Native, 0xff43),
        (6, Type::I32, 0x33),
        (7, Type::I32, 0),
    ] {
        f.blocks[0].instructions.push(Instruction {
            result: Some((id, ty)),
            op: Op::Constant(value),
            il_offset: 0,
        });
    }
    for (address, value) in [(3, 4), (5, 7), (0, 6)] {
        f.blocks[0].instructions.push(Instruction {
            result: None,
            op: Op::StoreMemory {
                address,
                value,
                bits: 8,
            },
            il_offset: 0,
        });
    }
    f.blocks[0].instructions.push(Instruction {
        result: None,
        op: Op::HBlankStream {
            source: 0,
            first: 1,
            count: 2,
            register: 0xff43,
        },
        il_offset: 1,
    });
    f.blocks[0].instructions.push(Instruction {
        result: Some((8, Type::I32)),
        op: Op::LoadMemory {
            address: 5,
            bits: 8,
            signed: false,
        },
        il_offset: 2,
    });
    f.blocks[0].terminator.kind = Control::Return(Some(8));
    let cases = [
        (0x100, 0x100, Ok(0)),
        (0x100, 0x101, Ok(0x33)),
        (0x18f, 1, Ok(0x33)),
        (0x1ff, 0, Ok(0)),
        (0x1ff, 1, Err(6)),
        (143, 0x102, Err(6)),
    ]
    .map(|(first, count, result)| (f.clone(), vec![0xc018, first, count], result));
    run(host, &cases);
    run_at(host, &[(f, vec![0xc018, 0, 1], Err(1))], 0xc120);
}

fn function(ty: Type, op: Op) -> Function {
    Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![ty, ty],
        returns: Some(ty),
        locals: vec![],
        blocks: vec![Block {
            parameters: vec![(0, ty), (1, ty)],
            instructions: vec![Instruction {
                result: Some((2, ty)),
                op,
                il_offset: 0,
            }],
            terminator: Terminator {
                kind: Control::Return(Some(2)),
                il_offset: 1,
            },
        }],
    }
}

#[rstest::rstest]
#[case(5)]
#[case(16)]
#[case(64)]
fn aggregate_values_copy_initialize_and_cross_calls(host: &Host, #[case] bytes: u16) {
    let shape = ValueType::new(
        Definition {
            assembly: 0,
            token: 0x02000001,
        },
        bytes,
    )
    .unwrap();
    let ty = Type::Struct(shape);
    let child = ValueType::new(
        Definition {
            assembly: 0,
            token: 0x02000002,
        },
        bytes / 2,
    )
    .unwrap();
    let scalar = if bytes % 8 == 0 {
        Scalar::U64
    } else {
        Scalar::U8
    };
    let storage = Storage::Scalar(scalar);
    let scalar_ty = storage.stack_type();
    let step = storage.bytes();
    let pattern = |offset: u16| {
        if step == 8 {
            0x0123456789abcdefu64.rotate_left(u32::from(offset))
        } else {
            u64::from(0xa0 + offset)
        }
    };
    let mut code = vec![];
    let mut next = 2u32;
    let mut value = |code: &mut Vec<Instruction>, ty, op| {
        let id = next;
        next += 1;
        code.push(Instruction {
            result: Some((id, ty)),
            op,
            il_offset: id,
        });
        id
    };
    let store = |code: &mut Vec<Instruction>, op| {
        code.push(Instruction {
            result: None,
            op,
            il_offset: 0,
        })
    };
    let zero = value(&mut code, ty, Op::ZeroStruct);
    store(
        &mut code,
        Op::StoreLocal {
            local: 0,
            value: zero,
        },
    );
    let local = value(
        &mut code,
        Type::StructRef(shape),
        Op::LocalAddress {
            local: 0,
            storage: FieldStorage::Value(Storage::Struct(shape)),
        },
    );
    let mut valid = value(&mut code, Type::I32, Op::Constant(1));
    for offset in (0..bytes).step_by(usize::from(step)) {
        let address = value(
            &mut code,
            storage.reference_type(),
            Op::FieldAddress {
                address: local,
                owner: shape,
                offset,
                storage: FieldStorage::Value(storage),
            },
        );
        let actual = value(
            &mut code,
            scalar_ty,
            Op::LoadMemory {
                address,
                bits: step * 8,
                signed: false,
            },
        );
        let expected = value(&mut code, scalar_ty, Op::Constant(0));
        let equal = value(
            &mut code,
            Type::I32,
            Op::Compare {
                op: Comparison::Equal,
                left: actual,
                right: expected,
            },
        );
        valid = value(
            &mut code,
            Type::I32,
            Op::Binary {
                op: Binary::And,
                left: valid,
                right: equal,
            },
        );
        let address = value(
            &mut code,
            Type::Native,
            Op::FieldAddress {
                address: 0,
                owner: shape,
                offset,
                storage: FieldStorage::Value(storage),
            },
        );
        let data = value(&mut code, scalar_ty, Op::Constant(pattern(offset)));
        store(
            &mut code,
            Op::StoreMemory {
                address,
                value: data,
                bits: step * 8,
            },
        );
    }
    let snapshot = value(
        &mut code,
        ty,
        Op::LoadMemory {
            address: 0,
            bits: bytes * 8,
            signed: false,
        },
    );
    let changed = value(&mut code, scalar_ty, Op::Constant(0));
    store(
        &mut code,
        Op::StoreMemory {
            address: 0,
            value: changed,
            bits: step * 8,
        },
    );
    let callee = Definition {
        assembly: 0,
        token: 0x06000002,
    };
    let returned = value(
        &mut code,
        ty,
        Op::Call {
            target: callee,
            signature: Signature {
                parameters: vec![ty],
                returns: Some(ty),
            },
            arguments: vec![snapshot],
        },
    );
    store(
        &mut code,
        Op::StoreLocal {
            local: 0,
            value: returned,
        },
    );
    let copied = value(&mut code, ty, Op::LoadLocal(0));
    store(
        &mut code,
        Op::StoreMemory {
            address: 1,
            value: copied,
            bits: bytes * 8,
        },
    );
    let nested = value(
        &mut code,
        Type::Struct(child),
        Op::ExtractField {
            value: returned,
            offset: bytes - child.bytes(),
            storage: FieldStorage::Value(Storage::Struct(child)),
        },
    );
    let address = value(
        &mut code,
        Type::Native,
        Op::FieldAddress {
            address: 1,
            owner: shape,
            offset: bytes - child.bytes(),
            storage: FieldStorage::Value(Storage::Struct(child)),
        },
    );
    store(
        &mut code,
        Op::StoreMemory {
            address,
            value: nested,
            bits: child.bytes() * 8,
        },
    );
    let signed = value(
        &mut code,
        Type::I32,
        Op::ExtractField {
            value: returned,
            offset: 0,
            storage: FieldStorage::Value(Storage::Scalar(Scalar::I8)),
        },
    );
    let expected = value(
        &mut code,
        Type::I32,
        Op::Constant(u64::from(pattern(0) as u8 as i8 as i32 as u32)),
    );
    let equal = value(
        &mut code,
        Type::I32,
        Op::Compare {
            op: Comparison::Equal,
            left: signed,
            right: expected,
        },
    );
    valid = value(
        &mut code,
        Type::I32,
        Op::Binary {
            op: Binary::And,
            left: valid,
            right: equal,
        },
    );
    for offset in (0..bytes).step_by(usize::from(step)) {
        let address = value(
            &mut code,
            Type::Native,
            Op::FieldAddress {
                address: 1,
                owner: shape,
                offset,
                storage: FieldStorage::Value(storage),
            },
        );
        let actual = value(
            &mut code,
            scalar_ty,
            Op::LoadMemory {
                address,
                bits: step * 8,
                signed: false,
            },
        );
        let expected = value(&mut code, scalar_ty, Op::Constant(pattern(offset)));
        let equal = value(
            &mut code,
            Type::I32,
            Op::Compare {
                op: Comparison::Equal,
                left: actual,
                right: expected,
            },
        );
        valid = value(
            &mut code,
            Type::I32,
            Op::Binary {
                op: Binary::And,
                left: valid,
                right: equal,
            },
        );
    }
    let result = value(
        &mut code,
        Type::I64,
        Op::Convert {
            value: valid,
            bits: 32,
            signed: false,
        },
    );
    let root = Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![Type::Native, Type::Native],
        returns: Some(Type::I64),
        locals: vec![ty],
        blocks: vec![Block {
            parameters: vec![(0, Type::Native), (1, Type::Native)],
            instructions: code,
            terminator: Terminator {
                kind: Control::Return(Some(result)),
                il_offset: 0,
            },
        }],
    };
    let echo = Function {
        identity: callee,
        parameters: vec![ty],
        returns: Some(ty),
        locals: vec![],
        blocks: vec![Block {
            parameters: vec![(0, ty)],
            instructions: vec![],
            terminator: Terminator {
                kind: Control::Return(Some(0)),
                il_offset: 0,
            },
        }],
    };
    let rom = rom_program(
        &[root.clone(), echo],
        root.identity,
        &[0xd000, 0xd080],
        0xd000,
    );
    run_roms(host, &[(rom, Ok(1))], 0xd000);
}

#[rstest::rstest]
#[case(0)]
#[case(8)]
fn null_struct_field_reference_traps_before_offset(host: &Host, #[case] offset: u16) {
    let shape = ValueType::new(
        Definition {
            assembly: 0,
            token: 0x02000001,
        },
        64,
    )
    .unwrap();
    let f = Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![Type::StructRef(shape)],
        returns: Some(Type::I32),
        locals: vec![],
        blocks: vec![Block {
            parameters: vec![(0, Type::StructRef(shape))],
            instructions: vec![
                Instruction {
                    result: Some((1, Type::ByRef(Scalar::U32))),
                    op: Op::FieldAddress {
                        address: 0,
                        owner: shape,
                        offset,
                        storage: FieldStorage::Value(Storage::Scalar(Scalar::U32)),
                    },
                    il_offset: 0,
                },
                Instruction {
                    result: Some((2, Type::I32)),
                    op: Op::LoadMemory {
                        address: 1,
                        bits: 32,
                        signed: false,
                    },
                    il_offset: 1,
                },
            ],
            terminator: Terminator {
                kind: Control::Return(Some(2)),
                il_offset: 2,
            },
        }],
    };
    run(host, &[(f, vec![0], Err(4))]);
}

fn symbol(name: String, definition: SymbolDefinition) -> Symbol {
    Symbol {
        name,
        binding: if definition == SymbolDefinition::Import {
            SymbolBinding::Local
        } else {
            SymbolBinding::Exported
        },
        definition,
        source: None,
    }
}

fn patch(offset: u32, target: SymbolId) -> Patch {
    Patch {
        offset: offset as usize,
        width: PatchWidth::Word,
        semantics: PatchSemantics::Absolute,
        pc: AddressContext {
            section: SectionId(0),
            offset,
        },
        expression: RelocationExpr::Symbol(target),
        source: Provenance {
            file: "<test-start>".into(),
            line: 1,
            column: 1,
            expansion: vec![],
        },
    }
}

fn rom(f: &Function, arguments: &[u64], stack: u16) -> Vec<u8> {
    rom_program(std::slice::from_ref(f), f.identity, arguments, stack)
}

fn rom_program(
    functions: &[Function],
    entry: Definition,
    arguments: &[u64],
    stack: u16,
) -> Vec<u8> {
    verify_program(functions).unwrap();
    let f = functions.iter().find(|f| f.identity == entry).unwrap();
    let objects: Vec<_> = functions
        .iter()
        .map(|f| read_elf(&write_elf(&sm83::emit(f).unwrap()).unwrap()).unwrap())
        .collect();
    let mut data = vec![0; 0x150];
    data[0x100..0x103].copy_from_slice(&[0xc3, 0x50, 0x01]);
    data.extend_from_slice(&[0xf3, 0x31, stack as u8, (stack >> 8) as u8]);
    let ret_bytes = f.returns.map(|t| t.bits() / 8).unwrap_or(0);
    let bytes = usize::from(ret_bytes)
        + f.parameters
            .iter()
            .map(|t| usize::from(t.bits() / 8))
            .sum::<usize>();
    assert!(bytes < 128);
    data.extend_from_slice(&[0xe8, (-(bytes as i16)) as u8]);
    let mut offset = usize::from(ret_bytes);
    for (&argument, ty) in arguments.iter().zip(&f.parameters) {
        for byte in argument
            .to_le_bytes()
            .iter()
            .take(usize::from(ty.bits() / 8))
        {
            data.extend_from_slice(&[0xf8, offset as u8, 0x36, *byte]);
            offset += 1;
        }
    }
    data.push(0xcd);
    let call = data.len() as u32;
    data.extend_from_slice(&[0, 0]);
    for i in 0..ret_bytes {
        let i = u8::try_from(i).unwrap();
        data.extend_from_slice(&[0xf8, i, 0x7e, 0xea, 0x08 + i, 0xc0]);
    }
    data.extend_from_slice(&[0xe8, bytes as u8, 0x3e, 0xa5, 0xea, 0x00, 0xc0, 0x18, 0xfe]);
    let stack_trap = data.len() as u32;
    data.extend_from_slice(&[
        0x3e, 1, 0xea, 1, 0xc0, 0x3e, 0xee, 0xea, 0, 0xc0, 0x18, 0xfe,
    ]);
    let arithmetic_trap = data.len() as u32;
    data.extend_from_slice(&[
        0x3e, 2, 0xea, 1, 0xc0, 0x3e, 0xee, 0xea, 0, 0xc0, 0x18, 0xfe,
    ]);
    let null_trap = data.len() as u32;
    data.extend_from_slice(&[
        0x3e, 4, 0xea, 1, 0xc0, 0x3e, 0xee, 0xea, 0, 0xc0, 0x18, 0xfe,
    ]);
    let pin_trap = data.len() as u32;
    data.extend_from_slice(&[
        0x3e, 8, 0xea, 1, 0xc0, 0x3e, 0xee, 0xea, 0, 0xc0, 0x18, 0xfe,
    ]);
    let bounds_trap = data.len() as u32;
    data.extend_from_slice(&[
        0x3e, 6, 0xea, 1, 0xc0, 0x3e, 0xee, 0xea, 0, 0xc0, 0x18, 0xfe,
    ]);
    let hardware_trap = data.len() as u32;
    data.extend_from_slice(&[
        0x3e, 9, 0xea, 1, 0xc0, 0x3e, 0xee, 0xea, 0, 0xc0, 0x18, 0xfe,
    ]);
    data.resize(0x300, 0);
    let entry = Object {
        sections: vec![Section {
            name: "test.start".into(),
            kind: 0,
            address: Some(0),
            bank: Some(0),
            size: data.len() as u32,
            data,
            ..Default::default()
        }],
        symbols: vec![
            symbol(sm83::symbol(f.identity), SymbolDefinition::Import),
            symbol(
                "__koh_trap_stack".into(),
                SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: stack_trap,
                },
            ),
            symbol(
                "__koh_trap_arithmetic".into(),
                SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: arithmetic_trap,
                },
            ),
            symbol(
                "__koh_trap_null".into(),
                SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: null_trap,
                },
            ),
            symbol(
                "__koh_trap_pin".into(),
                SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: pin_trap,
                },
            ),
            symbol(
                "__koh_trap_bounds".into(),
                SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: bounds_trap,
                },
            ),
            symbol(
                "__koh_trap_hardware".into(),
                SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: hardware_trap,
                },
            ),
        ],
    };
    let mut entry = entry;
    entry.sections[0].patches.push(patch(call, SymbolId(0)));
    let mut all = vec![entry];
    all.extend(objects);
    koh_link::link(&all, &koh_link::LinkOptions::default())
        .unwrap()
        .rom
}

fn run(host: &Host, cases: &[(Function, Vec<u64>, Result<u64, u8>)]) {
    run_at(host, cases, 0xd000);
}

fn run_at(host: &Host, cases: &[(Function, Vec<u64>, Result<u64, u8>)], stack: u16) {
    let programs: Vec<_> = cases
        .iter()
        .map(|(f, args, expected)| (rom(f, args, stack), *expected))
        .collect();
    run_roms(host, &programs, stack);
}

fn run_roms(host: &Host, cases: &[(Vec<u8>, Result<u64, u8>)], stack: u16) {
    let directory = tempfile::Builder::new()
        .prefix("rom run ")
        .tempdir_in(host.directory.path())
        .unwrap();
    let mut command = dotnet();
    command.arg(&host.dll);
    for (index, (rom, _)) in cases.iter().enumerate() {
        let path = directory.path().join(format!("case{index}.gb"));
        std::fs::write(&path, rom).unwrap();
        command.arg(path);
    }
    let output = command.output().unwrap();
    assert!(
        output.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let stdout = String::from_utf8(output.stdout).unwrap();
    let lines: Vec<_> = stdout.lines().collect();
    assert_eq!(lines.len(), cases.len(), "{stdout}");
    for (index, (line, (_, expected))) in lines.iter().zip(cases).enumerate() {
        let fields: Vec<_> = line.split_whitespace().collect();
        assert_eq!(
            fields[4], "OK",
            "stack guard modified reserved memory in case {index}"
        );
        if let Err(fault) = expected {
            assert_eq!(fields[0], "EE", "case {index}: {line}");
            assert_eq!(fields[1], format!("{fault:02X}"), "case {index}: {line}");
            continue;
        }
        assert_eq!(fields[0], "A5", "case {index}: {line}");
        let result = (0..8)
            .map(|i| {
                u64::from(u8::from_str_radix(&fields[2][i * 2..i * 2 + 2], 16).unwrap()) << (i * 8)
            })
            .fold(0, |a, b| a | b);
        assert_eq!(Ok(result), *expected, "case {index}: {line}");
        assert_eq!(
            fields[3],
            format!("{stack:04X}"),
            "unbalanced SP in case {index}"
        );
    }
}

#[rstest::rstest]
fn managed_calls_preserve_caller_values_and_guard_call_storage(host: &Host) {
    let mut callee = function(
        Type::I32,
        Op::Binary {
            op: Binary::Add,
            left: 0,
            right: 1,
        },
    );
    callee.identity.token += 1;
    let mut caller = function(
        Type::I32,
        Op::Call {
            target: callee.identity,
            signature: Signature {
                parameters: vec![Type::I32; 2],
                returns: Some(Type::I32),
            },
            arguments: vec![0, 1],
        },
    );
    caller.blocks[0].instructions.push(Instruction {
        result: Some((3, Type::I32)),
        op: Op::Binary {
            op: Binary::Add,
            left: 0,
            right: 2,
        },
        il_offset: 2,
    });
    caller.blocks[0].terminator.kind = Control::Return(Some(3));
    let entry = caller.identity;
    let functions = vec![caller, callee];
    let normal = rom_program(&functions, entry, &[11, 22], 0xd000);
    run_roms(host, &[(normal, Ok(44))], 0xd000);
    let guarded = rom_program(&functions, entry, &[11, 22], 0xc144);
    run_roms(host, &[(guarded, Err(1))], 0xc144);
}

#[test]
fn stack_reservation_is_canonical_and_blocks_overlapping_globals() {
    let f = function(
        Type::I32,
        Op::Binary {
            op: Binary::Add,
            left: 0,
            right: 1,
        },
    );
    let object = read_elf(&write_elf(&sm83::emit(&f).unwrap()).unwrap()).unwrap();
    assert!(object.sections.iter().any(|s| s.kind == 4
        && s.address == Some(0xc100)
        && s.size == 0xf00
        && s.combination == SectionCombination::Union));
    let globals = Object {
        sections: vec![Section {
            name: "test.globals".into(),
            kind: 4,
            address: Some(0xc080),
            bank: Some(0),
            size: 0x90,
            ..Default::default()
        }],
        symbols: vec![],
    };
    assert!(
        koh_link::link(&[object, globals], &koh_link::LinkOptions::default())
            .unwrap_err()
            .contains("Cannot place")
    );
}

#[rstest::rstest]
fn integer_arithmetic_executes_in_existing_emulator(host: &Host) {
    let mut cases = Vec::new();
    for (ty, mask, sign) in [
        (Type::I32, 0xffff_ffff, 0x8000_0000),
        (Type::I64, u64::MAX, 0x8000_0000_0000_0000),
        (Type::Native, 0xffff, 0x8000),
    ] {
        for (op, left, right, expected) in [
            (Binary::Add, mask, 1, 0),
            (Binary::Subtract, 0, 1, mask),
            (Binary::And, mask, 0x55, 0x55),
            (Binary::Or, sign, 0x55, sign | 0x55),
            (Binary::Xor, mask, 0x55, mask ^ 0x55),
            (Binary::Multiply, mask, 3, mask - 2),
            (Binary::DivideUnsigned, mask, 2, mask >> 1),
            (Binary::DivideSigned, mask - 6, 3, mask - 1),
            (Binary::RemainderUnsigned, mask, 2, 1),
            (Binary::RemainderSigned, mask - 6, 3, mask),
        ] {
            cases.push((
                function(
                    ty,
                    Op::Binary {
                        op,
                        left: 0,
                        right: 1,
                    },
                ),
                vec![left, right],
                Ok(expected),
            ));
        }
    }
    run(host, &cases);
}

#[rstest::rstest]
#[case(Type::Native, 0)]
#[case(Type::Native, 20)]
#[case(Type::I32, 0)]
#[case(Type::I32, 20)]
#[case(Type::I64, 0)]
#[case(Type::I64, 20)]
fn narrow_arithmetic_preserves_carry_at_large_stack_offsets(
    host: &Host,
    #[case] ty: Type,
    #[case] locals: usize,
) {
    let mask = if ty == Type::I64 {
        u64::MAX
    } else {
        (1u64 << ty.bits()) - 1
    };
    let left = 254u64;
    let right = 0x123456789abcdef & mask;
    let mut cases = Vec::new();
    for (op, expected) in [
        (Binary::Add, left.wrapping_add(right)),
        (Binary::Subtract, left.wrapping_sub(right)),
        (Binary::And, left & right),
        (Binary::Or, left | right),
        (Binary::Xor, left ^ right),
    ] {
        let mut f = function(
            ty,
            Op::Binary {
                op,
                left: 3,
                right: 1,
            },
        );
        f.locals = vec![Type::I64; locals];
        f.blocks[0].instructions.insert(
            0,
            Instruction {
                result: Some((3, ty)),
                op: Op::Convert {
                    value: 0,
                    bits: 8,
                    signed: false,
                },
                il_offset: 0,
            },
        );
        cases.push((f, vec![0x12fe, right], Ok(expected & mask)));
    }
    run(host, &cases);
}

#[rstest::rstest]
#[case("Debug")]
#[case("Release")]
fn roslyn_methods_execute_as_sm83(host: &Host, #[case] configuration: &str) {
    let game = host.directory.path().join(format!("game-{configuration}"));
    std::fs::create_dir_all(&game).unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Integers");
    for name in ["Integers.csproj", "Arithmetic.cs"] {
        std::fs::copy(fixture.join(name), game.join(name)).unwrap();
    }
    let output = dotnet()
        .arg("build")
        .arg(game.join("Integers.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let bytes =
        std::fs::read(game.join(format!("bin/{configuration}/net10.0/Integers.dll"))).unwrap();
    let resolver = koh_compile::resolve::Resolver::new(&[bytes.as_slice()]).unwrap();
    let assembly = resolver.assembly(0).unwrap();
    let mut cases = Vec::new();
    for (name, arguments, expected) in [
        ("Sum", vec![], 55),
        ("Choose", vec![u32::MAX as u64], 7),
        ("Choose", vec![0], 9),
        ("Wrap", vec![0x7fff_ffff], 0x8000_0000),
        ("Wide", vec![3], 0x60_0000_0000),
        ("Wide", vec![u64::MAX], 0x1f_ffff_ffff),
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
        let f = koh_compile::lower::assembly_method(&resolver, Definition { assembly: 0, token })
            .unwrap();
        cases.push((f, arguments, Ok(expected)));
    }
    run(host, &cases);
}

#[rstest::rstest]
fn widths_comparisons_shifts_and_conversions_execute(host: &Host) {
    let mut cases = Vec::new();
    for (ty, mask, sign) in [
        (Type::I32, 0xffff_ffff, 0x8000_0000),
        (Type::I64, u64::MAX, 0x8000_0000_0000_0000),
        (Type::Native, 0xffff, 0x8000),
    ] {
        for (op, left, right, expected) in [
            (Comparison::Equal, mask, mask, 1),
            (Comparison::Equal, sign, 0, 0),
            (Comparison::LessSigned, mask, 1, 1),
            (Comparison::GreaterSigned, 1, mask, 1),
            (Comparison::LessUnsigned, mask, 1, 0),
            (Comparison::GreaterUnsigned, mask, 1, 1),
            (Comparison::LessSigned, sign, mask, 1),
            (Comparison::GreaterSigned, mask, sign, 1),
        ] {
            let mut f = function(
                ty,
                Op::Compare {
                    op,
                    left: 0,
                    right: 1,
                },
            );
            f.returns = Some(Type::I32);
            f.blocks[0].instructions[0].result = Some((2, Type::I32));
            cases.push((f, vec![left, right], Ok(expected)));
        }
        for (op, left, right, expected) in [
            (Binary::ShiftLeft, 1, u64::from(ty.bits()), 1),
            (Binary::ShiftLeft, 1, u64::from(ty.bits() + 1), 2),
            (Binary::ShiftRightSigned, mask, 1, mask),
            (Binary::ShiftRightUnsigned, mask, 1, mask >> 1),
            (Binary::ShiftRightSigned, sign, 1, (sign >> 1) | sign),
        ] {
            let mut f = function(
                ty,
                Op::Binary {
                    op,
                    left: 0,
                    right: 1,
                },
            );
            f.parameters[1] = Type::I32;
            f.blocks[0].parameters[1].1 = Type::I32;
            cases.push((f, vec![left, right], Ok(expected)));
        }
        for (op, input, expected) in [
            (Op::Negate(0), 1, mask),
            (Op::Negate(0), sign, sign),
            (Op::Not(0), 0, mask),
        ] {
            let mut f = function(ty, op);
            f.parameters.truncate(1);
            f.blocks[0].parameters.truncate(1);
            cases.push((f, vec![input], Ok(expected)));
        }
    }
    for (source, target, bits, signed, input, expected) in [
        (Type::I32, Type::I64, 64, true, 0xffff_ffff, u64::MAX),
        (Type::I32, Type::I64, 64, false, 0xffff_ffff, 0xffff_ffff),
        (Type::I64, Type::I32, 8, true, 0x1234_ff, 0xffff_ffff),
        (Type::I64, Type::I32, 8, false, u64::MAX, 0xff),
        (Type::I64, Type::Native, 16, false, u64::MAX, 0xffff),
        (Type::Native, Type::I64, 64, true, 0xffff, u64::MAX),
    ] {
        let mut f = function(
            source,
            Op::Convert {
                value: 0,
                bits,
                signed,
            },
        );
        f.parameters.truncate(1);
        f.blocks[0].parameters.truncate(1);
        f.returns = Some(target);
        f.blocks[0].instructions[0].result = Some((2, target));
        cases.push((f, vec![input], Ok(expected)));
    }
    run(host, &cases);
}

#[rstest::rstest]
#[case(0, false)]
#[case(20, false)]
#[case(0, true)]
#[case(20, true)]
fn memory_copy_truncates_count_and_preserves_stack(
    host: &Host,
    #[case] locals: usize,
    #[case] constant: bool,
) {
    let mut f = function(Type::I32, Op::Constant(0));
    f.parameters = vec![Type::Native, Type::Native, Type::I32];
    f.returns = None;
    f.locals = vec![Type::I64; locals];
    f.blocks[0].parameters = vec![(0, Type::Native), (1, Type::Native), (2, Type::I32)];
    f.blocks[0].terminator.kind = Control::Return(None);
    let pattern = 0x8877665544332211u64;
    let mut cases = Vec::new();
    for count in [0u64, 1, 2, 7, 8, 0x10000, 0x10002] {
        f.blocks[0].instructions = vec![
            Instruction {
                result: Some((3, Type::I64)),
                op: Op::Constant(pattern),
                il_offset: 0,
            },
            Instruction {
                result: None,
                op: Op::StoreMemory {
                    address: 1,
                    value: 3,
                    bits: 64,
                },
                il_offset: 0,
            },
        ];
        if constant {
            f.blocks[0].instructions.push(Instruction {
                result: Some((4, Type::I32)),
                op: Op::Constant(count),
                il_offset: 0,
            });
        }
        f.blocks[0].instructions.push(Instruction {
            result: None,
            op: Op::MemoryCopy {
                destination: 0,
                source: 1,
                count: if constant { 4 } else { 2 },
            },
            il_offset: 1,
        });
        let bytes = count & 0xffff;
        let expected = if bytes == 8 {
            pattern
        } else {
            pattern & ((1u64 << (bytes * 8)) - 1)
        };
        cases.push((f.clone(), vec![0xc008, 0xc018, count], Ok(expected)));
    }
    run(host, &cases);
    run_at(host, &[(f, vec![0xc008, 0xc018, 2], Err(1))], 0xc120);
}

#[rstest::rstest]
#[case(0, false)]
#[case(20, false)]
#[case(0, true)]
#[case(20, true)]
fn memory_fill_truncates_formal_widths_and_preserves_stack(
    host: &Host,
    #[case] locals: usize,
    #[case] constant: bool,
) {
    let mut f = function(Type::I32, Op::Constant(0));
    f.parameters = vec![Type::Native, Type::I32, Type::I32];
    f.returns = None;
    f.locals = vec![Type::I64; locals];
    f.blocks[0].parameters = vec![(0, Type::Native), (1, Type::I32), (2, Type::I32)];
    f.blocks[0].terminator.kind = Control::Return(None);
    let mut cases = Vec::new();
    for count in [0u64, 1, 2, 7, 8, 0x10000, 0x10002] {
        f.blocks[0].instructions.clear();
        if constant {
            f.blocks[0].instructions.push(Instruction {
                result: Some((3, Type::I32)),
                op: Op::Constant(count),
                il_offset: 0,
            });
        }
        f.blocks[0].instructions.push(Instruction {
            result: None,
            op: Op::MemoryFill {
                address: 0,
                value: 1,
                count: if constant { 3 } else { 2 },
            },
            il_offset: 1,
        });
        let expected = (0..(count & 0xffff)).fold(0, |bits, byte| bits | (0xa7u64 << (byte * 8)));
        cases.push((f.clone(), vec![0xc008, 0x123456a7, count], Ok(expected)));
    }
    run(host, &cases);
    run_at(host, &[(f, vec![0xc008, 0xa7, 2], Err(1))], 0xc120);
}

#[rstest::rstest]
#[case(Type::I32)]
#[case(Type::I64)]
fn repeated_products_fit_a_rom_bank_and_preserve_frames(host: &Host, #[case] ty: Type) {
    let mut f = function(
        ty,
        Op::Binary {
            op: Binary::Multiply,
            left: 0,
            right: 1,
        },
    );
    f.locals = vec![Type::I64; 20];
    f.blocks[0].instructions = (2..42)
        .map(|id| Instruction {
            result: Some((id, ty)),
            op: Op::Binary {
                op: Binary::Multiply,
                left: if id == 2 { 0 } else { id - 1 },
                right: 1,
            },
            il_offset: id,
        })
        .collect();
    f.blocks[0].terminator.kind = Control::Return(Some(41));
    run(host, &[(f, vec![30, 1], Ok(30))]);
}

#[rstest::rstest]
#[case(Type::I32, 0xffff_ffe2, 0xffff_ff81)]
#[case(Type::I64, (-30i64) as u64, (-127i64) as u64)]
fn small_multiplication_cycle_cost(
    host: &Host,
    #[case] ty: Type,
    #[case] left: u64,
    #[case] right: u64,
) {
    let f = function(
        ty,
        Op::Binary {
            op: Binary::Multiply,
            left: 0,
            right: 1,
        },
    );
    let directory = tempfile::tempdir_in(host.directory.path()).unwrap();
    let path = directory.path().join("multiply.gb");
    std::fs::write(&path, rom(&f, &[left, right], 0xd000)).unwrap();
    let output = dotnet()
        .arg(&host.dll)
        .arg("--cost")
        .arg(path)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let stdout = String::from_utf8(output.stdout).unwrap();
    let fields: Vec<_> = stdout.split_whitespace().collect();
    assert_eq!(fields[0], "A5", "{stdout}");
    assert_eq!(fields[2], "E20E000000000000", "{stdout}");
    assert_eq!(fields[3], "D000", "{stdout}");
    assert_eq!(fields[4], "OK", "{stdout}");
    let cycles: u64 = fields[5].parse().unwrap();
    println!("{ty:?}: {cycles} T-cycles");
    assert!(
        cycles < 20000,
        "small {ty:?} multiplication costs {cycles} T-cycles; budget 20000"
    );
}

#[rstest::rstest]
#[case(Type::Native, 0)]
#[case(Type::Native, 20)]
#[case(Type::I32, 0)]
#[case(Type::I32, 20)]
#[case(Type::I64, 0)]
#[case(Type::I64, 20)]
fn multiplication_dispatch_preserves_modular_width(
    host: &Host,
    #[case] ty: Type,
    #[case] locals: usize,
) {
    let mask = if ty == Type::I64 {
        u64::MAX
    } else {
        (1u64 << ty.bits()) - 1
    };
    let sign = 1u64 << (ty.bits() - 1);
    let mut cases = Vec::new();
    for (left, right) in [
        (0, 0),
        (255, 255),
        (256, 255),
        (255, 256),
        (mask, mask),
        (mask - 254, 255),
        (255, mask - 254),
        (mask - 255, 255),
        (sign, mask),
        (sign, 2),
        (sign, sign),
        (sign + 1, 3),
    ] {
        let mut f = function(
            ty,
            Op::Binary {
                op: Binary::Multiply,
                left: 0,
                right: 1,
            },
        );
        f.locals = vec![Type::I64; locals];
        cases.push((f, vec![left, right], Ok(left.wrapping_mul(right) & mask)));
    }
    run(host, &cases);
}

#[rstest::rstest]
#[case(Type::I32)]
#[case(Type::I64)]
fn narrow_spills_preserve_full_width_arithmetic_and_call_results(host: &Host, #[case] ty: Type) {
    let mask = if ty == Type::I64 {
        u64::MAX
    } else {
        u64::from(u32::MAX)
    };
    let mut cases = Vec::new();
    let normalized = |op| {
        let mut f = function(ty, op);
        f.blocks[0].instructions.insert(
            0,
            Instruction {
                result: Some((3, ty)),
                op: Op::Convert {
                    value: 0,
                    bits: 8,
                    signed: false,
                },
                il_offset: 1,
            },
        );
        f.blocks[0].instructions.insert(
            1,
            Instruction {
                result: Some((4, ty)),
                op: Op::Convert {
                    value: 1,
                    bits: 8,
                    signed: false,
                },
                il_offset: 2,
            },
        );
        f
    };
    for (op, left, right, expected) in [
        (Binary::Add, 0x1234ff, 0xab01, 256),
        (Binary::Subtract, 0, 1, mask),
        (Binary::Multiply, 255, 255, 65025),
        (Binary::And, 0xff, 0x80, 0x80),
        (Binary::Or, 0x80, 1, 0x81),
        (Binary::Xor, 0xff, 0xf0, 15),
        (Binary::DivideSigned, 255, 2, 127),
        (Binary::RemainderSigned, 255, 2, 1),
        (Binary::DivideUnsigned, 255, 2, 127),
        (Binary::RemainderUnsigned, 255, 2, 1),
        (Binary::ShiftRightSigned, 255, 1, 127),
        (Binary::ShiftRightUnsigned, 255, 8, 0),
        (Binary::ShiftRightSigned, 255, 31, 0),
        (
            Binary::ShiftLeft,
            1,
            40,
            if ty == Type::I64 { 1 << 40 } else { 256 },
        ),
    ] {
        let mut f = normalized(Op::Binary {
            op,
            left: 3,
            right: 4,
        });
        if matches!(
            op,
            Binary::ShiftLeft | Binary::ShiftRightSigned | Binary::ShiftRightUnsigned
        ) {
            f.blocks[0].instructions[1].result = Some((4, Type::I32));
        }
        cases.push((f, vec![left, right], Ok(expected)));
    }
    for (op, expected) in [(Op::Negate(3), mask - 254), (Op::Not(3), mask ^ 255)] {
        cases.push((normalized(op), vec![255, 0], Ok(expected)));
    }
    for (comparison, left, right, signed, expected) in [
        (Comparison::LessSigned, 255, 1, false, 0),
        (Comparison::GreaterSigned, 128, 1, false, 1),
        (Comparison::LessSigned, mask, 1, true, 1),
        (Comparison::Equal, 255, 255, false, 1),
    ] {
        let mut f = normalized(Op::Compare {
            op: comparison,
            left: 3,
            right: 4,
        });
        f.blocks[0].instructions[0].op = Op::Convert {
            value: 0,
            bits: 8,
            signed,
        };
        f.blocks[0].instructions[2].result = Some((2, Type::I32));
        f.returns = Some(Type::I32);
        cases.push((f, vec![left, right], Ok(expected)));
    }
    let mut f = normalized(Op::Binary {
        op: Binary::Multiply,
        left: 3,
        right: 5,
    });
    f.blocks[0].instructions.insert(
        2,
        Instruction {
            result: Some((5, ty)),
            op: Op::Constant(4),
            il_offset: 3,
        },
    );
    cases.push((f, vec![255, 0], Ok(1020)));
    for signed in [false, true] {
        let mut f = normalized(Op::Convert {
            value: 3,
            bits: 16,
            signed,
        });
        f.blocks[0].instructions[2].result = Some((2, Type::Native));
        f.returns = Some(Type::Native);
        cases.push((f, vec![255, 0], Ok(255)));
    }
    let mut f = normalized(Op::Convert {
        value: 3,
        bits: 8,
        signed: true,
    });
    cases.push((f.clone(), vec![255, 0], Ok(mask)));
    f.blocks[0].instructions[0].op = Op::Convert {
        value: 0,
        bits: 16,
        signed: false,
    };
    f.blocks[0].instructions[2].op = Op::Binary {
        op: Binary::ShiftRightSigned,
        left: 3,
        right: 4,
    };
    f.blocks[0].instructions[1].result = Some((4, Type::I32));
    cases.push((f, vec![65535, 1], Ok(32767)));
    run(host, &cases);
}

#[rstest::rstest]
#[case(Scalar::U8, 256, 255, 0)]
#[case(Scalar::U16, 65536, 65535, 8)]
fn partial_local_aliases_preserve_high_bits_and_carry(
    host: &Host,
    #[case] scalar: Scalar,
    #[case] initial: u64,
    #[case] rewritten: u64,
    #[case] shift: u64,
) {
    let mut f = function(Type::I32, Op::Constant(0));
    f.parameters.clear();
    f.locals = vec![Type::I32];
    f.blocks[0].parameters.clear();
    let instruction = |id, ty, op| Instruction {
        result: Some((id, ty)),
        op,
        il_offset: id,
    };
    f.blocks[0].instructions = vec![
        instruction(0, Type::I32, Op::Constant(initial)),
        Instruction {
            result: None,
            op: Op::StoreLocal { local: 0, value: 0 },
            il_offset: 0,
        },
        instruction(
            1,
            Type::ByRef(scalar),
            Op::LocalAddress {
                local: 0,
                storage: FieldStorage::Value(Storage::Scalar(scalar)),
            },
        ),
        instruction(2, Type::I32, Op::Constant(rewritten)),
        Instruction {
            result: None,
            op: Op::StoreMemory {
                address: 1,
                value: 2,
                bits: u16::from(scalar.bits()),
            },
            il_offset: 1,
        },
        instruction(3, Type::I32, Op::LoadLocal(0)),
        instruction(4, Type::I32, Op::Constant(shift)),
        instruction(
            5,
            Type::I32,
            Op::Binary {
                op: Binary::ShiftRightUnsigned,
                left: 3,
                right: 4,
            },
        ),
        instruction(6, Type::I32, Op::Constant(0xfeff)),
        instruction(
            7,
            Type::I32,
            Op::Binary {
                op: Binary::Add,
                left: 5,
                right: 6,
            },
        ),
    ];
    f.blocks[0].terminator.kind = Control::Return(Some(7));
    run(host, &[(f, vec![], Ok(0x100fe))]);
}

#[rstest::rstest]
#[case(Type::I32)]
#[case(Type::I64)]
fn small_division_cycle_cost(host: &Host, #[case] ty: Type) {
    let f = function(
        ty,
        Op::Binary {
            op: Binary::DivideSigned,
            left: 0,
            right: 1,
        },
    );
    let directory = tempfile::tempdir_in(host.directory.path()).unwrap();
    let path = directory.path().join("division.gb");
    std::fs::write(&path, rom(&f, &[30000, 31], 0xd000)).unwrap();
    let output = dotnet()
        .arg(&host.dll)
        .arg("--cost")
        .arg(path)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let stdout = String::from_utf8(output.stdout).unwrap();
    let fields: Vec<_> = stdout.split_whitespace().collect();
    assert_eq!(fields[0], "A5", "{stdout}");
    assert_eq!(fields[2], "C703000000000000", "{stdout}");
    assert_eq!(fields[3], "D000", "{stdout}");
    assert_eq!(fields[4], "OK", "{stdout}");
    let cycles: u64 = fields[5].parse().unwrap();
    println!("{ty:?}: {cycles} T-cycles");
    assert!(
        cycles < 20000,
        "small {ty:?} division costs {cycles} T-cycles; budget 20000"
    );
}

#[rstest::rstest]
#[case(Type::Native, 0)]
#[case(Type::Native, 20)]
#[case(Type::I32, 0)]
#[case(Type::I32, 20)]
#[case(Type::I64, 0)]
#[case(Type::I64, 20)]
fn division_word_boundaries_execute(host: &Host, #[case] ty: Type, #[case] locals: usize) {
    let mask = if ty == Type::I64 {
        u64::MAX
    } else {
        (1u64 << ty.bits()) - 1
    };
    let mut cases = Vec::new();
    for (left, right) in [
        (0, 1),
        (1, 65535),
        (32768, 1),
        (65535, 32768),
        (65535, 65534),
        (65535, 255),
        (65535, 1),
        (65536, 31),
        (131071, 65536),
    ] {
        if left > mask || right > mask {
            continue;
        }
        for (op, expected) in [
            (Binary::DivideUnsigned, left / right),
            (Binary::RemainderUnsigned, left % right),
        ] {
            let mut f = function(
                ty,
                Op::Binary {
                    op,
                    left: 0,
                    right: 1,
                },
            );
            f.locals = vec![Type::I64; locals];
            cases.push((f, vec![left, right], Ok(expected)));
        }
    }
    for (left, right) in [
        (-32768i64, -1i64),
        (-30000, 31),
        (30000, -31),
        (-30000, -31),
    ] {
        if ty == Type::Native && left == -32768 && right == -1 {
            continue;
        }
        for (op, expected) in [
            (Binary::DivideSigned, left / right),
            (Binary::RemainderSigned, left % right),
        ] {
            let mut f = function(
                ty,
                Op::Binary {
                    op,
                    left: 0,
                    right: 1,
                },
            );
            f.locals = vec![Type::I64; locals];
            cases.push((
                f,
                vec![left as u64 & mask, right as u64 & mask],
                Ok(expected as u64 & mask),
            ));
        }
    }
    run(host, &cases);
}

#[rstest::rstest]
fn division_boundaries_and_faults_execute(host: &Host) {
    let mut cases = Vec::new();
    for (ty, mask, sign) in [
        (Type::I32, 0xffff_ffff, 0x8000_0000),
        (Type::I64, u64::MAX, 0x8000_0000_0000_0000),
        (Type::Native, 0xffff, 0x8000),
    ] {
        for (op, left, right, expected) in [
            (Binary::Multiply, mask, mask, Ok(1)),
            (Binary::DivideUnsigned, mask, sign + 3, Ok(1)),
            (Binary::RemainderUnsigned, mask, sign + 3, Ok(sign - 4)),
            (Binary::DivideSigned, mask - 6, mask - 2, Ok(2)),
            (Binary::RemainderSigned, mask - 6, mask - 2, Ok(mask)),
            (Binary::DivideSigned, 7, mask - 2, Ok(mask - 1)),
            (Binary::RemainderSigned, 7, mask - 2, Ok(1)),
            (Binary::DivideSigned, sign, 1, Ok(sign)),
            (Binary::RemainderSigned, sign, mask, Ok(0)),
            (Binary::DivideSigned, sign, mask, Err(2)),
            (Binary::DivideUnsigned, 1, 0, Err(2)),
            (Binary::RemainderUnsigned, 1, 0, Err(2)),
        ] {
            cases.push((
                function(
                    ty,
                    Op::Binary {
                        op,
                        left: 0,
                        right: 1,
                    },
                ),
                vec![left, right],
                expected,
            ));
        }
    }
    run(host, &cases);
}

#[rstest::rstest]
fn cil_switch_executes_full_width_selectors_and_stack_edges(host: &Host) {
    let il = [
        0x1f, 40, 0x02, 0x45, 3, 0, 0, 0, 4, 0, 0, 0, 7, 0, 0, 0, 10, 0, 0, 0, 0x1f, 9, 0x58, 0x2a,
        0x17, 0x58, 0x2a, 0x18, 0x58, 0x2a, 0x19, 0x58, 0x2a,
    ];
    let mut f = koh_compile::lower::lower(koh_compile::lower::Method {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        il: &il,
        parameters: vec![Type::I32],
        returns: Some(Type::I32),
        locals: vec![],
        init_locals: true,
        max_stack: 2,
    })
    .unwrap();
    koh_compile::optimize::simplify(&mut f).unwrap();
    let mut cases: Vec<_> = [0, 1, 2, 3, 256, 65536, 0x80000000, 0xffffffff]
        .into_iter()
        .map(|selector| {
            (
                f.clone(),
                vec![selector],
                Ok(if selector < 3 { 41 + selector } else { 49 }),
            )
        })
        .collect();
    let mut duplicate = il;
    duplicate[16] = 7;
    let empty = [0x1f, 40, 0x02, 0x45, 0, 0, 0, 0, 0x2a];
    let mut loop_il = vec![0x16, 0x0a, 0x06, 0x17, 0x58, 0x0a, 0x06, 0x45, 4, 0, 0, 0];
    for displacement in [-26i32, -26, -26, 0] {
        loop_il.extend_from_slice(&displacement.to_le_bytes());
    }
    loop_il.extend_from_slice(&[0x06, 0x2a]);
    for (il, parameters, locals, arguments, expected) in [
        (duplicate.as_slice(), vec![Type::I32], vec![], vec![2], 42),
        (
            empty.as_slice(),
            vec![Type::I32],
            vec![],
            vec![0xffffffff],
            40,
        ),
        (loop_il.as_slice(), vec![], vec![Type::I32], vec![], 3),
    ] {
        let mut f = koh_compile::lower::lower(koh_compile::lower::Method {
            identity: Definition {
                assembly: 0,
                token: 0x06000001,
            },
            il,
            parameters,
            returns: Some(Type::I32),
            locals,
            init_locals: true,
            max_stack: 2,
        })
        .unwrap();
        koh_compile::optimize::simplify(&mut f).unwrap();
        cases.push((f, arguments, Ok(expected)));
    }
    run(host, &cases);
}

#[rstest::rstest]
fn stack_capacity_is_checked_before_frame_writes(host: &Host) {
    let f = function(
        Type::I32,
        Op::Binary {
            op: Binary::Add,
            left: 0,
            right: 1,
        },
    );
    run_at(host, &[(f.clone(), vec![1, 2], Err(1))], 0xc120);
    let mut multiply = f.clone();
    multiply.blocks[0].instructions[0].op = Op::Binary {
        op: Binary::Multiply,
        left: 0,
        right: 1,
    };
    run_at(host, &[(multiply, vec![30, 127], Err(1))], 0xc120);
    let mut huge = f;
    huge.parameters = vec![Type::I64; 1000];
    huge.blocks[0].parameters = (0..1000).map(|id| (id, Type::I64)).collect();
    huge.blocks[0].instructions.clear();
    huge.returns = Some(Type::I64);
    huge.blocks[0].terminator.kind = Control::Return(Some(0));
    assert!(sm83::emit(&huge).unwrap_err().contains("capacity"));
}

#[rstest::rstest]
fn pinned_native_clear_checks_zero_before_fabricating_a_reference(host: &Host) {
    let f = Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![Type::Native],
        returns: Some(Type::I32),
        locals: vec![Type::ByRef(Scalar::U8)],
        blocks: vec![Block {
            parameters: vec![(0, Type::Native)],
            instructions: vec![
                Instruction {
                    result: Some((1, Type::ByRef(Scalar::U8))),
                    op: Op::Constant(0),
                    il_offset: 0,
                },
                Instruction {
                    result: None,
                    op: Op::StoreLocal { local: 0, value: 1 },
                    il_offset: 0,
                },
                Instruction {
                    result: None,
                    op: Op::StorePinned { local: 0, value: 0 },
                    il_offset: 1,
                },
                Instruction {
                    result: Some((2, Type::I32)),
                    op: Op::Constant(42),
                    il_offset: 2,
                },
            ],
            terminator: Terminator {
                il_offset: 3,
                kind: Control::Return(Some(2)),
            },
        }],
    };
    let cases: Vec<_> = [0, 1, 0xc100, 0xd000, 0xffff]
        .into_iter()
        .map(|value| {
            (
                f.clone(),
                vec![value],
                if value == 0 { Ok(42) } else { Err(8) },
            )
        })
        .collect();
    run(host, &cases);
}

#[rstest::rstest]
#[case(false, false)]
#[case(false, true)]
#[case(true, false)]
#[case(true, true)]
fn short_and_wide_local_argument_address_encodings_execute(
    host: &Host,
    #[case] argument: bool,
    #[case] wide: bool,
) {
    let mut il = if wide {
        vec![0xfe, if argument { 0x0a } else { 0x0d }, 0, 0]
    } else {
        vec![if argument { 0x0f } else { 0x12 }, 0]
    };
    il.extend_from_slice(&[0x1f, 42, 0x54, if argument { 0x02 } else { 0x06 }, 0x2a]);
    let f = koh_compile::lower::lower(koh_compile::lower::Method {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        il: &il,
        parameters: if argument { vec![Type::I32] } else { vec![] },
        returns: Some(Type::I32),
        locals: if argument { vec![] } else { vec![Type::I32] },
        init_locals: true,
        max_stack: 2,
    })
    .unwrap();
    run(
        host,
        &[(f, if argument { vec![7] } else { vec![] }, Ok(42))],
    );
}

#[rstest::rstest]
#[case(false)]
#[case(true)]
fn null_managed_references_trap_but_native_bus_zero_is_valid(host: &Host, #[case] store: bool) {
    let mut cases = Vec::new();
    for ty in [Type::Native, Type::ByRef(Scalar::I32)] {
        let mut f = function(Type::I32, Op::Constant(0));
        f.parameters[0] = ty;
        f.blocks[0].parameters[0].1 = ty;
        f.blocks[0].instructions = vec![Instruction {
            result: if store { None } else { Some((2, Type::I32)) },
            op: if store {
                Op::StoreMemory {
                    address: 0,
                    value: 1,
                    bits: 32,
                }
            } else {
                Op::LoadMemory {
                    address: 0,
                    bits: 32,
                    signed: false,
                }
            },
            il_offset: 0,
        }];
        f.blocks[0].terminator.kind = Control::Return(Some(if store { 1 } else { 2 }));
        let expected = if matches!(ty, Type::ByRef(_)) {
            Err(4)
        } else {
            Ok(if store { 42 } else { 0 })
        };
        cases.push((f, vec![0, 42], expected));
    }
    run(host, &cases);
}

#[rstest::rstest]
fn indirect_memory_respects_the_last_stack_scratch_word(host: &Host) {
    let f = Function {
        identity: Definition {
            assembly: 0,
            token: 0x06000001,
        },
        parameters: vec![Type::Native, Type::I32],
        returns: Some(Type::I32),
        locals: vec![],
        blocks: vec![Block {
            parameters: vec![(0, Type::Native), (1, Type::I32)],
            instructions: vec![
                Instruction {
                    result: None,
                    op: Op::StoreMemory {
                        address: 0,
                        value: 1,
                        bits: 32,
                    },
                    il_offset: 0,
                },
                Instruction {
                    result: Some((2, Type::I32)),
                    op: Op::LoadMemory {
                        address: 0,
                        bits: 32,
                        signed: false,
                    },
                    il_offset: 1,
                },
            ],
            terminator: Terminator {
                kind: Control::Return(Some(2)),
                il_offset: 2,
            },
        }],
    };
    run_at(host, &[(f.clone(), vec![0xc020, 42], Ok(42))], 0xc137);
    run_at(host, &[(f, vec![0xc020, 42], Err(1))], 0xc136);
}

#[rstest::rstest]
fn cyclic_block_arguments_transfer_simultaneously(host: &Host) {
    let mut f = function(Type::I32, Op::Constant(0));
    f.blocks[0].terminator.kind = Control::Jump(Edge {
        block: 1,
        arguments: vec![0, 1, 2],
    });
    f.blocks.push(Block {
        parameters: vec![(3, Type::I32), (4, Type::I32), (5, Type::I32)],
        instructions: vec![
            Instruction {
                result: Some((6, Type::I32)),
                op: Op::Constant(1),
                il_offset: 1,
            },
            Instruction {
                result: Some((7, Type::I32)),
                op: Op::Binary {
                    op: Binary::Add,
                    left: 5,
                    right: 6,
                },
                il_offset: 2,
            },
            Instruction {
                result: Some((8, Type::I32)),
                op: Op::Constant(3),
                il_offset: 3,
            },
            Instruction {
                result: Some((9, Type::I32)),
                op: Op::Compare {
                    op: Comparison::LessSigned,
                    left: 5,
                    right: 8,
                },
                il_offset: 4,
            },
        ],
        terminator: Terminator {
            kind: Control::Branch {
                condition: 9,
                yes: Edge {
                    block: 1,
                    arguments: vec![4, 3, 7],
                },
                no: Edge {
                    block: 2,
                    arguments: vec![3],
                },
            },
            il_offset: 5,
        },
    });
    f.blocks.push(Block {
        parameters: vec![(10, Type::I32)],
        instructions: vec![],
        terminator: Terminator {
            kind: Control::Return(Some(10)),
            il_offset: 6,
        },
    });
    run(host, &[(f, vec![11, 22], Ok(22))]);
}

#[test]
fn elf_retains_method_and_il_offset_symbols() {
    let mut f = function(
        Type::I32,
        Op::Binary {
            op: Binary::Add,
            left: 0,
            right: 1,
        },
    );
    f.blocks[0].instructions[0].il_offset = 0x37;
    f.blocks[0].terminator.il_offset = 0x52;
    let object = read_elf(&write_elf(&sm83::emit(&f).unwrap()).unwrap()).unwrap();
    assert!(
        object
            .symbols
            .iter()
            .any(|s| s.name == sm83::symbol(f.identity))
    );
    for marker in [".IL_0037.", ".IL_0052."] {
        assert!(
            object.symbols.iter().any(|s| s.name.contains(marker)),
            "Missing {marker}"
        );
    }
}

#[test]
fn maximum_ir_offset_does_not_panic_during_emission() {
    let mut f = function(
        Type::I32,
        Op::Compare {
            op: Comparison::Equal,
            left: 0,
            right: 1,
        },
    );
    f.blocks[0].instructions[0].il_offset = u32::MAX;
    let result = std::panic::catch_unwind(|| sm83::emit(&f));
    assert!(result.is_ok());
    result.unwrap().unwrap();
}

#[test]
fn oversized_machine_code_is_diagnostic() {
    let mut f = function(Type::I64, Op::Constant(0));
    f.locals = vec![Type::I64];
    f.blocks[0].instructions = (0..20_000)
        .map(|offset| Instruction {
            result: None,
            op: Op::StoreLocal { local: 0, value: 0 },
            il_offset: offset,
        })
        .collect();
    f.blocks[0].terminator.kind = Control::Return(Some(0));
    assert!(sm83::emit(&f).unwrap_err().contains("16 KiB"));
}

#[rstest::rstest]
#[case("Debug")]
#[case("Release")]
fn roslyn_reachable_calls_and_recursion_execute(host: &Host, #[case] configuration: &str) {
    let directory = host.directory.path().join(format!("calls-{configuration}"));
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/Calls");
    for project in ["Entry", "Bridge", "Runtime"] {
        std::fs::create_dir_all(directory.join(project)).unwrap();
        for file in std::fs::read_dir(fixture.join(project)).unwrap() {
            let file = file.unwrap();
            std::fs::copy(file.path(), directory.join(project).join(file.file_name())).unwrap();
        }
    }
    let output = dotnet()
        .arg("build")
        .arg(directory.join("Entry/Entry.csproj"))
        .args(["-c", configuration, "--nologo", "-v:q", "-m:1"])
        .arg("-p:DisableTransitiveProjectReferences=true")
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let images: Vec<_> = [
        ("Entry", "Game.Calls"),
        ("Bridge", "Game.Bridge"),
        ("Runtime", "Game.Runtime"),
    ]
    .iter()
    .map(|(project, assembly)| {
        std::fs::read(directory.join(format!(
            "{project}/bin/{configuration}/net10.0/{assembly}.dll"
        )))
        .unwrap()
    })
    .collect();
    let views: Vec<_> = images.iter().map(Vec::as_slice).collect();
    let resolver = koh_compile::resolve::Resolver::new(&views).unwrap();
    let assembly = resolver.assembly(0).unwrap();
    let token = |name| {
        assembly
            .metadata()
            .method_defs
            .iter()
            .enumerate()
            .find(|(_, row)| assembly.metadata().strings.get(row.name).unwrap() == name)
            .map(|(i, _)| 0x06000001 + i as u32)
            .unwrap()
    };
    let mut cases = Vec::new();
    for (name, expected) in [
        ("Main", Ok(42)),
        ("Wide", Ok(0x123456789ABCDEF9)),
        ("Live", Ok(0x112233445566778B)),
        ("Mutual", Ok(1)),
        ("VoidCalls", Ok(9)),
        ("Deep", Err(1)),
    ] {
        let entry = Definition {
            assembly: 0,
            token: token(name),
        };
        let functions = koh_compile::compile::reachable(&resolver, entry).unwrap();
        if name == "Main" {
            let mut names: Vec<_> = functions
                .iter()
                .map(|f| {
                    resolver
                        .assembly(f.identity.assembly)
                        .unwrap()
                        .method_name(f.identity.token)
                        .unwrap()
                })
                .collect();
            names.sort();
            assert_eq!(
                names,
                [
                    "Game::Play",
                    "Program::Main",
                    "Runtime::Answer",
                    "Runtime::Sum"
                ]
            );
        }
        cases.push((rom_program(&functions, entry, &[], 0xd000), expected));
    }
    run_roms(host, &cases, 0xd000);
    for (name, diagnostic) in [
        ("Dead", "System.Runtime"),
        ("DeadThrow", "opcode"),
        ("Generic", "Generic"),
        ("Protected", "sections"),
        ("StaticInit", ".cctor"),
    ] {
        let error = koh_compile::compile::reachable(
            &resolver,
            Definition {
                assembly: 0,
                token: token(name),
            },
        )
        .unwrap_err();
        assert!(
            error.contains(diagnostic) && error.contains("IL_"),
            "{name}: {error}"
        );
    }
    let missing = koh_compile::resolve::Resolver::new(&views[..2]).unwrap();
    let error = koh_compile::compile::reachable(
        &missing,
        Definition {
            assembly: 0,
            token: assembly.entry_point,
        },
    )
    .unwrap_err();
    assert!(
        error.contains("Game.Runtime") && error.contains("IL_"),
        "{error}"
    );
    let module_output = directory.join("module-initializer");
    let output = dotnet()
        .arg("build")
        .arg(directory.join("Entry/Entry.csproj"))
        .args([
            "-c",
            configuration,
            "--nologo",
            "-v:q",
            "-m:1",
            "-p:DefineConstants=MODULE_INIT",
            "-p:DisableTransitiveProjectReferences=true",
            "-o",
        ])
        .arg(&module_output)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    let module_bytes = std::fs::read(module_output.join("Game.Calls.dll")).unwrap();
    let mut module_views = views;
    module_views[0] = &module_bytes;
    let module = koh_compile::resolve::Resolver::new(&module_views).unwrap();
    let error = koh_compile::compile::reachable(
        &module,
        Definition {
            assembly: 0,
            token: module.assembly(0).unwrap().entry_point,
        },
    )
    .unwrap_err();
    assert!(
        error.contains("InitModule") && error.contains("opcode") && error.contains("IL_"),
        "{error}"
    );
}
