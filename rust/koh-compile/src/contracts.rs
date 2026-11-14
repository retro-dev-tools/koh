use crate::resolve::{Definition, Resolver};
use clrmeta::{CodedIndex, CodedIndexKind, CustomAttributeRow, Metadata, TableId, TypeSig};
use std::collections::{HashMap, HashSet};

#[derive(Clone, Copy, PartialEq)]
enum Kind {
    Intrinsic,
    Interrupt,
}

fn row<T>(rows: &[T], index: u32) -> Result<&T, String> {
    index
        .checked_sub(1)
        .and_then(|i| rows.get(i as usize))
        .ok_or("Invalid attribute metadata row".into())
}

fn named(
    metadata: &Metadata,
    index: CodedIndex,
    depth: usize,
) -> Result<Option<(u32, &str, &str)>, String> {
    if depth > 64 {
        return Err("Attribute type nesting exceeds limit".into());
    }
    let (namespace, name) = match index.table {
        Some(TableId::TypeDef) => {
            let ty = row(&metadata.type_defs, index.row)?;
            (ty.type_namespace, ty.type_name)
        }
        Some(TableId::TypeRef) => {
            let ty = row(&metadata.type_refs, index.row)?;
            (ty.type_namespace, ty.type_name)
        }
        Some(TableId::TypeSpec) => {
            let spec = row(&metadata.type_specs, index.row)?;
            let blob = metadata
                .blobs
                .get(spec.signature)
                .map_err(|e| e.to_string())?;
            let base = match crate::signature::type_spec(blob)? {
                TypeSig::Class(base)
                | TypeSig::ValueType(base)
                | TypeSig::GenericInst { type_ref: base, .. } => base,
                _ => return Err("Attribute constructor requires a named type".into()),
            };
            return named(
                metadata,
                CodedIndex::decode(CodedIndexKind::TypeDefOrRef, base),
                depth + 1,
            );
        }
        _ => return Err("Invalid attribute declaring-type scope".into()),
    };
    let token = ((index.table.ok_or("Missing attribute type table")? as u32) << 24) | index.row;
    Ok(Some((
        token,
        metadata.strings.get(namespace).map_err(|e| e.to_string())?,
        metadata.strings.get(name).map_err(|e| e.to_string())?,
    )))
}

fn kind(
    resolver: &Resolver<'_>,
    assembly: usize,
    attribute: &CustomAttributeRow,
) -> Result<Option<Kind>, String> {
    let metadata = resolver.assembly(assembly)?.metadata();
    let constructor = attribute.attr_type;
    let (parent, name, signature) = match constructor.table {
        Some(TableId::MethodDef) => {
            let method = row(&metadata.method_defs, constructor.row)?;
            let owner = metadata
                .type_defs
                .partition_point(|ty| ty.method_list <= constructor.row);
            if owner == 0 {
                return Err("Attribute constructor has no declaring type".into());
            }
            (
                CodedIndex {
                    table: Some(TableId::TypeDef),
                    row: owner as u32,
                },
                method.name,
                method.signature,
            )
        }
        Some(TableId::MemberRef) => {
            let member = row(&metadata.member_refs, constructor.row)?;
            (member.class, member.name, member.signature)
        }
        _ => return Err("Invalid attribute constructor table".into()),
    };
    if metadata.strings.get(name).map_err(|e| e.to_string())? != ".ctor" {
        return Err("Attribute constructor must name .ctor".into());
    }
    let Some((token, namespace, name)) = named(metadata, parent, 0)? else {
        return Ok(None);
    };
    if namespace != "Koh.GameBoy" {
        return Ok(None);
    }
    let kind = match name {
        "KohIntrinsicAttribute" => Kind::Intrinsic,
        "InterruptAttribute" => Kind::Interrupt,
        _ => return Ok(None),
    };
    let owner = resolver.resolve_type(assembly, token)?;
    if resolver.assembly(owner.assembly)?.name()? != "Koh.GameBoy" {
        return Ok(None);
    }
    let signature =
        crate::signature::method(metadata.blobs.get(signature).map_err(|e| e.to_string())?)?;
    if !signature.calling_convention.has_this()
        || signature.generic_param_count != 0
        || signature.return_type != TypeSig::Primitive(clrmeta::ElementType::Void)
    {
        return Err("Invalid attribute constructor signature".into());
    }
    let token = ((constructor
        .table
        .ok_or("Missing attribute constructor table")? as u32)
        << 24)
        | constructor.row;
    resolver.resolve_method(assembly, token)?;
    Ok(Some(kind))
}

pub(crate) fn assembly(resolver: &Resolver<'_>, assembly: usize) -> Result<(), String> {
    let metadata = resolver.assembly(assembly)?.metadata();
    let mut kinds = HashMap::new();
    for attribute in &metadata.custom_attributes {
        let key = (attribute.attr_type.table, attribute.attr_type.row);
        let resolved = if let Some(&kind) = kinds.get(&key) {
            kind
        } else {
            let kind = kind(resolver, assembly, attribute)?;
            kinds.insert(key, kind);
            kind
        };
        match resolved {
            Some(Kind::Interrupt) => {
                if attribute.parent.table != Some(TableId::MethodDef) {
                    return Err("Interrupt attribute must target a method".into());
                }
                row(&metadata.method_defs, attribute.parent.row)?;
                return Err(format!(
                    "Assembly {}: Interrupt handlers require interrupt lowering",
                    resolver.assembly(assembly)?.name()?
                ));
            }
            Some(Kind::Intrinsic) => match attribute.parent.table {
                Some(TableId::MethodDef) => {
                    row(&metadata.method_defs, attribute.parent.row)?;
                }
                Some(TableId::Property) => {
                    row(&metadata.properties, attribute.parent.row)?;
                }
                _ => return Err("Koh intrinsic must target a method or property".into()),
            },
            None => {}
        }
    }
    Ok(())
}

pub(crate) fn method(resolver: &Resolver<'_>, method: Definition) -> Result<(), String> {
    let metadata = resolver.assembly(method.assembly)?.metadata();
    let rid = method.token & 0x00ff_ffff;
    let properties: HashSet<_> = metadata
        .method_semantics
        .iter()
        .filter(|s| s.method == rid && s.association.table == Some(TableId::Property))
        .map(|s| s.association.row)
        .collect();
    for attribute in &metadata.custom_attributes {
        let direct =
            attribute.parent.table == Some(TableId::MethodDef) && attribute.parent.row == rid;
        let property = attribute.parent.table == Some(TableId::Property)
            && properties.contains(&attribute.parent.row);
        if (direct || property)
            && kind(resolver, method.assembly, attribute)? == Some(Kind::Intrinsic)
        {
            return Err("Koh hardware intrinsic requires intrinsic lowering; desktop body cannot be compiled".into());
        }
    }
    Ok(())
}
