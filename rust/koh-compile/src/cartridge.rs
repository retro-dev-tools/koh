use crate::{
    ir::{Function, Type, verify_program},
    resolve::Definition,
    sm83,
};
use koh_objects::*;
use std::collections::{BTreeMap, BTreeSet, HashMap};

const OAM_DMA: [u8; 8] = [0xe0, 0x46, 0x3e, 40, 0x3d, 0x20, 0xfd, 0xc9];

fn symbol(name: String, section: usize, offset: usize) -> Symbol {
    Symbol {
        name,
        binding: SymbolBinding::Exported,
        definition: SymbolDefinition::Label {
            section: SectionId(section),
            offset: offset as u32,
        },
        source: None,
    }
}

fn patch(section: usize, offset: usize, target: usize) -> Patch {
    Patch {
        offset,
        width: PatchWidth::Word,
        semantics: PatchSemantics::Absolute,
        pc: AddressContext {
            section: SectionId(section),
            offset: offset as u32,
        },
        expression: RelocationExpr::Symbol(SymbolId(target)),
        source: Provenance {
            file: "<koh startup>".into(),
            line: 1,
            column: 1,
            expansion: vec![],
        },
    }
}

fn startup(entry: &Function, globals: u16, dma: bool) -> Result<Object, String> {
    if !entry.parameters.is_empty() || !matches!(entry.returns, None | Some(Type::I32)) {
        return Err("Invalid cartridge entry signature".into());
    }
    let mut symbols = vec![
        symbol("__koh_start".into(), 1, 0),
        Symbol {
            name: sm83::symbol(entry.identity),
            binding: SymbolBinding::Local,
            definition: SymbolDefinition::Import,
            source: None,
        },
    ];
    let mut boot = vec![0; 0x150];
    boot[0x147] = 0x19;
    boot[0x101] = 0xc3;
    let mut boot_patches = vec![patch(0, 0x102, 0)];
    let mut data = vec![0xf3, 0x31, 0, 0xd0, 0xaf, 0xe0, 0x0f, 0xea, 0xff, 0xff];
    let zero = data.len();
    if globals != 0 {
        data.extend_from_slice(&[
            0x21,
            0,
            0,
            0x01,
            globals as u8,
            (globals >> 8) as u8,
            0x36,
            0,
            0x23,
            0x0b,
            0x78,
            0xb1,
            0x20,
            0xf8,
        ]);
    }
    let install = data.len();
    if dma {
        data.extend_from_slice(&[
            0x21,
            0,
            0,
            0x11,
            0,
            0,
            0x06,
            OAM_DMA.len() as u8,
            0x2a,
            0x12,
            0x13,
            0x05,
            0x20,
            0xfa,
        ]);
    }
    let returns = entry.returns.is_some();
    data.extend_from_slice(&[
        0xe8,
        if returns { 0xf8 } else { 0xfc },
        0xf8,
        2,
        0x36,
        0,
        0x23,
        0x36,
        0,
    ]);
    data.push(0x01);
    let mut patches = vec![patch(1, data.len(), 1)];
    data.extend_from_slice(&[0, 0, 0x11]);
    let mut entry_bank = patch(1, data.len(), 1);
    entry_bank.expression = RelocationExpr::Bank(SymbolId(1));
    patches.push(entry_bank);
    data.extend_from_slice(&[0, 0, 0xcd]);
    let far_call = data.len();
    data.extend_from_slice(&[0, 0]);
    if returns {
        data.extend_from_slice(&[
            0xf8, 0, 0x4e, 0x23, 0x46, 0x23, 0x5e, 0x23, 0x56, 0x69, 0x60, 0xe8, 4,
        ]);
    } else {
        data.extend_from_slice(&[0x21, 0, 0, 0x11, 0, 0]);
    }
    data.extend_from_slice(&[0xf3, 0xaf]);
    symbols.push(symbol("__koh_exit".into(), 1, data.len()));
    data.extend_from_slice(&[0x76, 0x18, 0xfd]);
    symbols.push(symbol("__koh_abort".into(), 1, 0));
    for (name, code) in [
        ("__koh_trap_stack", 1),
        ("__koh_trap_arithmetic", 2),
        ("__koh_trap_unexpected", 3),
        ("__koh_trap_null", 4),
        ("__koh_trap_allocation", 5),
        ("__koh_trap_bounds", 6),
        ("__koh_trap_initializer", 7),
        ("__koh_trap_pin", 8),
    ] {
        symbols.push(symbol(name.into(), 1, data.len()));
        data.extend_from_slice(&[0xf3, 0x3e, code, 0xc3]);
        patches.push(patch(1, data.len(), 3));
        data.extend_from_slice(&[0, 0]);
    }
    symbols[3].definition = SymbolDefinition::Label {
        section: SectionId(1),
        offset: data.len() as u32,
    };
    data.extend_from_slice(&[0x76, 0x18, 0xfd]);
    for vector in [
        0, 8, 0x10, 0x18, 0x20, 0x28, 0x30, 0x38, 0x40, 0x48, 0x50, 0x58, 0x60,
    ] {
        boot[vector] = 0xc3;
        boot_patches.push(patch(0, vector + 1, 6));
    }
    let far = symbols.len();
    symbols.push(symbol("__koh_far_call".into(), 1, data.len()));
    patches.push(patch(1, far_call, far));
    data.extend_from_slice(&[0xe1, 0xe8, 2, 0xe5, 0x21]);
    let tail_address = data.len();
    data.extend_from_slice(&[
        0, 0, 0xe5, 0x7b, 0xea, 0, 0x20, 0x7a, 0xea, 0, 0x30, 0x60, 0x69, 0xe9,
    ]);
    let tail = symbols.len();
    symbols.push(symbol("__koh_far_return".into(), 1, data.len()));
    patches.push(patch(1, tail_address, tail));
    data.extend_from_slice(&[
        0xc1, 0xd1, 0x7b, 0xea, 0, 0x20, 0x7a, 0xea, 0, 0x30, 0x60, 0x69, 0xe9,
    ]);
    if globals != 0 {
        let id = symbols.len();
        symbols.push(Symbol {
            name: "__koh_statics".into(),
            binding: SymbolBinding::Local,
            definition: SymbolDefinition::Import,
            source: None,
        });
        patches.push(patch(1, zero + 1, id));
    }
    if dma {
        for (name, offset) in [
            ("__koh_oamdma_bytes", install + 1),
            ("__koh_oamdma", install + 4),
        ] {
            let id = symbols.len();
            symbols.push(Symbol {
                name: name.into(),
                binding: SymbolBinding::Local,
                definition: SymbolDefinition::Import,
                source: None,
            });
            patches.push(patch(1, offset, id));
        }
    }
    Ok(Object {
        sections: vec![
            Section {
                name: ".koh.boot".into(),
                kind: 0,
                address: Some(0),
                bank: Some(0),
                size: boot.len() as u32,
                data: boot,
                patches: boot_patches,
                ..Default::default()
            },
            Section {
                name: ".koh.startup".into(),
                kind: 0,
                size: data.len() as u32,
                data,
                patches,
                ..Default::default()
            },
        ],
        symbols,
    })
}

pub(crate) fn emit(functions: &[Function], entry: Definition) -> Result<Object, String> {
    verify_program(functions)?;
    let root = functions
        .iter()
        .find(|f| f.identity == entry)
        .ok_or("Cartridge entry is absent")?;
    let fields = crate::ir::static_fields(functions)?;
    let heap = functions
        .iter()
        .flat_map(|f| &f.blocks)
        .flat_map(|b| &b.instructions)
        .any(|i| {
            matches!(
                i.op,
                crate::ir::Op::NewArray { .. }
                    | crate::ir::Op::ArenaAllocate(_)
                    | crate::ir::Op::ArenaReset
            )
        });
    let mut offset = 0u16;
    let mut placements = Vec::new();
    let mut section_alignment = 1u16;
    for (field, width, alignment) in fields {
        section_alignment = section_alignment.max(alignment);
        offset = offset
            .checked_add(alignment - 1)
            .ok_or("Static alignment exceeds capacity")?
            & !(alignment - 1);
        placements.push((field, usize::from(offset)));
        offset = offset
            .checked_add(u16::from(width))
            .ok_or("Static storage exceeds capacity")?;
    }
    let bytes = offset
        .checked_add(if heap { 4 } else { 0 })
        .ok_or("Static storage exceeds capacity")?;
    if bytes > 0x100 {
        return Err("Compiler static storage exceeds available WRAM0 capacity (256 bytes)".into());
    }
    let dma = functions
        .iter()
        .flat_map(|f| &f.blocks)
        .flat_map(|b| &b.instructions)
        .any(|i| matches!(i.op, crate::ir::Op::OamDma(_)));
    let mut objects = vec![startup(root, bytes, dma)?];
    if dma {
        objects.push(Object {
            sections: vec![
                Section {
                    name: ".rodata.koh.oamdma".into(),
                    kind: 0,
                    size: OAM_DMA.len() as u32,
                    data: OAM_DMA.to_vec(),
                    ..Default::default()
                },
                Section {
                    name: ".koh.oamdma.hram".into(),
                    kind: 7,
                    size: OAM_DMA.len() as u32,
                    ..Default::default()
                },
            ],
            symbols: vec![
                symbol("__koh_oamdma_bytes".into(), 0, 0),
                symbol("__koh_oamdma".into(), 1, 0),
            ],
        });
    }
    if bytes != 0 {
        let mut symbols = vec![symbol("__koh_statics".into(), 0, 0)];
        for (field, offset) in placements {
            symbols.push(symbol(sm83::static_symbol(field), 0, offset));
        }
        if heap {
            symbols.push(symbol("__koh_heap_cursor".into(), 0, usize::from(offset)));
            symbols.push(symbol("__koh_raw_used".into(), 0, usize::from(offset) + 2));
        }
        objects.push(Object {
            sections: vec![Section {
                name: ".koh.statics".into(),
                kind: 4,
                align: section_alignment.trailing_zeros() as u8,
                size: u32::from(bytes),
                ..Default::default()
            }],
            symbols,
        });
    }
    if heap {
        objects.push(Object {
            sections: vec![Section {
                name: ".koh.heap".into(),
                kind: 5,
                bank: Some(1),
                size: 4096,
                ..Default::default()
            }],
            symbols: vec![symbol("__koh_heap".into(), 0, 0)],
        });
    }
    let literals: BTreeSet<_> = functions
        .iter()
        .flat_map(|f| &f.blocks)
        .flat_map(|b| &b.instructions)
        .filter_map(|i| match &i.op {
            crate::ir::Op::StringLiteral(units) => Some(units.clone()),
            _ => None,
        })
        .collect();
    let mut strings = BTreeMap::new();
    let mut data = Vec::new();
    let mut symbols = Vec::new();
    for (index, units) in literals.into_iter().enumerate() {
        if data.len() + units.len() * 2 + 2 > 0x4000 {
            return Err("Interned string data exceeds ROM0 capacity".into());
        }
        let name = format!("__koh_string_{index}");
        symbols.push(symbol(name.clone(), 0, data.len()));
        data.extend_from_slice(&(units.len() as u16).to_le_bytes());
        data.extend(units.iter().flat_map(|unit| unit.to_le_bytes()));
        strings.insert(units, name);
    }
    if !data.is_empty() {
        objects.push(Object {
            sections: vec![Section {
                name: ".rodata.koh.strings".into(),
                kind: 0,
                size: data.len() as u32,
                data,
                ..Default::default()
            }],
            symbols,
        });
    }
    for function in functions {
        objects.push(sm83::emit_with_strings(function, Some(&strings), true)?);
    }
    combine(objects)
}

#[cfg(test)]
mod string_tests {
    use super::*;
    use crate::ir::{Block, Control, Instruction, Op, Terminator};

    fn function(assembly: usize, units: Vec<u16>) -> Function {
        Function {
            identity: Definition {
                assembly,
                token: 0x06000001,
            },
            parameters: vec![],
            returns: Some(Type::I32),
            locals: vec![],
            blocks: vec![Block {
                parameters: vec![],
                instructions: vec![
                    Instruction {
                        result: Some((0, Type::String)),
                        op: Op::StringLiteral(units),
                        il_offset: 0,
                    },
                    Instruction {
                        result: Some((1, Type::I32)),
                        op: Op::StringLength(0),
                        il_offset: 1,
                    },
                ],
                terminator: Terminator {
                    il_offset: 2,
                    kind: Control::Return(Some(1)),
                },
            }],
        }
    }

    #[rstest::rstest]
    #[case(false)]
    #[case(true)]
    fn interns_across_assemblies_in_either_order(#[case] reverse: bool) {
        let mut functions = vec![function(0, vec![0, 0xd800]), function(1, vec![0, 0xd800])];
        let entry = functions[0].identity;
        if reverse {
            functions.reverse();
        }
        let object = emit(&functions, entry).unwrap();
        let object = koh_objects::read_elf(&koh_objects::write_elf(&object).unwrap()).unwrap();
        let pool: Vec<_> = object
            .sections
            .iter()
            .filter(|s| s.name == ".rodata.koh.strings")
            .collect();
        assert_eq!(pool.len(), 1);
        assert_eq!(pool[0].data, [2, 0, 0, 0, 0, 0xd8]);
        assert_eq!(
            object
                .symbols
                .iter()
                .filter(|s| s.name.starts_with("__koh_string_"))
                .count(),
            1
        );
        koh_link::link(&[object], &Default::default()).unwrap();
    }

    #[test]
    fn pool_capacity_is_checked_before_link_layout() {
        let functions = vec![function(0, vec![0; 4096]), function(1, vec![1; 4096])];
        assert!(
            emit(&functions, functions[0].identity)
                .unwrap_err()
                .contains("Interned string data exceeds ROM0")
        );
        let functions = vec![function(0, vec![0; 8191])];
        let object = emit(&functions, functions[0].identity).unwrap();
        assert!(
            koh_link::link(&[object], &Default::default())
                .unwrap_err()
                .contains("Cannot place")
        );
    }
}

fn combine(objects: Vec<Object>) -> Result<Object, String> {
    let mut output = Object::default();
    let mut section_maps = Vec::new();
    let mut symbol_tables = Vec::new();
    let mut pending = Vec::new();
    let mut stack: Option<SectionId> = None;
    for (input, object) in objects.into_iter().enumerate() {
        object.validate()?;
        let mut sections = Vec::new();
        for (local, mut section) in object.sections.into_iter().enumerate() {
            for patch in std::mem::take(&mut section.patches) {
                pending.push((input, local, patch));
            }
            let id = if section.name == ".koh.stack" {
                if let Some(id) = stack {
                    let old = &output.sections[id.0];
                    if old.kind != section.kind
                        || old.combination != section.combination
                        || old.address != section.address
                        || old.bank != section.bank
                        || old.size != section.size
                        || old.align != section.align
                        || old.align_offset != section.align_offset
                    {
                        return Err("Incompatible compiler stack reservations".into());
                    }
                    id
                } else {
                    let id = SectionId(output.sections.len());
                    stack = Some(id);
                    output.sections.push(section);
                    id
                }
            } else {
                let id = SectionId(output.sections.len());
                output.sections.push(section);
                id
            };
            sections.push(id);
        }
        section_maps.push(sections);
        symbol_tables.push(object.symbols);
    }
    let mut names = HashMap::new();
    let mut symbol_maps = Vec::new();
    for (input, symbols) in symbol_tables.into_iter().enumerate() {
        let mut mapping = Vec::new();
        for mut symbol in symbols {
            if let SymbolDefinition::Label { section, offset } = symbol.definition {
                symbol.definition = SymbolDefinition::Label {
                    section: section_maps[input][section.0],
                    offset,
                };
            }
            let id = if let Some(&id) = names.get(&symbol.name) {
                let old: &mut Symbol = &mut output.symbols[id];
                if old.definition == SymbolDefinition::Import {
                    *old = symbol;
                } else if symbol.definition != SymbolDefinition::Import {
                    return Err(format!("Duplicate compiler symbol '{}'", symbol.name));
                }
                SymbolId(id)
            } else {
                let id = SymbolId(output.symbols.len());
                names.insert(symbol.name.clone(), id.0);
                output.symbols.push(symbol);
                id
            };
            mapping.push(id);
        }
        symbol_maps.push(mapping);
    }
    for (input, section, mut patch) in pending {
        patch.pc.section = section_maps[input][patch.pc.section.0];
        patch.expression = match patch.expression {
            RelocationExpr::Symbol(id) => RelocationExpr::Symbol(symbol_maps[input][id.0]),
            RelocationExpr::Bank(id) => RelocationExpr::Bank(symbol_maps[input][id.0]),
            RelocationExpr::BankCurrent => RelocationExpr::BankCurrent,
            _ => return Err("Unsupported compiler relocation expression".into()),
        };
        output.sections[section_maps[input][section].0]
            .patches
            .push(patch);
    }
    output.validate()?;
    Ok(output)
}
