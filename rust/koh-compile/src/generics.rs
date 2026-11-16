use crate::resolve::{Definition, ResolvedMethod, Resolver};
use clrmeta::{MethodSig, TableId, TypeSig};

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Instance {
    pub method: Definition,
    pub arguments: Vec<TypeSig>,
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

pub(crate) fn substitute(ty: &TypeSig, arguments: &[TypeSig]) -> Result<TypeSig, String> {
    fn inner(
        ty: &TypeSig,
        arguments: &[TypeSig],
        depth: usize,
        nodes: &mut usize,
    ) -> Result<TypeSig, String> {
        *nodes += 1;
        if *nodes > 65536 {
            return Err("Generic substitution exceeds65536 nodes".into());
        }
        if depth > 64 {
            return Err("Generic substitution exceeds64 levels".into());
        }
        let mut nested = |ty| inner(ty, arguments, depth + 1, nodes);
        Ok(match ty {
            TypeSig::MVar(index) => {
                let argument = arguments
                    .get(*index as usize)
                    .ok_or_else(|| format!("Unbound generic method parameter !!{index}"))?;
                inner(argument, &[], depth + 1, nodes)?
            }
            TypeSig::Var(index) => {
                return Err(format!(
                    "Generic declaring type parameter !{index} requires instantiated storage"
                ));
            }
            TypeSig::SzArray(ty) => TypeSig::SzArray(Box::new(nested(ty)?)),
            TypeSig::ByRef(ty) => TypeSig::ByRef(Box::new(nested(ty)?)),
            TypeSig::Ptr(ty) => TypeSig::Ptr(Box::new(nested(ty)?)),
            TypeSig::Pinned(ty) => TypeSig::Pinned(Box::new(nested(ty)?)),
            TypeSig::Array {
                element_type,
                rank,
                sizes,
                lo_bounds,
            } => TypeSig::Array {
                element_type: Box::new(nested(element_type)?),
                rank: *rank,
                sizes: sizes.clone(),
                lo_bounds: lo_bounds.clone(),
            },
            TypeSig::GenericInst {
                is_value_type,
                type_ref,
                type_args,
            } => TypeSig::GenericInst {
                is_value_type: *is_value_type,
                type_ref: *type_ref,
                type_args: type_args.iter().map(nested).collect::<Result<_, _>>()?,
            },
            TypeSig::Modified {
                required,
                modifier,
                inner,
            } => TypeSig::Modified {
                required: *required,
                modifier: *modifier,
                inner: Box::new(nested(inner)?),
            },
            TypeSig::FnPtr(_) => {
                return Err("Generic function-pointer substitution is unsupported".into());
            }
            _ => ty.clone(),
        })
    }
    inner(ty, arguments, 0, &mut 0)
}

#[cfg(test)]
mod tests {
    use super::*;
    use clrmeta::ElementType;
    use rstest::rstest;

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

pub(crate) fn signature(signature: &MethodSig, arguments: &[TypeSig]) -> Result<MethodSig, String> {
    let mut result = signature.clone();
    result.params = signature
        .params
        .iter()
        .map(|ty| substitute(ty, arguments))
        .collect::<Result<_, _>>()?;
    result.return_type = substitute(&signature.return_type, arguments)?;
    result.generic_param_count = 0;
    result.calling_convention.0 &= !0x10;
    Ok(result)
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
        caller: &[TypeSig],
    ) -> Result<Definition, String> {
        if target.method_arguments.is_empty() && target.type_arguments.is_empty() {
            return Ok(target.definition);
        }
        if !target.type_arguments.is_empty() {
            return Err("Generic declaring types require instantiated storage".into());
        }
        let image = self.assembly(target.definition.assembly)?;
        let work = image
            .metadata()
            .generic_params
            .len()
            .checked_mul(image.metadata().generic_param_constraints.len() + 2)
            .ok_or("Generic constraint validation work overflow")?;
        charge(&self.generic_work, work)?;
        let signature = image.method_signature(target.definition.token)?;
        if signature.generic_param_count as usize != target.method_arguments.len() {
            return Err("Generic method arity mismatch".into());
        }
        if signature.calling_convention.has_this()
            || image.method(target.definition.token)?.flags & 0x10 == 0
        {
            return Err("Generic instance methods require instantiated dispatch".into());
        }
        let owner = crate::lower::owner_type(self, target.definition)?;
        if image.metadata().generic_params.iter().any(|p| {
            p.owner.table == Some(TableId::TypeDef) && p.owner.row == owner.token & 0xffffff
        }) {
            return Err("Generic declaring types require instantiated storage".into());
        }
        for (index, parameter) in image.metadata().generic_params.iter().enumerate() {
            if parameter.owner.table == Some(TableId::MethodDef)
                && parameter.owner.row == target.definition.token & 0xffffff
                && (parameter.flags != 0
                    || image
                        .metadata()
                        .generic_param_constraints
                        .iter()
                        .any(|c| c.owner as usize == index + 1))
            {
                return Err("Generic constraints require instantiated validation".into());
            }
        }
        let arguments = target
            .method_arguments
            .iter()
            .map(|ty| substitute(ty, caller))
            .collect::<Result<Vec<_>, _>>()?;
        let nodes = arguments
            .iter()
            .map(|argument| {
                let mut current = argument;
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
        fn portable(ty: &TypeSig, depth: usize) -> bool {
            if depth > 64 {
                return false;
            }
            match ty {
                TypeSig::Primitive(_) => true,
                TypeSig::SzArray(ty) => portable(ty, depth + 1),
                _ => false,
            }
        }
        for argument in &arguments {
            if !portable(argument, 0) {
                return Err(
                    "Named or compound generic arguments require assembly-aware substitution"
                        .into(),
                );
            }
            crate::lower::bound_type(self, target.argument_assembly, argument)?;
        }
        let instance = Instance {
            method: target.definition,
            arguments,
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
            assembly: target.definition.assembly,
            token: 0xfb000001 + index as u32,
        })
    }
}
