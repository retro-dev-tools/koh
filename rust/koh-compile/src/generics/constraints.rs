use super::{Argument, canonical, charge};
use crate::resolve::{Definition, Resolver, coded, framework_type};
use clrmeta::{ElementType, TableId, TypeSig};

fn value(argument: &Argument) -> bool {
    matches!(argument.ty, TypeSig::ValueType(_))
        || matches!(argument.ty, TypeSig::Primitive(element) if !matches!(element, ElementType::Object | ElementType::String | ElementType::Void))
}

fn enumeration(resolver: &Resolver<'_>, argument: &Argument) -> Result<bool, String> {
    let TypeSig::ValueType(encoded) = argument.ty else {
        return Ok(false);
    };
    let definition = resolver.signature_type(argument.assembly, encoded)?;
    let definition = resolver.type_definition(definition)?;
    let metadata = resolver.assembly(definition.assembly)?.metadata();
    let ty = &metadata.type_defs[(definition.token & 0xffffff) as usize - 1];
    Ok(framework_type(metadata, coded(ty.extends)?)? == Some(("System", "Enum")))
}

fn framework_ancestor(
    resolver: &Resolver<'_>,
    argument: &Argument,
    wanted: &str,
) -> Result<bool, String> {
    let TypeSig::Class(encoded) = argument.ty else {
        return Ok(false);
    };
    let mut current = Some(resolver.signature_type(argument.assembly, encoded)?);
    for _ in 0..64 {
        let Some(definition) = current else {
            return Ok(false);
        };
        charge(&resolver.generic_work, resolver.assembly_count() + 1)?;
        if resolver.framework_definition(definition)? == Some(("System", wanted)) {
            return Ok(true);
        }
        current = crate::layout::class(resolver, definition)?.base;
    }
    Err("Framework constraint ancestry exceeds64".into())
}

pub(super) fn constructor(resolver: &Resolver<'_>, argument: &Argument) -> Result<bool, String> {
    if value(argument) || argument.ty == TypeSig::Primitive(ElementType::Object) {
        return Ok(true);
    }
    Ok(parameterless(resolver, argument)?.is_some())
}

pub(super) fn parameterless(
    resolver: &Resolver<'_>,
    argument: &Argument,
) -> Result<Option<Definition>, String> {
    let (TypeSig::Class(encoded) | TypeSig::ValueType(encoded)) = argument.ty else {
        return Ok(None);
    };
    let owner = resolver.signature_type(argument.assembly, encoded)?;
    let definition = resolver.type_definition(owner)?;
    let image = resolver.assembly(definition.assembly)?;
    let metadata = image.metadata();
    let row = (definition.token & 0xffffff) as usize - 1;
    let ty = &metadata.type_defs[row];
    if ty.flags & (0x20 | 0x80) != 0 {
        return Ok(None);
    }
    let end = metadata
        .type_defs
        .get(row + 1)
        .map_or(metadata.method_defs.len() as u32 + 1, |ty| ty.method_list);
    charge(&resolver.generic_work, (end - ty.method_list) as usize)?;
    for method in ty.method_list..end {
        let row = image.method(0x06000000 | method)?;
        if matches!(argument.ty, TypeSig::ValueType(_))
            && row.flags & 7 != 6
            && metadata.strings.get(row.name).map_err(|e| e.to_string())? == ".ctor"
        {
            let signature = image.method_signature(0x06000000 | method)?;
            if signature.params.is_empty() {
                return Err(
                    "Nonpublic value-type constructor requires explicit activation semantics"
                        .into(),
                );
            }
        }
        if row.flags & 0x1817 != 0x1806
            || metadata.strings.get(row.name).map_err(|e| e.to_string())? != ".ctor"
        {
            continue;
        }
        if row.flags & 0x440 != 0 {
            return Err("Invalid abstract/virtual constructor in generic argument".into());
        }
        let signature = image.method_signature(0x06000000 | method)?;
        if signature.generic_param_count == 0
            && signature.params.is_empty()
            && signature.return_type == TypeSig::Primitive(ElementType::Void)
        {
            if signature.calling_convention.0 != 0x20 {
                return Err(
                    "Constructor calling convention requires explicit constraint validation".into(),
                );
            }
            let method = Definition {
                assembly: definition.assembly,
                token: 0x06000000 | method,
            };
            return Ok(Some(if owner == definition {
                method
            } else {
                resolver.specialize_on(method, Vec::new(), Some(owner))?
            }));
        }
    }
    Ok(None)
}

pub(crate) fn assignable(
    resolver: &Resolver<'_>,
    argument: &Argument,
    constraint: &Argument,
) -> Result<bool, String> {
    if argument == constraint || constraint.ty == TypeSig::Primitive(ElementType::Object) {
        return Ok(true);
    }
    let TypeSig::Class(encoded) = constraint.ty else {
        if matches!(constraint.ty, TypeSig::SzArray(_) | TypeSig::Array { .. }) {
            return Err(
                "Nonidentical array constraints require covariance/reduced-type validation".into(),
            );
        }
        return Ok(false);
    };
    let required = resolver.signature_type(constraint.assembly, encoded)?;
    let required_layout = crate::layout::class(resolver, required)?;
    match argument.ty {
        TypeSig::Class(encoded) => {
            let mut current = Some(resolver.signature_type(argument.assembly, encoded)?);
            for _ in 0..64 {
                let Some(definition) = current else {
                    return Ok(false);
                };
                if definition == required {
                    return Ok(true);
                }
                let layout = crate::layout::class(resolver, definition)?;
                charge(&resolver.generic_work, layout.interfaces.len() + 1)?;
                if required_layout.interface && layout.interfaces.contains(&required) {
                    return Ok(true);
                }
                current = layout.base;
            }
            Err("Constraint ancestry exceeds64".into())
        }
        TypeSig::ValueType(encoded) if required_layout.interface => {
            let definition = resolver.signature_type(argument.assembly, encoded)?;
            for actual in resolver.declared_interfaces(definition)? {
                let layout = crate::layout::class(resolver, actual)?;
                if !layout.interface {
                    return Err("Value-type InterfaceImpl target is not an interface".into());
                }
                charge(&resolver.generic_work, layout.interfaces.len() + 1)?;
                if actual == required || layout.interfaces.contains(&required) {
                    return Ok(true);
                }
            }
            Ok(false)
        }
        _ => Ok(false),
    }
}

pub(crate) fn validate(
    resolver: &Resolver<'_>,
    method: Definition,
    arguments: &[Argument],
    declaring_arguments: &[Argument],
) -> Result<(), String> {
    let metadata = resolver.assembly(method.assembly)?.metadata();
    let owner_table = if method.token >> 24 == 2 {
        TableId::TypeDef
    } else {
        TableId::MethodDef
    };
    charge(&resolver.generic_work, metadata.generic_params.len())?;
    let parameters: Vec<_> = metadata
        .generic_params
        .iter()
        .enumerate()
        .filter(|(_, parameter)| {
            parameter.owner.table == Some(owner_table)
                && parameter.owner.row == method.token & 0xffffff
        })
        .collect();
    let work = parameters
        .len()
        .checked_mul(
            metadata
                .generic_param_constraints
                .len()
                .checked_add(1)
                .ok_or("Generic constraint validation work overflow")?,
        )
        .ok_or("Generic constraint validation work overflow")?;
    charge(&resolver.generic_work, work)?;
    let (methods, types) = if owner_table == TableId::TypeDef {
        (&[][..], arguments)
    } else {
        (arguments, declaring_arguments)
    };
    let interface = owner_table == TableId::TypeDef
        && metadata.type_defs[(method.token & 0xffffff) as usize - 1].flags & 0x20 != 0;
    let variant =
        interface || owner_table == TableId::TypeDef && resolver.delegate_owner(method)?;
    for (_, parameter) in &parameters {
        let allowed = if variant { 0x3f } else { 0x3c };
        if parameter.flags & !allowed != 0
            || parameter.flags & 3 == 3
            || parameter.flags & 0x0c == 0x0c
        {
            return Err(if owner_table == TableId::TypeDef {
                "Invalid generic type parameter flags"
            } else {
                "Invalid generic method parameter flags"
            }
            .into());
        }
    }
    if variant {
        super::variance::validate(resolver, method)?;
    }
    for (index, parameter) in parameters {
        let argument = arguments
            .get(parameter.number as usize)
            .ok_or("Missing constrained generic argument")?;
        if let TypeSig::ValueType(encoded) = argument.ty {
            let owner = resolver.signature_type(argument.assembly, encoded)?;
            if crate::contracts::byref_like(resolver, owner)? {
                return Err(if parameter.flags & 0x20 != 0 {
                    "Byref-like generic arguments require ref-safety validation"
                } else {
                    "Byref-like generic argument requires AllowByRefLike"
                }
                .into());
            }
        }
        let failed = parameter.flags & 4 != 0 && value(argument)
            || parameter.flags & 8 != 0 && !value(argument)
            || parameter.flags & 16 != 0 && !constructor(resolver, argument)?;
        if failed {
            return Err(format!(
                "Generic argument {}{} does not satisfy special constraints",
                if owner_table == TableId::TypeDef {
                    "!"
                } else {
                    "!!"
                },
                parameter.number
            ));
        }
        let mut bases = 0;
        for constraint in metadata
            .generic_param_constraints
            .iter()
            .filter(|row| row.owner as usize == index + 1)
        {
            let token = coded(constraint.constraint)?;
            let framework = framework_type(metadata, token)?;
            if matches!(
                framework,
                Some(("System", "Object" | "ValueType" | "Enum" | "Array"))
            ) {
                bases += 1;
                if bases > 1 {
                    return Err("Multiple class constraints on generic parameter".into());
                }
            }
            let supported = match framework {
                Some(("System", "Object")) => true,
                Some(("System", "ValueType")) => {
                    value(argument) || framework_ancestor(resolver, argument, "ValueType")?
                }
                Some(("System", "Enum")) => {
                    enumeration(resolver, argument)?
                        || framework_ancestor(resolver, argument, "Enum")?
                }
                Some(("System", "Array")) => {
                    matches!(argument.ty, TypeSig::SzArray(_) | TypeSig::Array { .. })
                        || framework_ancestor(resolver, argument, "Array")?
                }
                Some(("System", "Void")) => {
                    return Err("Generic constraint cannot reference System.Void".into());
                }
                _ => {
                    let ty = resolver.type_signature(method.assembly, token)?;
                    let parameter = matches!(ty, TypeSig::MVar(_) | TypeSig::Var(_));
                    let constraint = canonical(
                        resolver,
                        resolver.bind_signature(method.assembly, &ty, methods, types)?,
                        0,
                    )?;
                    if !parameter {
                        let base = if let TypeSig::Class(encoded) = constraint.ty {
                            let definition =
                                resolver.signature_type(constraint.assembly, encoded)?;
                            !crate::layout::class(resolver, definition)?.interface
                        } else {
                            true
                        };
                        if base {
                            bases += 1;
                            if bases > 1 {
                                return Err(
                                    "Multiple class constraints on generic parameter".into()
                                );
                            }
                        }
                    }
                    assignable(resolver, argument, &constraint)?
                }
            };
            if !supported {
                return Err(format!(
                    "Generic argument !!{} does not satisfy type constraint 0x{token:08x}",
                    parameter.number
                ));
            }
        }
    }
    Ok(())
}
