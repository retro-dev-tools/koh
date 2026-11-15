use crate::{
    ir::{Function, Type, verify_program},
    resolve::Definition,
    sm83,
};
use koh_objects::*;
use std::collections::HashMap;

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

fn startup(entry: &Function, globals: u16) -> Result<Object, String> {
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
    let returns = entry.returns.is_some();
    if returns {
        data.extend_from_slice(&[0xe8, 0xfc]);
    }
    data.push(0xcd);
    let mut patches = vec![patch(1, data.len(), 1)];
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
    let bytes: u16 = fields
        .iter()
        .map(|(_, bytes)| u16::from(*bytes))
        .try_fold(0u16, |total, bytes| total.checked_add(bytes))
        .ok_or("Static storage exceeds capacity")?;
    if bytes > 0x100 {
        return Err("Compiler static storage exceeds available WRAM0 capacity (256 bytes)".into());
    }
    let mut objects = vec![startup(root, bytes)?];
    if bytes != 0 {
        let mut symbols = vec![symbol("__koh_statics".into(), 0, 0)];
        let mut offset = 0;
        for (field, width) in fields {
            symbols.push(symbol(sm83::static_symbol(field), 0, offset));
            offset += usize::from(width);
        }
        objects.push(Object {
            sections: vec![Section {
                name: ".koh.statics".into(),
                kind: 4,
                size: u32::from(bytes),
                ..Default::default()
            }],
            symbols,
        });
    }
    for function in functions {
        objects.push(sm83::emit(function)?);
    }
    combine(objects)
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
            _ => return Err("Unsupported compiler relocation expression".into()),
        };
        output.sections[section_maps[input][section].0]
            .patches
            .push(patch);
    }
    output.validate()?;
    Ok(output)
}
