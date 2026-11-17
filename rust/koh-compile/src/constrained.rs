use crate::{
    cil::{Instruction, Operand},
    resolve::{Definition, Resolver},
};
use std::collections::{HashMap, HashSet};

pub(crate) fn prefixes(cil: &[Instruction]) -> Result<HashMap<u32, u32>, String> {
    let targets: HashSet<_> = cil
        .iter()
        .flat_map(|instruction| match &instruction.operand {
            Operand::Target(target) => std::slice::from_ref(target),
            Operand::Targets(targets) => targets.as_slice(),
            _ => &[],
        })
        .copied()
        .collect();
    let mut result = HashMap::new();
    for (index, prefix) in cil.iter().enumerate().filter(|(_, i)| i.opcode == 0xfe16) {
        let Operand::Token(token) = prefix.operand else {
            return Err("Missing constrained type token".into());
        };
        if !matches!(token >> 24, 1 | 2 | 0x1b) || token & 0xffffff == 0 {
            return Err(format!(
                "IL_{:04x}: Invalid constrained type token",
                prefix.offset
            ));
        }
        let next = cil
            .get(index + 1)
            .ok_or_else(|| format!("IL_{:04x}: Dangling constrained prefix", prefix.offset))?;
        if matches!(next.opcode, 0x28 | 0xfe06) {
            return Err(format!(
                "IL_{:04x}: Constrained static dispatch requires explicit lowering",
                prefix.offset
            ));
        }
        if next.opcode != 0x6f {
            return Err(format!(
                "IL_{:04x}: Unsupported constrained prefix sequence",
                prefix.offset
            ));
        }
        if targets.contains(&next.offset) {
            return Err(format!(
                "IL_{:04x}: Branch enters constrained call after its prefix",
                next.offset
            ));
        }
        result.insert(next.offset, token);
    }
    Ok(result)
}

pub(crate) fn value_target(
    resolver: &Resolver<'_>,
    value: Definition,
    method: Definition,
) -> Result<Definition, String> {
    use clrmeta::TableId;
    let raw_value = resolver.type_definition(value)?;
    let owner = crate::lower::owner_type(resolver, method)?;
    let method = resolver
        .instance(method)?
        .map_or(method, |instance| instance.method);
    let declared_image = resolver.assembly(method.assembly)?;
    let declared = declared_image.method(method.token)?;
    let signature = declared_image.method_signature(method.token)?;
    if declared.flags & 0x10 != 0 || !signature.calling_convention.has_this() {
        return Err("Constrained call requires an instance method".into());
    }
    if resolver.type_definition(owner)? == raw_value {
        if owner != value {
            return Err(
                "Constrained value method owner must match its closed receiver type".into(),
            );
        }
        if declared.flags & 0x400 != 0 || declared.rva == 0 {
            return Err("Constrained value method has no body".into());
        }
        return Ok(method);
    }
    if !crate::layout::class(resolver, owner)?.interface {
        return Err("Constrained value call requires boxing/base-value runtime support".into());
    }
    let supplied = resolver.type_argument(value)?;
    let required = resolver.type_argument(owner)?;
    if !crate::generics::assignable(resolver, &supplied, &required)? {
        return Err("Constrained value does not implement the declared interface".into());
    }
    let image = resolver.assembly(raw_value.assembly)?;
    let metadata = image.metadata();
    let row = (raw_value.token & 0xffffff)
        .checked_sub(1)
        .ok_or("Invalid constrained value identity")? as usize;
    let ty = metadata
        .type_defs
        .get(row)
        .ok_or("Invalid constrained value identity")?;
    let end = metadata
        .type_defs
        .get(row + 1)
        .map_or(metadata.method_defs.len() as u32 + 1, |ty| ty.method_list);
    if ty.method_list == 0 || ty.method_list > end || end > metadata.method_defs.len() as u32 + 1 {
        return Err("Invalid constrained value method range".into());
    }
    crate::generics::charge(
        &resolver.generic_work,
        (end - ty.method_list) as usize + metadata.method_impls.len(),
    )?;
    let matches = |body: Definition| -> Result<bool, String> {
        if crate::lower::owner_type(resolver, body)? != raw_value {
            return Err("Constrained MethodImpl body must belong to its value type".into());
        }
        let body_image = resolver.assembly(body.assembly)?;
        let row = body_image.method(body.token)?;
        let actual = body_image.method_signature(body.token)?;
        Ok(row.flags & 0x410 == 0
            && row.rva != 0
            && actual.calling_convention.has_this()
            && resolver.member_signatures_equal(
                method.assembly,
                &signature,
                owner,
                &actual,
                value,
            )?)
    };
    let mut explicit = None;
    let mut declarations = HashSet::new();
    for mapping in metadata
        .method_impls
        .iter()
        .filter(|row| row.class == raw_value.token & 0xffffff)
    {
        let resolve = |index: clrmeta::CodedIndex| -> Result<(Definition, Definition), String> {
            let table = index.table.ok_or("Null constrained MethodImpl token")?;
            if !matches!(table, TableId::MethodDef | TableId::MemberRef) || index.row == 0 {
                return Err("Invalid constrained MethodImpl token".into());
            }
            let arguments = resolver
                .type_instance(value)?
                .map_or_else(Vec::new, |instance| instance.arguments);
            let target = resolver.resolve_method_in(
                raw_value.assembly,
                ((table as u32) << 24) | index.row,
                &[],
                &arguments,
            )?;
            let template = crate::lower::owner_type(resolver, target.definition)?;
            let declaring = if let Some(declaring) = target.declaring {
                declaring
            } else if !target.type_arguments.is_empty() {
                let types = target
                    .type_arguments
                    .iter()
                    .map(|ty| {
                        resolver.bind_signature(target.argument_assembly, ty, &[], &arguments)
                    })
                    .collect::<Result<Vec<_>, _>>()?;
                let value_type = matches!(
                    resolver.type_argument(template)?.ty,
                    clrmeta::TypeSig::ValueType(_)
                );
                resolver.close_type(template, types, value_type)?
            } else if template == raw_value {
                value
            } else {
                template
            };
            if !target.method_arguments.is_empty() {
                return Err("Constrained MethodImpl cannot name a method instantiation".into());
            }
            Ok((target.definition, declaring))
        };
        let declaration = resolve(mapping.method_declaration)?;
        let (body, body_owner) = resolve(mapping.method_body)?;
        if !declarations.insert(declaration) {
            return Err("Duplicate constrained MethodImpl declaration".into());
        }
        if declaration == (method, owner) {
            if body_owner != value {
                return Err("Constrained MethodImpl body must belong to its value type".into());
            }
            if declared.flags & 0x70 != 0x40
                || resolver.assembly(body.assembly)?.method(body.token)?.flags & 0x50 != 0x40
            {
                return Err("Constrained MethodImpl requires nonfinal interface declaration and instance virtual body".into());
            }
            if !matches(body)? {
                return Err(
                    "Constrained MethodImpl signature or body differs from declaration".into(),
                );
            }
            explicit = Some(body);
        }
    }
    if let Some(body) = explicit {
        return Ok(body);
    }
    let name = declared_image
        .metadata()
        .strings
        .get(declared.name)
        .map_err(|e| e.to_string())?;
    let mut selected = None;
    for index in ty.method_list..end {
        let candidate = &metadata.method_defs[index as usize - 1];
        if candidate.flags & 0x57 != 0x46
            || metadata
                .strings
                .get(candidate.name)
                .map_err(|e| e.to_string())?
                != name
        {
            continue;
        }
        let body = Definition {
            assembly: raw_value.assembly,
            token: 0x06000000 | index,
        };
        if matches(body)? && selected.replace(body).is_some() {
            return Err("Ambiguous constrained value implementations".into());
        }
    }
    selected
        .ok_or("No supported constrained value implementation; boxing/default-interface value dispatch is not implemented".into())
}

#[cfg(test)]
mod tests {
    use super::prefixes;
    use crate::cil::{Instruction, Operand};
    use rstest::rstest;

    #[rstest]
    #[case(None, 0x6f, 0x02000001, true)]
    #[case(Some(0), 0x6f, 0x1b000001, true)]
    #[case(Some(6), 0x6f, 0x02000001, false)]
    #[case(None, 0x28, 0x02000001, false)]
    #[case(None, 0xfe06, 0x02000001, false)]
    #[case(None, 0x2a, 0x02000001, false)]
    #[case(None, 0x6f, 0x06000001, false)]
    #[case(None, 0x6f, 0x02000000, false)]
    fn prefix_and_call_are_one_control_flow_unit(
        #[case] branch: Option<u32>,
        #[case] opcode: u16,
        #[case] token: u32,
        #[case] valid: bool,
    ) {
        let mut cil = vec![
            Instruction {
                offset: 0,
                opcode: 0xfe16,
                operand: Operand::Token(token),
            },
            Instruction {
                offset: 6,
                opcode,
                operand: Operand::Token(0x06000001),
            },
        ];
        if let Some(target) = branch {
            cil.push(Instruction {
                offset: 11,
                opcode: 0x2b,
                operand: Operand::Target(target),
            });
        }
        assert_eq!(prefixes(&cil).is_ok(), valid);
        if matches!(opcode, 0x28 | 0xfe06) {
            assert!(prefixes(&cil).unwrap_err().contains("static dispatch"));
        }
        assert!(prefixes(&cil[..1]).unwrap_err().contains("IL_0000"));
    }
}
