use super::*;

fn section(name: &str, kind: u8, size: u32) -> Section {
    Section {
        name: name.into(),
        kind,
        size,
        data: if kind < 2 {
            vec![0; size as usize]
        } else {
            vec![]
        },
        ..Default::default()
    }
}
fn source() -> Provenance {
    Provenance {
        file: "test.asm".into(),
        line: 1,
        column: 1,
        expansion: vec![],
    }
}
fn symbol(name: &str, binding: SymbolBinding, definition: SymbolDefinition) -> Symbol {
    Symbol {
        name: name.into(),
        binding,
        definition,
        source: Some(source()),
    }
}
fn raw(size: usize) -> LinkOptions {
    LinkOptions {
        raw: true,
        size: Some(size),
        ..Default::default()
    }
}
fn patch(expression: RelocationExpr, width: PatchWidth, semantics: PatchSemantics) -> Patch {
    Patch {
        offset: 0,
        width,
        semantics,
        pc: AddressContext {
            section: SectionId(0),
            offset: 0,
        },
        expression,
        source: source(),
    }
}

#[test]
fn import_resolves_export_not_same_named_local() {
    let a = Object {
        sections: vec![section("a", 0, 2)],
        symbols: vec![symbol(
            "target",
            SymbolBinding::Local,
            SymbolDefinition::Import,
        )],
    };
    let mut a = a;
    a.sections[0].patches.push(patch(
        RelocationExpr::Symbol(SymbolId(0)),
        PatchWidth::Word,
        PatchSemantics::Absolute,
    ));
    let b = Object {
        sections: vec![section("b", 0, 1)],
        symbols: vec![symbol(
            "target",
            SymbolBinding::Exported,
            SymbolDefinition::Label {
                section: SectionId(0),
                offset: 0,
            },
        )],
    };
    let c = Object {
        sections: vec![],
        symbols: vec![symbol(
            "target",
            SymbolBinding::Local,
            SymbolDefinition::Absolute(999),
        )],
    };
    let r = link(&[a, b, c], &raw(16)).unwrap();
    assert_eq!(&r.rom[..2], &[2, 0]);
}
#[test]
fn undefined_and_duplicate_exports_are_errors() {
    let o = Object {
        sections: vec![],
        symbols: vec![symbol(
            "missing",
            SymbolBinding::Local,
            SymbolDefinition::Import,
        )],
    };
    assert!(link(&[o], &raw(1)).unwrap_err().contains("Undefined"));
    let o = Object {
        sections: vec![],
        symbols: vec![symbol(
            "same",
            SymbolBinding::Exported,
            SymbolDefinition::Absolute(1),
        )],
    };
    assert!(
        link(&[o.clone(), o], &raw(1))
            .unwrap_err()
            .contains("Duplicate exported")
    );
}
#[test]
fn all_memory_regions_obey_bank_and_alignment() {
    for kind in 0..=7 {
        let (start, _, first, _) = memory_region(kind).unwrap();
        let mut s = section("s", kind, 1);
        s.align = 4;
        s.align_offset = 3;
        let o = Object {
            sections: vec![s],
            symbols: vec![symbol(
                "label",
                SymbolBinding::Local,
                SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: 0,
                },
            )],
        };
        let r = link(&[o], &LinkOptions::default()).unwrap();
        assert_eq!(r.symbols[0].value, ((start + 15) & !15) as i32 + 3);
        assert_eq!(r.symbols[0].bank, Some(first as u32));
    }
}
#[test]
fn fixed_reservations_precede_floating_and_overlaps_fail() {
    let mut fixed = section("fixed", 0, 4);
    fixed.address = Some(0);
    let floating = section("floating", 0, 2);
    let o = Object {
        sections: vec![floating, fixed.clone()],
        symbols: vec![symbol(
            "float",
            SymbolBinding::Local,
            SymbolDefinition::Label {
                section: SectionId(0),
                offset: 0,
            },
        )],
    };
    assert_eq!(link(&[o], &raw(6)).unwrap().symbols[0].value, 4);
    let mut second = fixed.clone();
    second.name = "second".into();
    assert!(
        link(
            &[Object {
                sections: vec![fixed, second],
                symbols: vec![]
            }],
            &raw(8)
        )
        .is_err()
    );
}
#[test]
fn fragments_append_and_adjust_constraints_symbols_and_relocations() {
    let mut a = section("fragment", 0, 2);
    a.combination = SectionCombination::Fragment;
    a.data = vec![1, 2];
    a.address = Some(0x100);
    let mut b = section("fragment", 0, 2);
    b.combination = SectionCombination::Fragment;
    b.address = Some(0x102);
    b.patches.push(patch(
        RelocationExpr::CurrentAddress,
        PatchWidth::Word,
        PatchSemantics::Absolute,
    ));
    let objects = [
        Object {
            sections: vec![a],
            symbols: vec![],
        },
        Object {
            sections: vec![b],
            symbols: vec![symbol(
                "b",
                SymbolBinding::Local,
                SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: 0,
                },
            )],
        },
    ];
    let r = link(&objects, &raw(0x104)).unwrap();
    assert_eq!(&r.rom[0x100..], &[1, 2, 2, 1]);
    assert_eq!(r.symbols[0].value, 0x102);
}
#[test]
fn union_uses_maximum_size_and_shared_start() {
    let mut a = section("union", 4, 3);
    a.combination = SectionCombination::Union;
    let mut b = a.clone();
    b.size = 8;
    let c = section("after", 4, 1);
    let o = Object {
        sections: vec![a, b, c],
        symbols: vec![
            symbol(
                "a",
                SymbolBinding::Local,
                SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: 0,
                },
            ),
            symbol(
                "b",
                SymbolBinding::Local,
                SymbolDefinition::Label {
                    section: SectionId(1),
                    offset: 0,
                },
            ),
            symbol(
                "c",
                SymbolBinding::Local,
                SymbolDefinition::Label {
                    section: SectionId(2),
                    offset: 0,
                },
            ),
        ],
    };
    let r = link(&[o], &raw(1)).unwrap();
    assert_eq!(r.symbols[0].value, 0xc000);
    assert_eq!(r.symbols[1].value, 0xc000);
    assert_eq!(r.symbols[2].value, 0xc008);
}
#[test]
fn raw_is_exact_and_preserves_header_bytes() {
    let mut s = section("boot", 0, 0x900);
    s.data[0x143..0x150].fill(0x65);
    let r = link(
        &[Object {
            sections: vec![s.clone()],
            symbols: vec![],
        }],
        &raw(0x900),
    )
    .unwrap();
    assert_eq!(r.rom.len(), 0x900);
    assert!(r.rom[0x143..0x150].iter().all(|b| *b == 0x65));
    assert!(
        link(
            &[Object {
                sections: vec![s],
                symbols: vec![]
            }],
            &raw(0x100)
        )
        .is_err()
    );
}
#[test]
fn cartridge_rounds_sets_cgb_and_both_checksums() {
    let mut s = section("bank", 1, 1);
    s.bank = Some(2);
    let r = link(
        &[Object {
            sections: vec![s],
            symbols: vec![],
        }],
        &LinkOptions {
            cgb: CgbMode::Only,
            ..Default::default()
        },
    )
    .unwrap();
    assert_eq!(r.rom.len(), 0x10000);
    assert_eq!(r.rom[0x143], 0xc0);
    assert_eq!(r.rom[0x148], 1);
    assert_eq!(
        r.rom[0x14d],
        r.rom[0x134..=0x14c]
            .iter()
            .fold(0u8, |a, b| a.wrapping_sub(*b).wrapping_sub(1))
    );
    let sum = r
        .rom
        .iter()
        .enumerate()
        .filter(|(i, _)| *i != 0x14e && *i != 0x14f)
        .fold(0u16, |a, (_, b)| a.wrapping_add(*b as u16));
    assert_eq!(&r.rom[0x14e..0x150], &sum.to_be_bytes());
}

#[test]
fn cartridge_supplies_boot_logo_while_raw_preserves_input() {
    let mut header = section("header", 0, 0x150);
    header.address = Some(0);
    header.data.fill(0xa5);
    let object = Object {
        sections: vec![header],
        symbols: vec![],
    };
    let rom = link(&[object.clone()], &LinkOptions::default())
        .unwrap()
        .rom;
    let logo = [
        0xce, 0xed, 0x66, 0x66, 0xcc, 0x0d, 0, 0x0b, 3, 0x73, 0, 0x83, 0, 0x0c, 0, 0x0d, 0, 8,
        0x11, 0x1f, 0x88, 0x89, 0, 0x0e, 0xdc, 0xcc, 0x6e, 0xe6, 0xdd, 0xdd, 0xd9, 0x99, 0xbb,
        0xbb, 0x67, 0x63, 0x6e, 0x0e, 0xec, 0xcc, 0xdd, 0xdc, 0x99, 0x9f, 0xbb, 0xb9, 0x33, 0x3e,
    ];
    assert_eq!(&rom[0x104..0x134], &logo);
    assert_eq!(
        &link(&[object], &raw(0x150)).unwrap().rom,
        &vec![0xa5; 0x150]
    );
}
#[test]
fn expression_arithmetic_matches_rgbds_floor_and_wrapping() {
    use BinaryOp::*;
    for (op, a, b, expected) in [
        (Divide, -7, 3, -3),
        (Modulo, -7, 3, 2),
        (Divide, 7, -3, -3),
        (Modulo, 7, -3, -2),
        (Add, i32::MAX, 1, i32::MIN),
        (Multiply, i32::MAX, 2, -2),
        (Divide, i32::MIN, -1, i32::MIN),
        (Power, 2, 31, i32::MIN),
        (ShiftLeft, 1, 32, 0),
        (ShiftRight, -1, 32, -1),
        (UnsignedShiftRight, -1, 1, i32::MAX),
        (ShiftLeft, 8, -1, 4),
    ] {
        assert_eq!(binary(op, a, b).unwrap(), expected);
    }
    assert!(binary(Divide, 1, 0).is_err());
    assert!(binary(Power, 2, -1).is_err());
}
#[test]
fn patches_reject_out_of_range_and_accept_boundaries() {
    let mut p = patch(
        RelocationExpr::Literal(0),
        PatchWidth::Byte,
        PatchSemantics::Absolute,
    );
    assert_eq!(patch_value(255, &p, 0).unwrap(), 255);
    assert_eq!(patch_value(-128, &p, 0).unwrap(), -128);
    assert!(patch_value(256, &p, 0).is_err());
    assert!(patch_value(-129, &p, 0).is_err());
    p.semantics = PatchSemantics::PcRelative { bias: 2 };
    assert_eq!(patch_value(129, &p, 0).unwrap(), 127);
    assert!(patch_value(130, &p, 0).is_err());
    p.semantics = PatchSemantics::HighMemory;
    assert_eq!(patch_value(0xff80, &p, 0).unwrap(), 128);
    assert!(patch_value(0xfeff, &p, 0).is_err());
}

#[test]
fn pc_relative_wraps_cpu_address_before_range_check() {
    let p = patch(
        RelocationExpr::Literal(0),
        PatchWidth::Byte,
        PatchSemantics::PcRelative { bias: 2 },
    );
    assert_eq!(patch_value(0, &p, 0xfffd).unwrap(), 1);
    assert_eq!(patch_value(0xffff, &p, 0).unwrap(), -3);
    assert_eq!(patch_value(-1, &p, 0).unwrap(), -3);
    assert_eq!(patch_value(0x7e, &p, 0xfffd).unwrap(), 127);
    assert!(patch_value(0x7f, &p, 0xfffd).is_err());
    assert_eq!(patch_value(0xff82, &p, 0).unwrap(), -128);
    assert!(patch_value(0xff81, &p, 0).is_err());
    assert!(
        patch_value(65536, &p, 0xfffd)
            .unwrap_err()
            .contains("16-bit")
    );
    assert!(patch_value(-32769, &p, 0).unwrap_err().contains("16-bit"));
}

#[test]
fn load_jr_wrap_matches_rgbds_101() {
    let mut storage = section("A", 0, 2);
    storage.data = vec![0x18, 0];
    let mut relocation = patch(
        RelocationExpr::Literal(0),
        PatchWidth::Byte,
        PatchSemantics::PcRelative { bias: 2 },
    );
    relocation.offset = 1;
    relocation.pc.section = SectionId(1);
    storage.patches.push(relocation);
    let mut execution = section("X", 7, 2);
    execution.address = Some(0xfffd);
    let result = link(
        &[Object {
            sections: vec![storage, execution],
            symbols: vec![],
        }],
        &raw(2),
    )
    .unwrap();
    assert_eq!(result.rom, vec![0x18, 1]);
    if std::env::var("KOH_RGBDS_TEST").as_deref() != Ok("1") {
        return;
    }
    let directory = std::env::temp_dir().join(format!("koh-link-jr-oracle-{}", std::process::id()));
    std::fs::create_dir_all(&directory).unwrap();
    let asm = directory.join("wrap.asm");
    let object = directory.join("wrap.o");
    let rom = directory.join("wrap.bin");
    std::fs::write(
        &asm,
        "SECTION \"A\",ROM0\nLOAD \"X\",HRAM[$fffd]\njr $0000\nENDL\n",
    )
    .unwrap();
    let output = std::process::Command::new("rgbasm")
        .arg("-o")
        .arg(&object)
        .arg(&asm)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let output = std::process::Command::new("rgblink")
        .arg("-x")
        .arg("-o")
        .arg(&rom)
        .arg(&object)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert_eq!(result.rom, std::fs::read(rom).unwrap());
    std::fs::remove_dir_all(directory).unwrap();
}
#[test]
fn dwarf_preserves_long_ranges_and_expansion_locations() {
    let mut r = link(&[], &raw(1)).unwrap();
    r.lines.push(LinkedLine {
        bank: 1,
        address: 0x4000,
        count: 600,
        file: "source.asm".into(),
        line: 4,
        expansion: vec![SourceLocation {
            file: "macro.asm".into(),
            line: 2,
            column: 1,
        }],
    });
    let b = r.dwarf().unwrap();
    assert_eq!(&b[..4], b"\x7fELF");
    let parsed = crate::read_dwarf(&b).unwrap();
    assert_eq!(parsed.lines.len(), 1);
    assert_eq!(parsed.lines[0].count, 600);
    assert_eq!(parsed.lines[0].expansion, r.lines[0].expansion);
}
#[test]
fn dwarf_rejects_unrepresentable_symbols() {
    let mut r = link(&[], &raw(1)).unwrap();
    r.symbols.push(LinkedSymbol {
        name: "wide".into(),
        value: 0x4000,
        bank: Some(65536),
        kind: 0,
        source: None,
    });
    assert!(r.dwarf().unwrap_err().contains("bank:16"));
    r.symbols[0].bank = None;
    r.symbols[0].value = -1;
    assert!(r.dwarf().unwrap_err().contains("Negative"));
}

#[test]
fn rgbds_101_expression_oracle() {
    if std::env::var("KOH_RGBDS_TEST").as_deref() != Ok("1") {
        return;
    }
    use BinaryOp::*;
    let cases = [
        ("-7 / 3", Divide, -7, 3),
        ("7 / -3", Divide, 7, -3),
        ("-7 % 3", Modulo, -7, 3),
        ("7 % -3", Modulo, 7, -3),
        ("$7fffffff + 1", Add, i32::MAX, 1),
        ("2 ** 31", Power, 2, 31),
        ("1 << 32", ShiftLeft, 1, 32),
        ("-1 >> 32", ShiftRight, -1, 32),
        ("-1 >>> 1", UnsignedShiftRight, -1, 1),
        ("8 << -1", ShiftLeft, 8, -1),
        ("8 >>> -1", UnsignedShiftRight, 8, -1),
    ];
    let directory = std::env::temp_dir().join(format!("koh-link-rgbds-{}", std::process::id()));
    std::fs::create_dir_all(&directory).unwrap();
    let asm = directory.join("oracle.asm");
    let obj = directory.join("oracle.o");
    let rom = directory.join("oracle.gb");
    let text = format!(
        "SECTION \"oracle\", ROM0[$0000]\n{}",
        cases
            .iter()
            .map(|(text, _, _, _)| format!("dl {text}\n"))
            .collect::<String>()
    );
    std::fs::write(&asm, text).unwrap();
    let result = std::process::Command::new("rgbasm")
        .arg("-o")
        .arg(&obj)
        .arg(&asm)
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    let result = std::process::Command::new("rgblink")
        .arg("-x")
        .arg("-o")
        .arg(&rom)
        .arg(&obj)
        .output()
        .unwrap();
    assert!(
        result.status.success(),
        "{}",
        String::from_utf8_lossy(&result.stderr)
    );
    let bytes = std::fs::read(rom).unwrap();
    for (i, (text, op, a, b)) in cases.into_iter().enumerate() {
        let expected = i32::from_le_bytes(bytes[i * 4..i * 4 + 4].try_into().unwrap());
        assert_eq!(binary(op, a, b).unwrap(), expected, "{text}");
    }
    std::fs::remove_dir_all(directory).unwrap();
}

#[test]
fn remaining_binary_operators_have_explicit_results() {
    use BinaryOp::*;
    for (op, expected) in [
        (Subtract, 3),
        (BitOr, 7),
        (BitAnd, 2),
        (BitXor, 5),
        (LogicalAnd, 1),
        (LogicalOr, 1),
        (Equal, 0),
        (NotEqual, 1),
        (Greater, 1),
        (Less, 0),
        (GreaterEqual, 1),
        (LessEqual, 0),
        (ShiftRight, 0),
    ] {
        assert_eq!(binary(op, 6, 3).unwrap(), expected, "{op:?}");
    }
}

#[test]
fn all_expression_queries_and_unary_operators_resolve() {
    let mut s = section("banked", 1, 4);
    s.address = Some(0x4100);
    s.bank = Some(3);
    let objects = [Object {
        sections: vec![s],
        symbols: vec![symbol(
            "target",
            SymbolBinding::Exported,
            SymbolDefinition::Label {
                section: SectionId(0),
                offset: 2,
            },
        )],
    }];
    let placements = [vec![Placement {
        address: 0x4100,
        bank: 3,
        group: 0,
    }]];
    let groups = [Group {
        name: "banked".into(),
        kind: 1,
        combination: SectionCombination::Normal,
        size: 4,
        address: Some(0x4100),
        bank: Some(3),
        align: 0,
        offset: 0,
    }];
    let located = [placements[0][0]];
    let context = Context {
        objects: &objects,
        placements: &placements,
        groups: &groups,
        located: &located,
        exports: HashMap::new(),
        section_names: HashMap::from([("banked".into(), 0)]),
    };
    let local = SectionRef::Local(SectionId(0));
    let external = SectionRef::External("banked".into());
    for (expression, expected) in [
        (RelocationExpr::Symbol(SymbolId(0)), 0x4102),
        (RelocationExpr::Bank(SymbolId(0)), 3),
        (RelocationExpr::CurrentAddress, 0x4101),
        (RelocationExpr::BankCurrent, 3),
        (RelocationExpr::BankSection(local.clone()), 3),
        (RelocationExpr::SectionSize(local), 4),
        (RelocationExpr::SectionStart(external.clone()), 0x4100),
        (RelocationExpr::SectionSize(external), 4),
        (RelocationExpr::SectionTypeSize(1), 0x4000),
        (RelocationExpr::SectionTypeStart(1), 0x4000),
    ] {
        assert_eq!(context.eval(0, &expression, 0x4101, 3).unwrap(), expected);
    }
    for (op, value, expected) in [
        (UnaryOp::Negate, 5, -5),
        (UnaryOp::BitNot, 0, -1),
        (UnaryOp::LogicalNot, 0, 1),
        (UnaryOp::High, 0xabcd, 0xab),
        (UnaryOp::Low, 0xabcd, 0xcd),
        (UnaryOp::BitWidth, 0, 0),
        (UnaryOp::BitWidth, -1, 32),
        (UnaryOp::TrailingZeros, 0, 32),
        (UnaryOp::TrailingZeros, 8, 3),
    ] {
        assert_eq!(
            context
                .eval(
                    0,
                    &RelocationExpr::Unary(op, Box::new(RelocationExpr::Literal(value))),
                    0,
                    0
                )
                .unwrap(),
            expected
        );
    }
    assert!(
        context
            .eval(
                0,
                &RelocationExpr::SectionSize(SectionRef::External("missing".into())),
                0,
                0
            )
            .is_err()
    );
}
