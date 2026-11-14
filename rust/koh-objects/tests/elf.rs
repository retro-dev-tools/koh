use koh_objects::*;
use object::{Object as _, ObjectSection as _, ObjectSymbol as _};
fn source() -> Provenance {
    Provenance {
        file: "日本語.asm".into(),
        line: 7,
        column: 3,
        expansion: vec![SourceLocation {
            file: "macro.asm".into(),
            line: 2,
            column: 1,
        }],
    }
}
fn object() -> Object {
    Object {
        sections: vec![
            Section {
                name: ".koh.meta".into(),
                size: 4,
                data: vec![0; 4],
                address: Some(0x100),
                lines: vec![Line {
                    expansion: source().expansion,
                    offset: 0,
                    count: 4,
                    file: "main.asm".into(),
                    line: 7,
                }],
                source: Some(source()),
                ..Default::default()
            },
            Section {
                name: "ram".into(),
                kind: 4,
                size: 32,
                align: 2,
                align_offset: 1,
                ..Default::default()
            },
        ],
        symbols: vec![
            Symbol {
                name: "Target".into(),
                binding: SymbolBinding::Exported,
                definition: SymbolDefinition::Label {
                    section: SectionId(1),
                    offset: 3,
                },
                source: Some(source()),
            },
            Symbol {
                name: "External".into(),
                binding: SymbolBinding::Local,
                definition: SymbolDefinition::Import,
                source: None,
            },
            Symbol {
                name: "constant".into(),
                binding: SymbolBinding::Local,
                definition: SymbolDefinition::Absolute(-5),
                source: None,
            },
        ],
    }
}
fn patch(expression: RelocationExpr) -> Patch {
    Patch {
        offset: 0,
        width: PatchWidth::Word,
        semantics: PatchSemantics::Absolute,
        pc: AddressContext {
            section: SectionId(1),
            offset: 2,
        },
        expression,
        source: source(),
    }
}
#[test]
fn standard_sections_symbols_and_provenance_survive() {
    let original = object();
    let bytes = write_elf(&original).unwrap();
    let parsed =
        object::read::elf::ElfFile32::<object::Endianness>::parse(bytes.as_slice()).unwrap();
    assert_eq!(
        parsed
            .section_by_index(object::SectionIndex(1))
            .unwrap()
            .data()
            .unwrap(),
        [0; 4]
    );
    assert_eq!(parsed.symbols().count(), 3);
    for symbol in parsed.symbols() {
        let (binding, undefined) = match symbol.name().unwrap() {
            "constant" => (object::elf::STB_LOCAL, false),
            "Target" => (object::elf::STB_GLOBAL, false),
            "External" => (object::elf::STB_GLOBAL, true),
            name => panic!("Unexpected symbol {name}"),
        };
        assert_eq!(symbol.is_undefined(), undefined);
        assert_eq!(
            symbol.flags(),
            object::SymbolFlags::Elf {
                st_info: object::elf::SymbolInfo((binding.0 << 4) | object::elf::STT_NOTYPE.0),
                st_other: object::elf::SymbolOther(0),
            }
        );
    }
    let restored = read_elf(&bytes).unwrap();
    assert_eq!(format!("{restored:?}"), format!("{original:?}"));
}
#[test]
fn all_expression_operators_survive() {
    let leaves = vec![
        RelocationExpr::Literal(-77),
        RelocationExpr::Symbol(SymbolId(1)),
        RelocationExpr::CurrentAddress,
        RelocationExpr::Bank(SymbolId(0)),
        RelocationExpr::BankCurrent,
        RelocationExpr::BankSection(SectionRef::Local(SectionId(0))),
        RelocationExpr::SectionSize(SectionRef::External("other".into())),
        RelocationExpr::SectionStart(SectionRef::Local(SectionId(1))),
        RelocationExpr::SectionTypeSize(3),
        RelocationExpr::SectionTypeStart(7),
    ];
    let mut expressions = leaves.clone();
    for op in [
        UnaryOp::Negate,
        UnaryOp::BitNot,
        UnaryOp::LogicalNot,
        UnaryOp::High,
        UnaryOp::Low,
        UnaryOp::BitWidth,
        UnaryOp::TrailingZeros,
    ] {
        expressions.push(RelocationExpr::Unary(op, Box::new(leaves[2].clone())))
    }
    for op in [
        BinaryOp::Add,
        BinaryOp::Subtract,
        BinaryOp::Multiply,
        BinaryOp::Divide,
        BinaryOp::Modulo,
        BinaryOp::Power,
        BinaryOp::BitOr,
        BinaryOp::BitAnd,
        BinaryOp::BitXor,
        BinaryOp::LogicalAnd,
        BinaryOp::LogicalOr,
        BinaryOp::Equal,
        BinaryOp::NotEqual,
        BinaryOp::Greater,
        BinaryOp::Less,
        BinaryOp::GreaterEqual,
        BinaryOp::LessEqual,
        BinaryOp::ShiftLeft,
        BinaryOp::ShiftRight,
        BinaryOp::UnsignedShiftRight,
    ] {
        expressions.push(RelocationExpr::Binary(
            op,
            Box::new(leaves[2].clone()),
            Box::new(leaves[0].clone()),
        ))
    }
    for expression in expressions {
        let mut original = object();
        original.sections[0].patches.push(patch(expression.clone()));
        let bytes = write_elf(&original).unwrap();
        assert_eq!(
            read_elf(&bytes).unwrap().sections[0].patches[0].expression,
            expression
        );
    }
}
#[test]
fn relocation_semantics_widths_pc_and_expansion_survive() {
    for semantics in [
        PatchSemantics::Absolute,
        PatchSemantics::Signed,
        PatchSemantics::PcRelative { bias: 2 },
        PatchSemantics::HighMemory,
    ] {
        for width in [PatchWidth::Byte, PatchWidth::Word, PatchWidth::Long] {
            if semantics == PatchSemantics::HighMemory && width != PatchWidth::Byte {
                continue;
            }
            let mut original = object();
            let mut p = patch(RelocationExpr::Symbol(SymbolId(1)));
            p.width = width;
            p.semantics = semantics;
            original.sections[0].patches.push(p);
            let restored = read_elf(&write_elf(&original).unwrap()).unwrap();
            assert_eq!(format!("{restored:?}"), format!("{original:?}"));
        }
    }
}
#[test]
fn every_truncation_and_corruption_is_rejected() {
    let mut original = object();
    original.sections[0]
        .patches
        .push(patch(RelocationExpr::CurrentAddress));
    let bytes = write_elf(&original).unwrap();
    for len in 0..bytes.len() {
        assert!(read_elf(&bytes[..len]).is_err(), "length {len}")
    }
    for offset in [4, 5, 16, 18, 20, 36] {
        let mut bad = bytes.clone();
        bad[offset] ^= 0x7f;
        assert!(read_elf(&bad).is_err(), "offset {offset}")
    }
    let mut bad = bytes.clone();
    bad.extend([0]);
    assert!(read_elf(&bad).is_err());
}
#[test]
fn invalid_graph_never_serializes() {
    let mut original = object();
    original.sections[0]
        .patches
        .push(patch(RelocationExpr::Symbol(SymbolId(99))));
    assert!(write_elf(&original).is_err());
    original.sections[0].patches.clear();
    original.sections[1].data.push(1);
    assert!(write_elf(&original).is_err());
}

#[test]
fn section_placement_checks_memory_and_bank_boundaries() {
    for kind in 0..8 {
        let (start, size, first, last) = memory_region(kind).unwrap();
        let mut object = Object {
            sections: vec![Section {
                name: "region".into(),
                kind,
                size,
                data: if kind <= 1 {
                    vec![0; size as usize]
                } else {
                    vec![]
                },
                address: Some(start as i32),
                bank: Some(first),
                source: Some(source()),
                ..Section::default()
            }],
            symbols: vec![],
        };
        assert!(object.validate().is_ok());
        object.sections[0].bank = Some(last + 1);
        assert!(object.validate().is_err());
        object.sections[0].bank = Some(first);
        object.sections[0].address = Some((start + 1) as i32);
        assert!(object.validate().is_err());
        object.sections[0].address = Some(start as i32);
        object.sections[0].size = size + 1;
        assert!(object.validate().is_err());
    }
}

#[test]
fn section_combinations_names_and_optional_provenance_survive() {
    for (combination, kind) in [
        (SectionCombination::Union, 4),
        (SectionCombination::Fragment, 0),
    ] {
        let original = Object {
            sections: vec![
                Section {
                    name: "共有".into(),
                    kind,
                    combination,
                    ..Default::default()
                },
                Section {
                    name: "共有".into(),
                    kind,
                    combination,
                    ..Default::default()
                },
            ],
            symbols: vec![
                Symbol {
                    name: "Name".into(),
                    binding: SymbolBinding::Local,
                    definition: SymbolDefinition::Absolute(1),
                    source: None,
                },
                Symbol {
                    name: "name".into(),
                    binding: SymbolBinding::Local,
                    definition: SymbolDefinition::Absolute(2),
                    source: None,
                },
            ],
        };
        let restored = read_elf(&write_elf(&original).unwrap()).unwrap();
        assert_eq!(format!("{restored:?}"), format!("{original:?}"));
    }
}
#[test]
fn malformed_rpn_is_rejected_before_evaluation() {
    let mut original = object();
    original.sections[0]
        .patches
        .push(patch(RelocationExpr::CurrentAddress));
    let bytes = write_elf(&original).unwrap();
    let parsed =
        object::read::elf::ElfFile32::<object::Endianness>::parse(bytes.as_slice()).unwrap();
    let meta = parsed
        .sections()
        .filter(|s| s.name().ok() == Some(".koh.meta"))
        .last()
        .unwrap();
    let (offset, size) = meta.file_range().unwrap();
    let end = (offset + size) as usize;
    let position = bytes[offset as usize..end]
        .windows(5)
        .position(|w| w == [1, 0, 0, 0, 3])
        .unwrap()
        + offset as usize
        + 4;
    for opcode in [0x20, 0x40, 0xff] {
        let mut bad = bytes.clone();
        bad[position] = opcode;
        assert!(read_elf(&bad).is_err());
    }
}
#[test]
fn malformed_symbol_section_data_and_provenance_graphs_are_rejected() {
    let mut value = object();
    value.sections[0]
        .patches
        .push(patch(RelocationExpr::Symbol(SymbolId(99))));
    assert!(write_elf(&value).is_err());
    value = object();
    value.symbols[0].definition = SymbolDefinition::Label {
        section: SectionId(99),
        offset: 0,
    };
    assert!(write_elf(&value).is_err());
    value = object();
    value.sections[0].size = 3;
    assert!(write_elf(&value).is_err());
    value = object();
    let mut p = patch(RelocationExpr::CurrentAddress);
    p.offset = 3;
    value.sections[0].patches.push(p);
    assert!(write_elf(&value).is_err());
    value = object();
    value.sections[0].source.as_mut().unwrap().line = 0;
    assert!(write_elf(&value).is_err());
    value = object();
    value.symbols[0].definition = SymbolDefinition::Absolute(1);
    value.sections[0]
        .patches
        .push(patch(RelocationExpr::Bank(SymbolId(0))));
    assert!(write_elf(&value).is_err());
    value = object();
    value.sections[0].kind = 4;
    assert!(write_elf(&value).is_err());
}

#[test]
fn overlapping_section_payloads_cannot_amplify_input_allocations() {
    let original = Object {
        sections: vec![
            Section {
                name: "large".into(),
                size: 0x4000,
                data: vec![0; 0x4000],
                ..Default::default()
            },
            Section {
                name: "alias".into(),
                ..Default::default()
            },
        ],
        symbols: vec![],
    };
    let mut bytes = write_elf(&original).unwrap();
    let table = u32::from_le_bytes(bytes[32..36].try_into().unwrap()) as usize;
    let payload = bytes[table + 40 + 16..table + 40 + 20].to_vec();
    bytes[table + 80 + 16..table + 80 + 20].copy_from_slice(&payload);
    bytes[table + 80 + 20..table + 80 + 24].copy_from_slice(&0x4000u32.to_le_bytes());
    assert!(read_elf(&bytes).unwrap_err().contains("allocation budget"));
}

#[test]
fn standard_alignment_mirrors_zero_offset_constraints() {
    for exponent in [0, 4, 8, 16] {
        let original = Object {
            sections: vec![Section {
                name: "aligned".into(),
                align: exponent,
                size: 1,
                data: vec![0],
                ..Default::default()
            }],
            symbols: vec![],
        };
        let bytes = write_elf(&original).unwrap();
        let parsed =
            object::read::elf::ElfFile32::<object::Endianness>::parse(bytes.as_slice()).unwrap();
        let section = parsed.section_by_index(object::SectionIndex(1)).unwrap();
        assert_eq!(section.align(), 1u64 << exponent);
        assert_eq!(section.file_range().unwrap().0 % (1u64 << exponent), 0);
        let restored = read_elf(&bytes).unwrap();
        assert_eq!(restored.sections[0].align, exponent);
    }
    let original = object();
    let bytes = write_elf(&original).unwrap();
    let parsed =
        object::read::elf::ElfFile32::<object::Endianness>::parse(bytes.as_slice()).unwrap();
    assert_eq!(
        parsed
            .section_by_index(object::SectionIndex(2))
            .unwrap()
            .align(),
        1
    );
    assert_eq!(read_elf(&bytes).unwrap().sections[1].align_offset, 1);
}

#[test]
fn alignment_padding_is_bounded_before_file_allocation() {
    let mut value = Object::default();
    for index in 0..1025 {
        value.sections.push(Section {
            name: format!("s{index}"),
            align: 16,
            size: 1,
            data: vec![0],
            ..Default::default()
        });
    }
    assert!(write_elf(&value).unwrap_err().contains("64 MiB"));
    value.sections.truncate(2);
    assert!(read_elf(&write_elf(&value).unwrap()).is_ok());
}

#[test]
fn dense_symbol_indices_preserve_interleaved_bindings_and_relocations() {
    let mut value = object();
    value.symbols.clear();
    for index in 0..2000 {
        value.symbols.push(Symbol {
            name: format!("symbol{index}"),
            binding: if index % 3 == 0 {
                SymbolBinding::Exported
            } else {
                SymbolBinding::Local
            },
            definition: if index % 3 == 1 {
                SymbolDefinition::Import
            } else {
                SymbolDefinition::Label {
                    section: SectionId(0),
                    offset: 0,
                }
            },
            source: None,
        });
    }
    value.sections[0]
        .patches
        .push(patch(RelocationExpr::Symbol(SymbolId(0))));
    let mut last = patch(RelocationExpr::Symbol(SymbolId(1999)));
    last.offset = 2;
    value.sections[0].patches.push(last);
    let bytes = write_elf(&value).unwrap();
    let restored = read_elf(&bytes).unwrap();
    assert_eq!(format!("{restored:?}"), format!("{value:?}"));
    assert_eq!(write_elf(&restored).unwrap(), bytes);
    let parsed =
        object::read::elf::ElfFile32::<object::Endianness>::parse(bytes.as_slice()).unwrap();
    let metadata = parsed
        .sections()
        .filter(|section| section.name().ok() == Some(".koh.meta"))
        .last()
        .unwrap();
    let (offset, size) = metadata.file_range().unwrap();
    let records = (offset + size) as usize - value.symbols.len() * 6;
    let first = bytes[records..records + 4].to_vec();
    for index in [
        first,
        2001u32.to_le_bytes().to_vec(),
        0u32.to_le_bytes().to_vec(),
    ] {
        let mut malformed = bytes.clone();
        malformed[records + 6..records + 10].copy_from_slice(&index);
        assert!(
            read_elf(&malformed)
                .unwrap_err()
                .contains("Invalid or duplicate ELF symbol index")
        );
    }
}
