use crate::{
    ir::{ArrayKind, DelegateType, FieldStorage, Signature, Type},
    resolve::{DelegateDefinition, Resolver},
};

pub(crate) struct Callback {
    pub kind: DelegateType,
    pub target: crate::resolve::Definition,
    pub signature: Signature,
}

pub(crate) fn construction_sequences(
    cil: &[crate::cil::Instruction],
) -> std::collections::HashMap<u32, bool> {
    use crate::cil::Operand;
    let targets: std::collections::HashSet<_> = cil
        .iter()
        .flat_map(|instruction| match &instruction.operand {
            Operand::Target(target) => std::slice::from_ref(target),
            Operand::Targets(targets) => targets.as_slice(),
            _ => &[],
        })
        .copied()
        .collect();
    let mut sequences = std::collections::HashMap::new();
    for (index, pair) in cil.windows(2).enumerate() {
        if pair[1].opcode != 0x73 || targets.contains(&pair[1].offset) {
            continue;
        }
        if pair[0].opcode == 0xfe06 {
            sequences.insert(pair[1].offset, false);
        } else if pair[0].opcode == 0xfe07
            && !targets.contains(&pair[0].offset)
            && index != 0
            && cil[index - 1].opcode == 0x25
        {
            sequences.insert(pair[1].offset, true);
        }
    }
    sequences
}

#[cfg(test)]
mod tests {
    use super::construction_sequences;
    use crate::cil::{Instruction, Operand};
    use rstest::rstest;

    #[rstest]
    #[case(None, true)]
    #[case(Some(0), true)]
    #[case(Some(1), false)]
    #[case(Some(7), false)]
    fn virtual_constructor_sequence_cannot_be_entered_after_dup(
        #[case] branch: Option<u32>,
        #[case] valid: bool,
    ) {
        let mut cil = vec![
            Instruction {
                offset: 0,
                opcode: 0x25,
                operand: Operand::None,
            },
            Instruction {
                offset: 1,
                opcode: 0xfe07,
                operand: Operand::Token(0x06000001),
            },
            Instruction {
                offset: 7,
                opcode: 0x73,
                operand: Operand::Token(0x0a000001),
            },
        ];
        if let Some(target) = branch {
            cil.push(Instruction {
                offset: 12,
                opcode: 0x38,
                operand: Operand::Target(target),
            });
        }
        assert_eq!(
            construction_sequences(&cil).get(&7).copied(),
            valid.then_some(true)
        );
    }
}

pub(crate) fn callback(
    resolver: &Resolver<'_>,
    kind: DelegateType,
    target: crate::resolve::Definition,
) -> Result<crate::resolve::Definition, String> {
    use crate::ir::ClassType;
    let declared = if crate::dispatch::is_dispatch(target) {
        crate::dispatch::declaration(target)
    } else {
        target
    };
    let bound = declared;
    let specialization = resolver.instance(declared)?;
    let declared = specialization
        .as_ref()
        .map_or(declared, |instance| instance.method);
    let arguments = specialization
        .as_ref()
        .map_or(&[][..], |instance| instance.arguments.as_slice());
    let owner_arguments = specialization
        .as_ref()
        .and_then(|instance| instance.declaring)
        .map(|owner| resolver.type_instance(owner))
        .transpose()?
        .flatten()
        .map_or_else(Vec::new, |instance| instance.arguments);
    let image = resolver.assembly(declared.assembly)?;
    let raw = image.method_signature(declared.token)?;
    let actual = crate::generics::method_types(resolver, bound, &raw, arguments)?.signature;
    let invoke = signature(resolver, kind)?;
    let instance = raw.calling_convention.has_this();
    if actual.parameters.get(usize::from(instance)..) != Some(invoke.parameters.as_slice())
        || actual.returns != invoke.returns
    {
        return Err("Delegate target signature differs from Invoke".into());
    }
    let bindings = resolver.delegate_types.borrow();
    let binding = &bindings[usize::from(kind.instance() - 1)];
    let expected_raw = binding.metadata.signature.clone();
    let expected_assembly = binding.metadata.identity.assembly;
    let type_arguments = binding.metadata.type_arguments.clone();
    drop(bindings);
    for (expected, actual) in expected_raw.params.iter().zip(&raw.params) {
        let actual =
            resolver.bind_signature(declared.assembly, actual, arguments, &owner_arguments)?;
        if !type_matches(
            resolver,
            expected_assembly,
            expected,
            &type_arguments,
            actual.assembly,
            &actual.ty,
        )? {
            return Err("Delegate target parameter type differs from Invoke".into());
        }
    }
    let returns = resolver.bind_signature(
        declared.assembly,
        &raw.return_type,
        arguments,
        &owner_arguments,
    )?;
    if invoke.returns.is_some()
        && !type_matches(
            resolver,
            expected_assembly,
            &expected_raw.return_type,
            &type_arguments,
            returns.assembly,
            &returns.ty,
        )?
    {
        return Err("Delegate target return type differs from Invoke".into());
    }
    let entries = resolver.callbacks.borrow();
    resolver.charge_delegate_work(entries.len() + 1)?;
    if let Some(index) = entries
        .iter()
        .position(|entry| entry.kind == kind && entry.target == target)
    {
        return Ok(crate::resolve::Definition {
            assembly: 0,
            token: 0xfc000001 + index as u32,
        });
    }
    if entries.len() >= 4096 {
        return Err("Delegate callback count exceeds4096".into());
    }
    let identity = crate::resolve::Definition {
        assembly: 0,
        token: 0xfc000001 + entries.len() as u32,
    };
    drop(entries);
    let mut parameters = vec![Type::Object(ClassType::root())];
    parameters.extend(invoke.parameters);
    resolver.callbacks.borrow_mut().push(Callback {
        kind,
        target,
        signature: Signature {
            parameters,
            returns: invoke.returns,
        },
    });
    Ok(identity)
}

fn type_matches(
    resolver: &Resolver<'_>,
    expected_assembly: usize,
    expected: &clrmeta::TypeSig,
    arguments: &[crate::generics::Argument],
    actual_assembly: usize,
    actual: &clrmeta::TypeSig,
) -> Result<bool, String> {
    resolver.charge_delegate_work(arguments.len() + 1)?;
    let expected = resolver.bind_signature(expected_assembly, expected, &[], arguments)?;
    let actual = resolver.bind_signature(actual_assembly, actual, &[], &[])?;
    resolver.types_equal(
        expected.assembly,
        &expected.ty,
        actual.assembly,
        &actual.ty,
        0,
    )
}

pub(crate) fn function(
    resolver: &Resolver<'_>,
    identity: crate::resolve::Definition,
) -> Result<crate::ir::Function, String> {
    use crate::ir::*;
    let entries = resolver.callbacks.borrow();
    let binding = entries
        .get(
            ((identity.token & 0xffffff)
                .checked_sub(1)
                .ok_or("Invalid callback identity")?) as usize,
        )
        .ok_or("Missing callback identity")?;
    let target = binding.target;
    let signature = binding.signature.clone();
    drop(entries);
    let declared = if crate::dispatch::is_dispatch(target) {
        crate::dispatch::declaration(target)
    } else {
        target
    };
    let bound = declared;
    let specialization = resolver.instance(declared)?;
    let declared = specialization
        .as_ref()
        .map_or(declared, |instance| instance.method);
    let arguments = specialization
        .as_ref()
        .map_or(&[][..], |instance| instance.arguments.as_slice());
    let raw = resolver
        .assembly(declared.assembly)?
        .method_signature(declared.token)?;
    let actual = crate::generics::method_types(resolver, bound, &raw, arguments)?.signature;
    let mut next = signature.parameters.len() as u32;
    let mut instructions = Vec::new();
    let mut arguments = Vec::new();
    if raw.calling_convention.has_this() {
        let Some(Type::Object(class)) = actual.parameters.first().copied() else {
            return Err("Delegate instance target requires a reference-class receiver".into());
        };
        instructions.push(Instruction {
            result: Some((next, Type::Object(class))),
            op: Op::CastClass { object: 0, class },
            il_offset: 0,
        });
        arguments.push(next);
        next += 1;
    }
    arguments.extend(1..signature.parameters.len() as u32);
    let result = actual.returns.map(|ty| (next, ty));
    instructions.push(Instruction {
        result,
        op: Op::Call {
            target,
            signature: actual,
            arguments,
        },
        il_offset: 0,
    });
    let function = Function {
        identity,
        parameters: signature.parameters.clone(),
        returns: signature.returns,
        locals: vec![],
        blocks: vec![Block {
            parameters: signature
                .parameters
                .iter()
                .enumerate()
                .map(|(i, &ty)| (i as u32, ty))
                .collect(),
            instructions,
            terminator: Terminator {
                il_offset: 0,
                kind: Control::Return(result.map(|(id, _)| id)),
            },
        }],
    };
    verify(&function)?;
    Ok(function)
}

pub(crate) struct BoundDelegate {
    pub kind: DelegateType,
    pub arguments: Vec<FieldStorage>,
    pub metadata: DelegateDefinition,
}

pub(crate) fn signature(resolver: &Resolver<'_>, kind: DelegateType) -> Result<Signature, String> {
    let entries = resolver.delegate_types.borrow();
    let binding = entries
        .get(usize::from(kind.instance() - 1))
        .filter(|binding| binding.kind == kind)
        .ok_or("Missing delegate type binding")?;
    let metadata = binding.metadata.clone();
    let arguments = binding.arguments.clone();
    drop(entries);
    if metadata.signature.params.len() > 1024 {
        return Err("Delegate parameter count exceeds1024".into());
    }
    let parameters = metadata
        .signature
        .params
        .iter()
        .map(|ty| argument_type(resolver, metadata.identity.assembly, ty, &arguments))
        .collect::<Result<_, _>>()?;
    let returns = if metadata.signature.return_type
        == clrmeta::TypeSig::Primitive(clrmeta::ElementType::Void)
    {
        None
    } else {
        Some(argument_type(
            resolver,
            metadata.identity.assembly,
            &metadata.signature.return_type,
            &arguments,
        )?)
    };
    Ok(Signature {
        parameters,
        returns,
    })
}

fn argument_type(
    resolver: &Resolver<'_>,
    assembly: usize,
    ty: &clrmeta::TypeSig,
    arguments: &[FieldStorage],
) -> Result<Type, String> {
    use clrmeta::TypeSig;
    let argument = |index: u32| {
        arguments
            .get(index as usize)
            .copied()
            .ok_or("Unbound delegate type parameter")
    };
    Ok(match ty {
        TypeSig::Var(index) => argument(*index)?.stack_type(),
        TypeSig::ByRef(inner) if matches!(**inner, TypeSig::Var(_)) => {
            let TypeSig::Var(index) = **inner else {
                unreachable!()
            };
            argument(index)?.reference_type()
        }
        TypeSig::SzArray(inner) if matches!(**inner, TypeSig::Var(_)) => {
            let TypeSig::Var(index) = **inner else {
                unreachable!()
            };
            let FieldStorage::Value(element) = argument(index)? else {
                return Err(
                    "Nested array delegate argument requires reference-element storage".into(),
                );
            };
            Type::Array(element, ArrayKind::Vector)
        }
        _ => crate::lower::bound_type(
            resolver,
            assembly,
            &crate::lower::storage_type(resolver, assembly, ty)?,
        )?,
    })
}

pub(crate) fn member(
    resolver: &Resolver<'_>,
    assembly: usize,
    token: u32,
    methods: &[crate::generics::Argument],
    types: &[crate::generics::Argument],
) -> Result<Option<(DelegateType, bool, Signature)>, String> {
    use clrmeta::{TableId, TypeSig};
    let metadata = resolver.assembly(assembly)?.metadata();
    let ty = match token >> 24 {
        6 => {
            let owner =
                crate::lower::owner_type(resolver, crate::resolve::Definition { assembly, token })?;
            TypeSig::Class((owner.token & 0xffffff) << 2)
        }
        0x0a => {
            let member = metadata
                .member_refs
                .get(
                    (token & 0xffffff)
                        .checked_sub(1)
                        .ok_or("Invalid delegate member token")? as usize,
                )
                .ok_or("Invalid delegate member token")?;
            match member.class.table {
                Some(TableId::TypeDef) => TypeSig::Class(member.class.row << 2),
                Some(TableId::TypeRef) => TypeSig::Class((member.class.row << 2) | 1),
                Some(TableId::TypeSpec) => {
                    resolver.type_signature(assembly, 0x1b000000 | member.class.row)?
                }
                _ => return Ok(None),
            }
        }
        _ => return Ok(None),
    };
    if resolver.delegate_signature_type(assembly, &ty)?.is_none() {
        return Ok(None);
    }
    let ty = resolver.bind_signature(assembly, &ty, methods, types)?;
    let Some(kind) = bind(resolver, ty.assembly, &ty.ty)? else {
        return Ok(None);
    };
    let resolved = resolver.resolve_method(assembly, token)?;
    let entries = resolver.delegate_types.borrow();
    let binding = &entries[usize::from(kind.instance() - 1)];
    let constructing = if resolved.definition == binding.metadata.constructor {
        true
    } else if resolved.definition == binding.metadata.invoke {
        false
    } else {
        return Err("Delegate member requires explicit runtime lowering".into());
    };
    drop(entries);
    Ok(Some((kind, constructing, signature(resolver, kind)?)))
}

pub(crate) fn bind(
    resolver: &Resolver<'_>,
    assembly: usize,
    ty: &clrmeta::TypeSig,
) -> Result<Option<DelegateType>, String> {
    if resolver.delegate_signature_type(assembly, ty)?.is_none() {
        return Ok(None);
    }
    let bound = resolver.bind_signature(assembly, ty, &[], &[])?;
    let metadata = resolver
        .delegate_signature_type(bound.assembly, &bound.ty)?
        .ok_or("Bound delegate lost its nominal identity")?;
    let arguments = metadata
        .type_arguments
        .iter()
        .map(|argument| crate::lower::field_storage(resolver, argument.assembly, &argument.ty))
        .collect::<Result<Vec<_>, _>>()?;
    let entries = resolver.delegate_types.borrow();
    resolver.charge_delegate_work(entries.len() + arguments.len() + 1)?;
    for found in entries.iter().filter(|entry| {
        entry.metadata.identity == metadata.identity
            && entry.metadata.type_arguments.len() == metadata.type_arguments.len()
    }) {
        let mut equal = true;
        for (left, right) in found
            .metadata
            .type_arguments
            .iter()
            .zip(&metadata.type_arguments)
        {
            if !resolver.types_equal(left.assembly, &left.ty, right.assembly, &right.ty, 0)? {
                equal = false;
                break;
            }
        }
        if equal {
            if found.arguments != arguments {
                return Err("Inconsistent storage for one nominal delegate type".into());
            }
            return Ok(Some(found.kind));
        }
    }
    if entries.len() >= 4096 {
        return Err("Delegate type count exceeds4096".into());
    }
    let kind = DelegateType::new(metadata.identity, entries.len() as u16 + 1)?;
    drop(entries);
    resolver.delegate_types.borrow_mut().push(BoundDelegate {
        kind,
        arguments,
        metadata,
    });
    Ok(Some(kind))
}
