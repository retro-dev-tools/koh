use crate::{draft, encode::OperandKind, expr::Expr, model::*};
use std::collections::HashMap;

pub(crate) fn lower(draft: draft::Object, allow_imports: bool) -> Result<Object, String> {
    let mut object = Object::default();
    let mut symbols = HashMap::new();
    let sections: HashMap<_, _> = draft
        .sections
        .iter()
        .enumerate()
        .map(|(i, s)| (s.name.clone(), SectionId(i)))
        .collect();
    for sym in draft.symbols {
        if sym.kind > 1 {
            return Err("non-numeric symbol reached object lowering".into());
        }
        if symbols.contains_key(&sym.name) {
            return Err(format!("duplicate symbol '{}'", sym.name));
        }
        let id = SymbolId(object.symbols.len());
        symbols.insert(sym.name.clone(), id);
        let definition = if let Some(section) = sym.section {
            SymbolDefinition::Label {
                section: SectionId(section),
                offset: u32::try_from(sym.value).map_err(|_| "negative label offset")?,
            }
        } else {
            SymbolDefinition::Absolute(sym.value)
        };
        object.symbols.push(Symbol {
            name: sym.name,
            binding: if sym.visibility == 1 {
                SymbolBinding::Exported
            } else {
                SymbolBinding::Local
            },
            definition,
            source: sym.source,
        });
    }
    for section in draft.sections {
        let mut patches = Vec::new();
        for patch in section.patches {
            let expression = expression(
                patch.expression,
                &mut object,
                &mut symbols,
                &sections,
                allow_imports,
            )
            .map_err(|e| {
                format!(
                    "{}:{}:{}: {e}",
                    patch.source.file, patch.source.line, patch.source.column
                )
            })?;
            let width = match patch.kind {
                0 | 3 => PatchWidth::Byte,
                1 => PatchWidth::Word,
                2 => PatchWidth::Long,
                _ => return Err("invalid pending patch width".into()),
            };
            let semantics = match patch.constraint {
                Some(OperandKind::High8) => PatchSemantics::HighMemory,
                Some(OperandKind::Signed8) => PatchSemantics::Signed,
                _ if patch.kind == 3 => PatchSemantics::PcRelative {
                    bias: u32::try_from(patch.pc_after - patch.pc_offset)
                        .map_err(|_| "negative PC bias")?,
                },
                _ => PatchSemantics::Absolute,
            };
            patches.push(Patch {
                offset: patch.offset,
                width,
                semantics,
                pc: AddressContext {
                    section: SectionId(patch.pc_section),
                    offset: u32::try_from(patch.pc_offset).map_err(|_| "negative patch PC")?,
                },
                expression,
                source: patch.source,
            });
        }
        let size = u32::try_from(section.data.len()).map_err(|_| "section too large")?;
        let data = if section.kind < 2 {
            section.data
        } else {
            if section.data.iter().any(|v| *v != 0) {
                return Err(format!(
                    "initialized data in RAM section '{}' cannot be represented",
                    section.name
                ));
            }
            Vec::new()
        };
        object.sections.push(Section {
            name: section.name,
            kind: section.kind,
            address: section.address,
            bank: section.bank,
            align: section.align,
            align_offset: section.align_offset,
            size,
            data,
            patches,
            lines: section.lines,
            source: section.source,
        });
    }
    object.validate()?;
    Ok(object)
}

fn symbol(
    name: String,
    object: &mut Object,
    symbols: &mut HashMap<String, SymbolId>,
    allow_imports: bool,
) -> Result<SymbolId, String> {
    if let Some(id) = symbols.get(&name) {
        return Ok(*id);
    }
    if !allow_imports {
        return Err(format!("undefined symbol '{name}'"));
    }
    let id = SymbolId(object.symbols.len());
    symbols.insert(name.clone(), id);
    object.symbols.push(Symbol {
        name,
        binding: SymbolBinding::Local,
        definition: SymbolDefinition::Import,
        source: None,
    });
    Ok(id)
}

fn expression(
    tree: Expr,
    object: &mut Object,
    symbols: &mut HashMap<String, SymbolId>,
    sections: &HashMap<String, SectionId>,
    allow_imports: bool,
) -> Result<RelocationExpr, String> {
    use {BinaryOp as B, RelocationExpr as R, UnaryOp as U};
    let recurse = |tree, object: &mut Object, symbols: &mut HashMap<String, SymbolId>| {
        expression(tree, object, symbols, sections, allow_imports)
    };
    Ok(match tree {
        Expr::Number(n) => R::Literal(n),
        Expr::Symbol(name) => R::Symbol(symbol(name, object, symbols, allow_imports)?),
        Expr::Pc => R::CurrentAddress,
        Expr::Unary(op, a) => {
            if op == "+" {
                return recurse(*a, object, symbols);
            }
            let op = match op.as_str() {
                "-" => U::Negate,
                "~" => U::BitNot,
                "!" => U::LogicalNot,
                _ => return Err(format!("unsupported relocation unary operator '{op}'")),
            };
            R::Unary(op, Box::new(recurse(*a, object, symbols)?))
        }
        Expr::Binary(op, a, b) => {
            if matches!(op.as_str(), "/" | "%") && crate::expr::eval(&b, &|_| None, 0) == Ok(0) {
                return Err("division by zero in relocation expression".into());
            }
            let op = match op.as_str() {
                "+" => B::Add,
                "-" => B::Subtract,
                "*" => B::Multiply,
                "/" => B::Divide,
                "%" => B::Modulo,
                "**" => B::Power,
                "|" => B::BitOr,
                "&" => B::BitAnd,
                "^" => B::BitXor,
                "&&" => B::LogicalAnd,
                "||" => B::LogicalOr,
                "==" => B::Equal,
                "!=" => B::NotEqual,
                ">" => B::Greater,
                "<" => B::Less,
                ">=" => B::GreaterEqual,
                "<=" => B::LessEqual,
                "<<" => B::ShiftLeft,
                ">>" => B::ShiftRight,
                ">>>" => B::UnsignedShiftRight,
                _ => return Err(format!("unsupported relocation binary operator '{op}'")),
            };
            R::Binary(
                op,
                Box::new(recurse(*a, object, symbols)?),
                Box::new(recurse(*b, object, symbols)?),
            )
        }
        Expr::Call(name, mut args) if args.len() == 1 => {
            let arg = args.remove(0);
            match name.as_str() {
                "HIGH" | "LOW" | "BITWIDTH" | "TZCOUNT" => R::Unary(
                    match name.as_str() {
                        "HIGH" => U::High,
                        "LOW" => U::Low,
                        "BITWIDTH" => U::BitWidth,
                        _ => U::TrailingZeros,
                    },
                    Box::new(recurse(arg, object, symbols)?),
                ),
                "BANK" => match arg {
                    Expr::Pc => R::BankCurrent,
                    Expr::Symbol(s) => R::Bank(symbol(s, object, symbols, allow_imports)?),
                    Expr::String(s) => R::BankSection(section_ref(s, sections)),
                    _ => return Err("BANK requires a symbol or section name".into()),
                },
                "SIZEOF" | "STARTOF" => {
                    let section = match arg {
                        Expr::String(s) | Expr::Symbol(s) => s,
                        _ => return Err(format!("{name} requires section name")),
                    };
                    if let Some(kind) = section_type(&section) {
                        if name == "SIZEOF" {
                            R::SectionTypeSize(kind)
                        } else {
                            R::SectionTypeStart(kind)
                        }
                    } else if name == "SIZEOF" {
                        R::SectionSize(section_ref(section, sections))
                    } else {
                        R::SectionStart(section_ref(section, sections))
                    }
                }
                _ => return Err(format!("unsupported relocation function '{name}'")),
            }
        }
        Expr::Call(name, _) => {
            return Err(format!("unsupported relocation function or arity '{name}'"));
        }
        Expr::String(_) => return Err("unresolved string in numeric relocation".into()),
    })
}

fn section_ref(name: String, sections: &HashMap<String, SectionId>) -> SectionRef {
    if let Some(id) = sections.get(&name) {
        SectionRef::Local(*id)
    } else {
        SectionRef::External(name)
    }
}
fn section_type(name: &str) -> Option<u8> {
    Some(match name.to_ascii_uppercase().as_str() {
        "ROM0" => 0,
        "ROMX" => 1,
        "VRAM" => 2,
        "SRAM" => 3,
        "WRAM0" => 4,
        "WRAMX" => 5,
        "OAM" => 6,
        "HRAM" => 7,
        _ => return None,
    })
}
