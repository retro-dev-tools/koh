use crate::resolve::{Definition, ResolvedMethod, Resolver};
use clrmeta::{MethodSig, TableId, TypeSig};
mod constraints;
mod construction;
pub(crate) use construction::Construction;

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Instance {
    pub method: Definition,
    pub arguments: Vec<Argument>,
}

fn charge(counter: &std::cell::Cell<usize>, amount: usize) -> Result<(), String> {
    let total = counter
        .get()
        .checked_add(amount)
        .filter(|&total| total <= 1_048_576)
        .ok_or("Generic instantiation work or storage exceeds1048576")?;
    counter.set(total);
    Ok(())
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Argument {
    pub assembly: usize,
    pub ty: TypeSig,
}

fn token_free(ty: &TypeSig) -> bool {
    match ty {
        TypeSig::Primitive(_) => true,
        TypeSig::SzArray(inner)
        | TypeSig::ByRef(inner)
        | TypeSig::Ptr(inner)
        | TypeSig::Pinned(inner)
        | TypeSig::Array {
            element_type: inner,
            ..
        } => token_free(inner),
        _ => false,
    }
}

pub(crate) fn scoped(
    assembly: usize,
    ty: &TypeSig,
    arguments: &[Argument],
) -> Result<Argument, String> {
    fn inner(
        assembly: usize,
        ty: &TypeSig,
        arguments: &[Argument],
        depth: usize,
        nodes: &mut usize,
    ) -> Result<Argument, String> {
        *nodes += 1;
        if *nodes > 65536 {
            return Err("Generic substitution exceeds65536 nodes".into());
        }
        if depth > 64 {
            return Err("Generic substitution exceeds64 levels".into());
        }
        let mut nested = |ty| inner(assembly, ty, arguments, depth + 1, nodes);
        let ty = match ty {
            TypeSig::MVar(index) => {
                let argument = arguments
                    .get(*index as usize)
                    .ok_or_else(|| format!("Unbound generic method parameter !!{index}"))?;
                return inner(argument.assembly, &argument.ty, &[], depth + 1, nodes);
            }
            TypeSig::Var(index) => {
                return Err(format!(
                    "Generic declaring type parameter !{index} requires instantiated storage"
                ));
            }
            TypeSig::SzArray(element)
            | TypeSig::ByRef(element)
            | TypeSig::Ptr(element)
            | TypeSig::Pinned(element) => {
                let element = nested(element)?;
                let wrapped = Box::new(element.ty);
                let ty = match ty {
                    TypeSig::SzArray(_) => TypeSig::SzArray(wrapped),
                    TypeSig::ByRef(_) => TypeSig::ByRef(wrapped),
                    TypeSig::Ptr(_) => TypeSig::Ptr(wrapped),
                    _ => TypeSig::Pinned(wrapped),
                };
                return Ok(Argument {
                    assembly: element.assembly,
                    ty,
                });
            }
            TypeSig::Array {
                element_type,
                rank,
                sizes,
                lo_bounds,
            } => {
                let element = nested(element_type)?;
                return Ok(Argument {
                    assembly: element.assembly,
                    ty: TypeSig::Array {
                        element_type: Box::new(element.ty),
                        rank: *rank,
                        sizes: sizes.clone(),
                        lo_bounds: lo_bounds.clone(),
                    },
                });
            }
            TypeSig::GenericInst {
                is_value_type,
                type_ref,
                type_args,
            } => {
                let args = type_args.iter().map(|argument| {
                    let argument = nested(argument)?;
                    if argument.assembly != assembly && !token_free(&argument.ty) {
                        return Err("Constructed generic signatures with foreign named arguments require scoped layout binding".into());
                    }
                    Ok(argument.ty)
                }).collect::<Result<_, String>>()?;
                TypeSig::GenericInst {
                    is_value_type: *is_value_type,
                    type_ref: *type_ref,
                    type_args: args,
                }
            }
            TypeSig::Modified {
                required,
                modifier,
                inner,
            } => {
                let value = nested(inner)?;
                if value.assembly != assembly && !token_free(&value.ty) {
                    return Err("Foreign generic arguments with custom modifiers require scoped modifier binding".into());
                }
                TypeSig::Modified {
                    required: *required,
                    modifier: *modifier,
                    inner: Box::new(value.ty),
                }
            }
            TypeSig::FnPtr(_) => {
                return Err("Generic function-pointer substitution is unsupported".into());
            }
            _ => ty.clone(),
        };
        Ok(Argument {
            assembly: if token_free(&ty) { 0 } else { assembly },
            ty,
        })
    }
    inner(assembly, ty, arguments, 0, &mut 0)
}

pub(crate) fn storage(
    resolver: &Resolver<'_>,
    assembly: usize,
    ty: &TypeSig,
    arguments: &[Argument],
) -> Result<Argument, String> {
    let mut result = scoped(assembly, ty, arguments)?;
    result.ty = crate::lower::storage_type(resolver, result.assembly, &result.ty)?;
    Ok(result)
}

pub(crate) struct MethodTypes {
    pub parameters: Vec<Argument>,
    pub returns: Argument,
    pub signature: crate::ir::Signature,
}

pub(crate) fn method_types(
    resolver: &Resolver<'_>,
    method: Definition,
    signature: &MethodSig,
    arguments: &[Argument],
) -> Result<MethodTypes, String> {
    if signature.generic_param_count as usize != arguments.len() {
        return Err("Generic method requires its closed argument list".into());
    }
    let signature = crate::lower::storage_signature(resolver, method, signature)?;
    if signature.calling_convention.0 & 0x0f != 0 || signature.params.len() > 1024 {
        return Err("Unsupported calling convention or parameter count".into());
    }
    let parameters = signature
        .params
        .iter()
        .map(|ty| storage(resolver, method.assembly, ty, arguments))
        .collect::<Result<Vec<_>, _>>()?;
    let returns = storage(resolver, method.assembly, &signature.return_type, arguments)?;
    let signature = crate::ir::Signature {
        parameters: parameters
            .iter()
            .map(|argument| crate::lower::bound_type(resolver, argument.assembly, &argument.ty))
            .collect::<Result<_, _>>()?,
        returns: if returns.ty == TypeSig::Primitive(clrmeta::ElementType::Void) {
            None
        } else {
            Some(crate::lower::bound_type(
                resolver,
                returns.assembly,
                &returns.ty,
            )?)
        },
    };
    Ok(MethodTypes {
        parameters,
        returns,
        signature,
    })
}

fn canonical(
    resolver: &Resolver<'_>,
    argument: Argument,
    depth: usize,
) -> Result<Argument, String> {
    if depth > 64 {
        return Err("Generic argument depth exceeds64".into());
    }
    let (assembly, ty) = match argument.ty {
        TypeSig::Primitive(_) => (0, argument.ty),
        TypeSig::Class(encoded) | TypeSig::ValueType(encoded) => {
            let definition =
                resolver.resolve_type(argument.assembly, crate::resolve::type_token(encoded)?)?;
            resolver.check_arity(definition, 0).map_err(|error| {
                format!("Generic named arguments require instantiated layouts: {error}")
            })?;
            let encoded = (definition.token & 0xffffff) << 2;
            (
                definition.assembly,
                if matches!(argument.ty, TypeSig::Class(_)) {
                    TypeSig::Class(encoded)
                } else {
                    TypeSig::ValueType(encoded)
                },
            )
        }
        TypeSig::SzArray(element) => {
            let element = canonical(
                resolver,
                Argument {
                    assembly: argument.assembly,
                    ty: *element,
                },
                depth + 1,
            )?;
            (element.assembly, TypeSig::SzArray(Box::new(element.ty)))
        }
        _ => return Err("Compound generic arguments require instantiated type binding".into()),
    };
    Ok(Argument { assembly, ty })
}

#[cfg(test)]
mod tests {
    use super::*;
    use clrmeta::ElementType;
    use rstest::rstest;

    fn substitute(ty: &TypeSig, arguments: &[TypeSig]) -> Result<TypeSig, String> {
        let arguments = arguments
            .iter()
            .cloned()
            .map(|ty| Argument { assembly: 0, ty })
            .collect::<Vec<_>>();
        scoped(0, ty, &arguments).map(|argument| argument.ty)
    }

    #[rstest]
    #[case(TypeSig::ValueType(12))]
    #[case(TypeSig::Class(12))]
    fn nested_substitution_preserves_foreign_scope(#[case] ty: TypeSig) {
        let arguments = [Argument {
            assembly: 7,
            ty: ty.clone(),
        }];
        let input = TypeSig::ByRef(Box::new(TypeSig::SzArray(Box::new(TypeSig::MVar(0)))));
        let result = scoped(2, &input, &arguments).unwrap();
        assert_eq!(result.assembly, 7);
        assert_eq!(
            result.ty,
            TypeSig::ByRef(Box::new(TypeSig::SzArray(Box::new(ty))))
        );
    }

    #[test]
    fn constructed_signatures_do_not_reinterpret_foreign_tokens() {
        let signature = TypeSig::GenericInst {
            is_value_type: false,
            type_ref: 8,
            type_args: vec![TypeSig::MVar(0)],
        };
        let foreign = Argument {
            assembly: 7,
            ty: TypeSig::Class(12),
        };
        assert!(
            scoped(2, &signature, &[foreign])
                .unwrap_err()
                .contains("foreign named")
        );
        let primitive = Argument {
            assembly: 7,
            ty: TypeSig::Primitive(ElementType::I4),
        };
        let result = scoped(2, &signature, &[primitive]).unwrap();
        assert_eq!(result.assembly, 2);
        assert_eq!(
            result.ty,
            TypeSig::GenericInst {
                is_value_type: false,
                type_ref: 8,
                type_args: vec![TypeSig::Primitive(ElementType::I4)]
            }
        );
    }

    #[rstest]
    #[case(TypeSig::MVar(0))]
    #[case(TypeSig::MVar(u32::MAX))]
    #[case(TypeSig::Var(0))]
    fn unbound_parameters_diagnose(#[case] ty: TypeSig) {
        assert!(substitute(&ty, &[]).unwrap_err().contains("parameter"));
    }

    #[test]
    fn substitution_preserves_wrappers_and_parameter_order() {
        let input = TypeSig::Array {
            element_type: Box::new(TypeSig::MVar(1)),
            rank: 2,
            sizes: vec![],
            lo_bounds: vec![0],
        };
        let expected = TypeSig::Array {
            element_type: Box::new(TypeSig::Primitive(ElementType::U8)),
            rank: 2,
            sizes: vec![],
            lo_bounds: vec![0],
        };
        assert_eq!(
            substitute(
                &input,
                &[
                    TypeSig::Primitive(ElementType::U1),
                    TypeSig::Primitive(ElementType::U8)
                ]
            )
            .unwrap(),
            expected
        );
    }

    #[rstest]
    #[case(63, true)]
    #[case(64, false)]
    #[case(65, false)]
    fn substituted_arguments_obey_depth_limit(#[case] count: usize, #[case] accepted: bool) {
        let mut argument = TypeSig::Primitive(ElementType::I4);
        for _ in 0..count {
            argument = TypeSig::SzArray(Box::new(argument));
        }
        assert_eq!(substitute(&TypeSig::MVar(0), &[argument]).is_ok(), accepted);
    }

    #[rstest]
    #[case(1_048_575, 1, true)]
    #[case(1_048_576, 1, false)]
    #[case(1, usize::MAX, false)]
    fn aggregate_budget_checks_before_mutating(
        #[case] before: usize,
        #[case] added: usize,
        #[case] accepted: bool,
    ) {
        let counter = std::cell::Cell::new(before);
        assert_eq!(charge(&counter, added).is_ok(), accepted);
        assert_eq!(
            counter.get(),
            if accepted { before + added } else { before }
        );
    }
}

impl Resolver<'_> {
    pub(crate) fn instance(&self, identity: Definition) -> Result<Option<Instance>, String> {
        if identity.token >> 24 != 0xfb {
            return Ok(None);
        }
        let index = (identity.token & 0xffffff)
            .checked_sub(1)
            .ok_or("Invalid generic instance identity")?;
        let instance = self
            .generic_instances
            .borrow()
            .get(index as usize)
            .cloned()
            .ok_or("Unknown generic instance identity")?;
        if instance.method.assembly != identity.assembly {
            return Err("Generic instance assembly mismatch".into());
        }
        Ok(Some(instance))
    }

    pub(crate) fn instantiate(
        &self,
        target: &ResolvedMethod,
        caller: &[Argument],
    ) -> Result<Definition, String> {
        if target.method_arguments.is_empty() && target.type_arguments.is_empty() {
            return Ok(target.definition);
        }
        if !target.type_arguments.is_empty() {
            return Err("Generic declaring types require instantiated storage".into());
        }
        if self
            .assembly(target.definition.assembly)?
            .method_signature(target.definition.token)?
            .generic_param_count as usize
            != target.method_arguments.len()
        {
            return Err("Generic method arity mismatch".into());
        }
        let arguments = target
            .method_arguments
            .iter()
            .map(|ty| canonical(self, scoped(target.argument_assembly, ty, caller)?, 0))
            .collect::<Result<Vec<_>, _>>()?;
        self.specialize(target.definition, arguments)
    }

    pub(crate) fn specialize(
        &self,
        method: Definition,
        arguments: Vec<Argument>,
    ) -> Result<Definition, String> {
        let image = self.assembly(method.assembly)?;
        let work = image
            .metadata()
            .generic_params
            .len()
            .checked_mul(image.metadata().generic_param_constraints.len() + 2)
            .ok_or("Generic constraint validation work overflow")?;
        charge(&self.generic_work, work)?;
        let signature = image.method_signature(method.token)?;
        if signature.generic_param_count as usize != arguments.len() {
            return Err("Generic method arity mismatch".into());
        }
        let owner = crate::lower::owner_type(self, method)?;
        if image.metadata().generic_params.iter().any(|p| {
            p.owner.table == Some(TableId::TypeDef) && p.owner.row == owner.token & 0xffffff
        }) {
            return Err("Generic declaring types require instantiated storage".into());
        }
        let nodes = arguments
            .iter()
            .map(|argument| {
                let mut current = &argument.ty;
                let mut count = 1usize;
                while let TypeSig::SzArray(inner) = current {
                    count += 1;
                    current = inner;
                }
                count
            })
            .sum::<usize>();
        charge(
            &self.generic_work,
            nodes.saturating_mul(self.generic_instances.borrow().len() + 1),
        )?;
        for argument in &arguments {
            let ty = crate::lower::storage_type(self, argument.assembly, &argument.ty)?;
            crate::lower::bound_type(self, argument.assembly, &ty)?;
        }
        constraints::validate(self, method, &arguments)?;
        let instance = Instance { method, arguments };
        let mut instances = self.generic_instances.borrow_mut();
        let index = if let Some(index) = instances.iter().position(|value| value == &instance) {
            index
        } else {
            if instances.len() >= 4096 {
                return Err("Generic instantiation count exceeds4096".into());
            }
            charge(&self.generic_nodes, nodes)?;
            instances.push(instance);
            instances.len() - 1
        };
        Ok(Definition {
            assembly: method.assembly,
            token: 0xfb000001 + index as u32,
        })
    }
}
