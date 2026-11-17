use crate::resolve::{Definition, ResolvedMethod, Resolver};
use clrmeta::{MethodSig, TableId, TypeSig};
pub(crate) mod constraints;
mod construction;
pub(crate) use constraints::assignable;
pub(crate) use construction::Construction;

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Instance {
    pub method: Definition,
    pub arguments: Vec<Argument>,
    pub declaring: Option<Definition>,
}

pub(crate) fn charge(counter: &std::cell::Cell<usize>, amount: usize) -> Result<(), String> {
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

pub(crate) struct MethodTypes {
    pub parameters: Vec<Argument>,
    pub returns: Argument,
    pub signature: crate::ir::Signature,
}

pub(crate) fn storage_in(
    resolver: &Resolver<'_>,
    assembly: usize,
    ty: &TypeSig,
    methods: &[Argument],
    types: &[Argument],
) -> Result<Argument, String> {
    let mut result = resolver.bind_signature(assembly, ty, methods, types)?;
    result.ty = crate::lower::storage_type(resolver, result.assembly, &result.ty)?;
    Ok(result)
}

pub(crate) fn method_types(
    resolver: &Resolver<'_>,
    method: Definition,
    signature: &MethodSig,
    arguments: &[Argument],
) -> Result<MethodTypes, String> {
    let declaring = resolver
        .instance(method)?
        .and_then(|instance| instance.declaring);
    let type_arguments = declaring
        .map(|id| resolver.type_instance(id))
        .transpose()?
        .flatten()
        .map_or_else(Vec::new, |instance| instance.arguments);
    if signature.generic_param_count as usize != arguments.len() {
        return Err("Generic method requires its closed argument list".into());
    }
    if signature.calling_convention.0 & 0x0f != 0 || signature.params.len() > 1024 {
        return Err("Unsupported calling convention or parameter count".into());
    }
    let mut parameters = signature
        .params
        .iter()
        .map(|ty| storage_in(resolver, method.assembly, ty, arguments, &type_arguments))
        .collect::<Result<Vec<_>, _>>()?;
    if signature.calling_convention.has_this() {
        let owner = crate::lower::owner_type(resolver, method)?;
        let mut receiver = resolver.type_argument(owner)?;
        if matches!(receiver.ty, TypeSig::ValueType(_)) {
            receiver.ty = TypeSig::ByRef(Box::new(receiver.ty));
        }
        parameters.insert(0, receiver);
    }
    let returns = storage_in(
        resolver,
        method.assembly,
        &signature.return_type,
        arguments,
        &type_arguments,
    )?;
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
            let definition = resolver.signature_type(argument.assembly, encoded)?;
            if resolver.type_instance(definition)?.is_some() {
                return resolver.type_argument(definition);
            }
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
        TypeSig::Array {
            element_type,
            rank,
            sizes,
            lo_bounds,
        } => {
            let element = canonical(
                resolver,
                Argument {
                    assembly: argument.assembly,
                    ty: *element_type,
                },
                depth + 1,
            )?;
            (
                element.assembly,
                TypeSig::Array {
                    element_type: Box::new(element.ty),
                    rank,
                    sizes,
                    lo_bounds,
                },
            )
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

    #[rstest]
    #[case(TypeSig::ValueType(12))]
    #[case(TypeSig::Class(12))]
    fn rectangular_substitution_preserves_foreign_scope(#[case] ty: TypeSig) {
        let arguments = [Argument {
            assembly: 7,
            ty: ty.clone(),
        }];
        let input = TypeSig::Array {
            element_type: Box::new(TypeSig::MVar(0)),
            rank: 2,
            sizes: vec![],
            lo_bounds: vec![0, 0],
        };
        let result = scoped(2, &input, &arguments).unwrap();
        assert_eq!(result.assembly, 7);
        assert_eq!(
            result.ty,
            TypeSig::Array {
                element_type: Box::new(ty),
                rank: 2,
                sizes: vec![],
                lo_bounds: vec![0, 0],
            }
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

    pub(crate) fn instantiate_in(
        &self,
        target: &ResolvedMethod,
        caller: &[Argument],
        caller_types: &[Argument],
        caller_owner: Option<Definition>,
    ) -> Result<Definition, String> {
        let owner = crate::lower::owner_type(self, target.definition)?;
        let declaring = if !target.type_arguments.is_empty() {
            let arguments = target
                .type_arguments
                .iter()
                .map(|ty| self.bind_signature(target.argument_assembly, ty, caller, caller_types))
                .collect::<Result<Vec<_>, _>>()?;
            let value = matches!(self.type_argument(owner)?.ty, TypeSig::ValueType(_));
            Some(self.close_type(owner, arguments, value)?)
        } else if let Some(current) = caller_owner {
            (self.type_definition(current)? == owner).then_some(current)
        } else {
            None
        };
        if target.method_arguments.is_empty() && target.type_arguments.is_empty() {
            if declaring.is_none() {
                return Ok(target.definition);
            }
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
            .map(|ty| {
                canonical(
                    self,
                    self.bind_signature(target.argument_assembly, ty, caller, caller_types)?,
                    0,
                )
            })
            .collect::<Result<Vec<_>, _>>()?;
        self.specialize_on(target.definition, arguments, declaring)
    }

    pub(crate) fn specialize(
        &self,
        method: Definition,
        arguments: Vec<Argument>,
    ) -> Result<Definition, String> {
        self.specialize_on(method, arguments, None)
    }

    pub(crate) fn specialize_on(
        &self,
        method: Definition,
        arguments: Vec<Argument>,
        declaring: Option<Definition>,
    ) -> Result<Definition, String> {
        let image = self.assembly(method.assembly)?;
        let signature = image.method_signature(method.token)?;
        if signature.generic_param_count as usize != arguments.len() {
            return Err("Generic method arity mismatch".into());
        }
        let owner = crate::lower::owner_type(self, method)?;
        if declaring.is_none()
            && image.metadata().generic_params.iter().any(|p| {
                p.owner.table == Some(TableId::TypeDef) && p.owner.row == owner.token & 0xffffff
            })
        {
            return Err("Generic declaring types require instantiated storage".into());
        }
        let nodes = arguments
            .iter()
            .map(|argument| {
                let mut current = &argument.ty;
                let mut count = 1usize;
                while let TypeSig::SzArray(inner)
                | TypeSig::Array {
                    element_type: inner,
                    ..
                } = current
                {
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
        let declaring_arguments = declaring
            .map(|id| self.type_instance(id))
            .transpose()?
            .flatten()
            .map_or_else(Vec::new, |instance| instance.arguments);
        constraints::validate(self, method, &arguments, &declaring_arguments)?;
        if let Some(declaring) = declaring {
            if self.type_definition(declaring)? != owner {
                return Err("Closed method declaring type mismatch".into());
            }
        }
        let instance = Instance {
            method,
            arguments,
            declaring,
        };
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
