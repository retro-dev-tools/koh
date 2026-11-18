use super::charge;
use crate::resolve::{Definition, Resolver, coded, type_token};
use clrmeta::{TableId, TypeSig};

pub(crate) fn parameters(resolver: &Resolver<'_>, owner: Definition) -> Result<Vec<u16>, String> {
    let owner = resolver.type_definition(owner)?;
    let metadata = resolver.assembly(owner.assembly)?.metadata();
    charge(&resolver.generic_work, metadata.generic_params.len())?;
    let mut parameters: Vec<_> = metadata
        .generic_params
        .iter()
        .filter(|p| {
            p.owner.table == Some(TableId::TypeDef) && p.owner.row == owner.token & 0xffffff
        })
        .collect();
    parameters.sort_by_key(|p| p.number);
    for (index, parameter) in parameters.iter().enumerate() {
        if parameter.number as usize != index {
            return Err("Invalid generic variance parameter numbering".into());
        }
    }
    Ok(parameters.iter().map(|p| p.flags & 3).collect())
}

fn signature(
    resolver: &Resolver<'_>,
    assembly: usize,
    ty: &TypeSig,
    flags: &[u16],
    position: i8,
    depth: usize,
) -> Result<(), String> {
    charge(&resolver.generic_work, 1)?;
    if depth > 64 {
        return Err("Variance signature exceeds64 levels".into());
    }
    let check = |ty, position| signature(resolver, assembly, ty, flags, position, depth + 1);
    match ty {
        TypeSig::Var(index) => {
            let variance = *flags
                .get(*index as usize)
                .ok_or("Invalid variance type parameter")?;
            if variance == 3 || variance == 1 && position != 1 || variance == 2 && position != -1 {
                return Err("Invalid variance position in interface signature".into());
            }
        }
        TypeSig::SzArray(inner)
        | TypeSig::Array {
            element_type: inner,
            ..
        } => check(inner, position)?,
        TypeSig::ByRef(inner) | TypeSig::Ptr(inner) => check(inner, 0)?,
        TypeSig::GenericInst {
            type_ref,
            type_args,
            ..
        } => {
            let owner = resolver.resolve_type(assembly, type_token(*type_ref)?)?;
            let nested = parameters(resolver, owner)?;
            if nested.len() != type_args.len() {
                return Err("Invalid variance generic arity".into());
            }
            for (ty, variance) in type_args.iter().zip(nested) {
                let position = match variance {
                    0 => 0,
                    1 => position,
                    2 => -position,
                    _ => return Err("Invalid nested generic variance flags".into()),
                };
                check(ty, position)?;
            }
        }
        TypeSig::FnPtr(_) | TypeSig::Modified { .. } | TypeSig::Pinned(_) => {
            return Err("Unsupported signature form in variant interface".into());
        }
        _ => {}
    }
    Ok(())
}

pub(super) fn validate(resolver: &Resolver<'_>, owner: Definition) -> Result<(), String> {
    let flags = parameters(resolver, owner)?;
    if flags.iter().all(|&flag| flag == 0) {
        return Ok(());
    }
    let image = resolver.assembly(owner.assembly)?;
    let metadata = image.metadata();
    let index = (owner.token & 0xffffff) as usize - 1;
    let ty = &metadata.type_defs[index];
    let end = metadata
        .type_defs
        .get(index + 1)
        .map_or(metadata.method_defs.len() as u32 + 1, |t| t.method_list);
    charge(
        &resolver.generic_work,
        metadata.interface_impls.len() + metadata.generic_param_constraints.len(),
    )?;
    for row in metadata
        .interface_impls
        .iter()
        .filter(|r| r.class == index as u32 + 1)
    {
        signature(
            resolver,
            owner.assembly,
            &resolver.type_signature(owner.assembly, coded(row.interface)?)?,
            &flags,
            1,
            0,
        )?;
    }
    for method in ty.method_list..end {
        let row = method
            .checked_sub(1)
            .and_then(|index| metadata.method_defs.get(index as usize))
            .ok_or("Invalid variant interface method range")?;
        charge(
            &resolver.generic_work,
            metadata.generic_params.len() + metadata.generic_param_constraints.len(),
        )?;
        if row.flags & 0x10 != 0 && row.flags & 0x40 == 0 {
            continue;
        }
        let sig = image.method_signature(0x06000000 | method)?;
        signature(resolver, owner.assembly, &sig.return_type, &flags, 1, 0)?;
        for param in &sig.params {
            signature(resolver, owner.assembly, param, &flags, -1, 0)?;
        }
        for (i, _) in metadata
            .generic_params
            .iter()
            .enumerate()
            .filter(|(_, p)| p.owner.table == Some(TableId::MethodDef) && p.owner.row == method)
        {
            charge(
                &resolver.generic_work,
                metadata.generic_param_constraints.len(),
            )?;
            for constraint in metadata
                .generic_param_constraints
                .iter()
                .filter(|c| c.owner as usize == i + 1)
            {
                signature(
                    resolver,
                    owner.assembly,
                    &resolver.type_signature(owner.assembly, coded(constraint.constraint)?)?,
                    &flags,
                    -1,
                    0,
                )?;
            }
        }
    }
    Ok(())
}
