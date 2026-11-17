use crate::{
    generics::{Argument, charge},
    resolve::{Definition, Resolver, type_token},
};
use clrmeta::TypeSig;

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Instance {
    pub definition: Definition,
    pub arguments: Vec<Argument>,
    pub value: bool,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Field {
    pub definition: Definition,
    pub owner: Definition,
}

fn copy_argument(argument: &Argument, depth: usize, nodes: &mut usize) -> Result<Argument, String> {
    fn check(ty: &TypeSig, depth: usize, nodes: &mut usize) -> Result<(), String> {
        let extra = if let TypeSig::Array {
            sizes, lo_bounds, ..
        } = ty
        {
            sizes
                .len()
                .checked_add(lo_bounds.len())
                .ok_or("Closed signature size overflow")?
        } else {
            0
        };
        let total = nodes
            .checked_add(extra)
            .and_then(|value| value.checked_add(1))
            .filter(|&value| value <= 65536 && depth <= 64)
            .ok_or("Closed signature exceeds64 levels or65536 nodes")?;
        *nodes = total;
        match ty {
            TypeSig::SzArray(inner)
            | TypeSig::ByRef(inner)
            | TypeSig::Ptr(inner)
            | TypeSig::Pinned(inner)
            | TypeSig::Modified { inner, .. }
            | TypeSig::Array {
                element_type: inner,
                ..
            } => check(inner, depth + 1, nodes)?,
            TypeSig::GenericInst { type_args, .. } => {
                for argument in type_args {
                    check(argument, depth + 1, nodes)?;
                }
            }
            TypeSig::FnPtr(signature) => {
                for ty in std::iter::once(&signature.return_type).chain(&signature.params) {
                    check(ty, depth + 1, nodes)?;
                }
            }
            _ => {}
        }
        Ok(())
    }
    check(&argument.ty, depth, nodes)?;
    Ok(argument.clone())
}

impl Resolver<'_> {
    pub(crate) fn type_instance(&self, id: Definition) -> Result<Option<Instance>, String> {
        if id.token >> 24 != 0xf9 {
            return Ok(None);
        }
        let index = (id.token & 0xffffff)
            .checked_sub(1)
            .ok_or("Invalid closed type identity")?;
        self.type_instances
            .borrow()
            .get(index as usize)
            .filter(|value| value.definition.assembly == id.assembly)
            .cloned()
            .map(Some)
            .ok_or_else(|| "Unknown closed type identity".into())
    }

    pub(crate) fn type_definition(&self, id: Definition) -> Result<Definition, String> {
        Ok(self
            .type_instance(id)?
            .map_or(id, |instance| instance.definition))
    }

    pub(crate) fn signature_type(
        &self,
        assembly: usize,
        encoded: u32,
    ) -> Result<Definition, String> {
        if encoded & 3 == 3 {
            let id = Definition {
                assembly,
                token: 0xf9000000 | (encoded >> 2),
            };
            self.type_instance(id)?
                .ok_or("Missing closed type binding")?;
            Ok(id)
        } else {
            self.resolve_type(assembly, type_token(encoded)?)
        }
    }

    pub(crate) fn type_argument(&self, id: Definition) -> Result<Argument, String> {
        let raw = self.type_definition(id)?;
        let metadata = self.assembly(raw.assembly)?.metadata();
        let row = &metadata.type_defs[(raw.token & 0xffffff) as usize - 1];
        let base = match row.extends.table {
            Some(clrmeta::TableId::TypeRef) => {
                crate::resolve::framework_type(metadata, crate::resolve::coded(row.extends)?)?
            }
            Some(clrmeta::TableId::TypeDef) => self.framework_definition(Definition {
                assembly: raw.assembly,
                token: 0x02000000 | row.extends.row,
            })?,
            _ => None,
        };
        let value = matches!(base, Some(("System", "ValueType" | "Enum")));
        let encoded = ((id.token & 0xffffff) << 2) | if id.token >> 24 == 0xf9 { 3 } else { 0 };
        Ok(Argument {
            assembly: id.assembly,
            ty: if value {
                TypeSig::ValueType(encoded)
            } else {
                TypeSig::Class(encoded)
            },
        })
    }

    pub(crate) fn int128_scalar(
        &self,
        assembly: usize,
        ty: &TypeSig,
    ) -> Result<Option<crate::ir::Scalar>, String> {
        let TypeSig::ValueType(encoded) = ty else {
            return Ok(None);
        };
        let definition = self.signature_type(assembly, *encoded)?;
        Ok(match self.framework_definition(definition)? {
            Some(("System", "Int128")) => Some(crate::ir::Scalar::I128),
            Some(("System", "UInt128")) => Some(crate::ir::Scalar::U128),
            _ => None,
        })
    }

    pub(crate) fn close_type(
        &self,
        definition: Definition,
        arguments: Vec<Argument>,
        value: bool,
    ) -> Result<Definition, String> {
        self.check_arity(definition, arguments.len())?;
        if arguments.is_empty() {
            return Err("Constructed type requires type arguments".into());
        }
        let metadata = self.assembly(definition.assembly)?.metadata();
        if metadata.type_defs[(definition.token & 0xffffff) as usize - 1].flags & 0x18 == 0x10 {
            return Err("Generic types cannot use explicit layout (ECMA-335 II.10.1.2)".into());
        }
        if arguments.iter().any(|argument| {
            matches!(
                argument.ty,
                TypeSig::ByRef(_)
                    | TypeSig::Ptr(_)
                    | TypeSig::Pinned(_)
                    | TypeSig::FnPtr(_)
                    | TypeSig::Primitive(
                        clrmeta::ElementType::Void | clrmeta::ElementType::TypedByRef
                    )
            )
        }) {
            return Err(
                "Generic type arguments cannot be pointers, byrefs, typed references or void"
                    .into(),
            );
        }
        let raw = self.type_argument(definition)?;
        if matches!(raw.ty, TypeSig::ValueType(_)) != value {
            return Err("Constructed type class/value kind differs from its definition".into());
        }
        charge(
            &self.generic_work,
            self.type_instances.borrow().len() + arguments.len() + 1,
        )?;
        let instance = Instance {
            definition,
            arguments,
            value,
        };
        let found = self
            .type_instances
            .borrow()
            .iter()
            .position(|entry| entry == &instance);
        let index = if let Some(index) = found {
            index
        } else {
            let depth = self.type_binding_depth.get();
            if depth >= 64 {
                return Err("Closed type constraint binding exceeds64 levels".into());
            }
            self.type_binding_depth.set(depth + 1);
            let result =
                crate::generics::constraints::validate(self, definition, &instance.arguments, &[]);
            self.type_binding_depth.set(depth);
            result?;
            let mut instances = self.type_instances.borrow_mut();
            if instances.len() >= 4096 {
                return Err("Closed type count exceeds4096".into());
            }
            charge(&self.generic_nodes, instance.arguments.len() + 1)?;
            instances.push(instance);
            instances.len() - 1
        };
        Ok(Definition {
            assembly: definition.assembly,
            token: 0xf9000001 + index as u32,
        })
    }

    pub(crate) fn field_instance(&self, id: Definition) -> Result<Option<Field>, String> {
        if id.token >> 24 != 0xf8 {
            return Ok(None);
        }
        let index = (id.token & 0xffffff)
            .checked_sub(1)
            .ok_or("Invalid closed field identity")?;
        self.field_instances
            .borrow()
            .get(index as usize)
            .filter(|value| value.definition.assembly == id.assembly)
            .cloned()
            .map(Some)
            .ok_or_else(|| "Unknown closed field identity".into())
    }

    pub(crate) fn close_field(
        &self,
        definition: Definition,
        owner: Definition,
    ) -> Result<Definition, String> {
        if self.type_instance(owner)?.is_none() {
            return Ok(definition);
        }
        let field = Field { definition, owner };
        let mut entries = self.field_instances.borrow_mut();
        charge(&self.generic_work, entries.len() + 1)?;
        let index = if let Some(index) = entries.iter().position(|entry| entry == &field) {
            index
        } else {
            if entries.len() >= 65536 {
                return Err("Closed field count exceeds65536".into());
            }
            entries.push(field);
            entries.len() - 1
        };
        Ok(Definition {
            assembly: definition.assembly,
            token: 0xf8000001 + index as u32,
        })
    }

    pub(crate) fn bind_signature(
        &self,
        assembly: usize,
        ty: &TypeSig,
        methods: &[Argument],
        types: &[Argument],
    ) -> Result<Argument, String> {
        fn bind(
            resolver: &Resolver<'_>,
            assembly: usize,
            ty: &TypeSig,
            methods: &[Argument],
            types: &[Argument],
            depth: usize,
            nodes: &mut usize,
        ) -> Result<Argument, String> {
            *nodes += 1;
            if depth > 64 || *nodes > 65536 {
                return Err("Closed signature exceeds64 levels or65536 nodes".into());
            }
            let mut nested = |ty| bind(resolver, assembly, ty, methods, types, depth + 1, nodes);
            match ty {
                TypeSig::Var(index) | TypeSig::MVar(index) => {
                    let values = if matches!(ty, TypeSig::Var(_)) {
                        types
                    } else {
                        methods
                    };
                    let value = values
                        .get(*index as usize)
                        .ok_or_else(|| format!("Unbound generic parameter {ty:?}"))?;
                    copy_argument(value, depth + 1, nodes)
                }
                TypeSig::GenericInst {
                    is_value_type,
                    type_ref,
                    type_args,
                } => {
                    if resolver.delegate_signature_type(assembly, ty)?.is_some() {
                        return crate::generics::scoped(assembly, ty, methods);
                    }
                    let definition = resolver.resolve_type(assembly, type_token(*type_ref)?)?;
                    let arguments = type_args
                        .iter()
                        .map(&mut nested)
                        .collect::<Result<Vec<_>, _>>()?;
                    let id = resolver.close_type(definition, arguments, *is_value_type)?;
                    resolver.type_argument(id)
                }
                TypeSig::Class(encoded) | TypeSig::ValueType(encoded) => {
                    let id = resolver.signature_type(assembly, *encoded)?;
                    if resolver.type_instance(id)?.is_none() {
                        resolver.check_arity(id, 0)?;
                    }
                    let actual = resolver.type_argument(id)?;
                    if matches!(actual.ty, TypeSig::ValueType(_))
                        != matches!(ty, TypeSig::ValueType(_))
                    {
                        return Err(
                            "Named signature class/value kind differs from its definition".into(),
                        );
                    }
                    Ok(actual)
                }
                TypeSig::SzArray(inner)
                | TypeSig::ByRef(inner)
                | TypeSig::Ptr(inner)
                | TypeSig::Pinned(inner) => {
                    let value = nested(inner)?;
                    let inner = Box::new(value.ty);
                    Ok(Argument {
                        assembly: value.assembly,
                        ty: match ty {
                            TypeSig::SzArray(_) => TypeSig::SzArray(inner),
                            TypeSig::ByRef(_) => TypeSig::ByRef(inner),
                            TypeSig::Ptr(_) => TypeSig::Ptr(inner),
                            _ => TypeSig::Pinned(inner),
                        },
                    })
                }
                TypeSig::Array {
                    element_type, rank, ..
                } => {
                    crate::lower::rectangle_element(ty)?;
                    let element = nested(element_type)?;
                    Ok(Argument {
                        assembly: element.assembly,
                        ty: TypeSig::Array {
                            element_type: Box::new(element.ty),
                            rank: *rank,
                            sizes: Vec::new(),
                            lo_bounds: Vec::new(),
                        },
                    })
                }
                TypeSig::Primitive(_) => Ok(Argument {
                    assembly: 0,
                    ty: ty.clone(),
                }),
                _ if types.is_empty() => crate::generics::scoped(assembly, ty, methods),
                _ => Err(
                    "Constructed signature modifier/function-pointer binding is unfinished".into(),
                ),
            }
        }
        let mut nodes = 0;
        let result = bind(self, assembly, ty, methods, types, 0, &mut nodes)?;
        charge(&self.generic_work, nodes)?;
        Ok(result)
    }

    pub(crate) fn bind_field(
        &self,
        field: &crate::resolve::ResolvedField,
        methods: &[Argument],
        types: &[Argument],
        current: Option<Definition>,
    ) -> Result<(Definition, Definition, Argument), String> {
        let raw_owner = crate::lower::owner_type(self, field.definition)?;
        let owner = if !field.type_arguments.is_empty() {
            let arguments = field
                .type_arguments
                .iter()
                .map(|ty| self.bind_signature(field.argument_assembly, ty, methods, types))
                .collect::<Result<Vec<_>, _>>()?;
            let value = matches!(self.type_argument(raw_owner)?.ty, TypeSig::ValueType(_));
            self.close_type(raw_owner, arguments, value)?
        } else {
            match current {
                Some(current) if self.type_definition(current)? == raw_owner => current,
                _ => {
                    self.check_arity(raw_owner, 0)?;
                    raw_owner
                }
            }
        };
        let arguments = self
            .type_instance(owner)?
            .map_or_else(Vec::new, |instance| instance.arguments);
        let signature = self
            .assembly(field.definition.assembly)?
            .field_signature(field.definition.token)?;
        let signature =
            self.bind_signature(field.definition.assembly, &signature, &[], &arguments)?;
        Ok((self.close_field(field.definition, owner)?, owner, signature))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use rstest::rstest;

    #[rstest]
    #[case(vec![])]
    #[case(vec![0])]
    #[case(vec![0, 0])]
    fn rectangular_arguments_have_one_nominal_identity(#[case] lo_bounds: Vec<i32>) {
        let resolver = Resolver::new(&[]).unwrap();
        let input = TypeSig::Array {
            element_type: Box::new(TypeSig::Primitive(clrmeta::ElementType::I4)),
            rank: 2,
            sizes: vec![],
            lo_bounds,
        };
        let expected = TypeSig::Array {
            element_type: Box::new(TypeSig::Primitive(clrmeta::ElementType::I4)),
            rank: 2,
            sizes: vec![],
            lo_bounds: vec![],
        };
        assert_eq!(
            resolver.bind_signature(0, &input, &[], &[]).unwrap().ty,
            expected
        );
    }

    #[rstest]
    #[case(0, 1, 0, true)]
    #[case(63, 1, 0, true)]
    #[case(64, 1, 0, false)]
    #[case(32, 33, 0, false)]
    #[case(0, 1, 65535, true)]
    #[case(0, 1, 65536, false)]
    fn substituted_arguments_share_composed_depth_and_node_limits(
        #[case] wrappers: usize,
        #[case] depth: usize,
        #[case] before: usize,
        #[case] accepted: bool,
    ) {
        let mut ty = TypeSig::Primitive(clrmeta::ElementType::I4);
        for _ in 0..wrappers {
            ty = TypeSig::SzArray(Box::new(ty));
        }
        let argument = Argument { assembly: 7, ty };
        let mut nodes = before;
        let result = copy_argument(&argument, depth, &mut nodes);
        assert_eq!(result.is_ok(), accepted);
        if accepted {
            assert_eq!(result.unwrap(), argument);
        }
    }

    #[rstest]
    #[case(65534, true)]
    #[case(65535, false)]
    fn array_shape_payload_counts_toward_copy_budget(#[case] count: usize, #[case] accepted: bool) {
        let argument = Argument {
            assembly: 0,
            ty: TypeSig::Array {
                element_type: Box::new(TypeSig::Primitive(clrmeta::ElementType::I4)),
                rank: 2,
                sizes: vec![0; count],
                lo_bounds: vec![],
            },
        };
        assert_eq!(copy_argument(&argument, 0, &mut 0).is_ok(), accepted);
    }
}
