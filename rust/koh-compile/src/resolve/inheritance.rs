use super::{Definition, ResolvedMethod, Resolver, coded, framework_type, row};
use crate::{
    generics::{Argument, charge},
    signature,
};
use clrmeta::{MethodSig, TableId, TypeSig};

impl Resolver<'_> {
    pub(crate) fn base_type(&self, owner: Definition) -> Result<Option<Definition>, String> {
        let raw = self.type_definition(owner)?;
        let metadata = self.assembly(raw.assembly)?.metadata();
        let ty = row(&metadata.type_defs, raw.token, 2)?;
        if ty.extends.row == 0
            || (ty.extends.table == Some(TableId::TypeRef)
                && framework_type(metadata, coded(ty.extends)?)? == Some(("System", "Object")))
        {
            return Ok(None);
        }
        if ty.extends.table != Some(TableId::TypeSpec) {
            return self
                .resolve_type(raw.assembly, coded(ty.extends)?)
                .map(Some);
        }
        let signature = self.type_signature(raw.assembly, coded(ty.extends)?)?;
        let arguments = self
            .type_instance(owner)?
            .map_or_else(Vec::new, |value| value.arguments);
        let bound = self.bind_signature(raw.assembly, &signature, &[], &arguments)?;
        let TypeSig::Class(encoded) = bound.ty else {
            return Err("Class base must name a reference class".into());
        };
        self.signature_type(bound.assembly, encoded).map(Some)
    }

    fn member_parent(
        &self,
        assembly: usize,
        token: u32,
        methods: &[Argument],
        types: &[Argument],
    ) -> Result<Definition, String> {
        let metadata = self.assembly(assembly)?.metadata();
        let reference = row(&metadata.member_refs, token, 0x0a)?;
        let signature = self.type_signature(assembly, coded(reference.class)?)?;
        let bound = self.bind_signature(assembly, &signature, methods, types)?;
        let TypeSig::Class(encoded) = bound.ty else {
            return Err("Inherited member requires a reference class parent".into());
        };
        self.signature_type(bound.assembly, encoded)
    }

    fn member_arguments(&self, owner: Definition) -> Result<Vec<Argument>, String> {
        Ok(self
            .type_instance(owner)?
            .map_or_else(Vec::new, |value| value.arguments))
    }

    pub(crate) fn member_signatures_equal(
        &self,
        assembly: usize,
        left: &MethodSig,
        parent: Definition,
        right: &MethodSig,
        owner: Definition,
    ) -> Result<bool, String> {
        if left.calling_convention != right.calling_convention
            || left.generic_param_count != right.generic_param_count
            || left.sentinel != right.sentinel
            || left.params.len() != right.params.len()
        {
            return Ok(false);
        }
        let arity = left.generic_param_count;
        if arity > 1024 || left.params.len() > 1024 {
            return Err("Inherited method arity or parameters exceed1024".into());
        }
        let left_types = self.member_arguments(parent)?;
        let right_types = self.member_arguments(owner)?;
        charge(
            &self.generic_work,
            arity as usize + left.params.len() + left_types.len() + right_types.len() + 1,
        )?;
        let methods = (0..arity)
            .map(|index| Argument {
                assembly: 0,
                ty: TypeSig::MVar(index),
            })
            .collect::<Vec<_>>();
        for (left, right) in std::iter::once((&left.return_type, &right.return_type))
            .chain(left.params.iter().zip(&right.params))
        {
            let left = self.bind_signature(assembly, left, &methods, &left_types)?;
            let right = self.bind_signature(owner.assembly, right, &methods, &right_types)?;
            if !self.types_equal(left.assembly, &left.ty, right.assembly, &right.ty, 0)? {
                return Ok(false);
            }
        }
        Ok(true)
    }

    pub(super) fn inherited_method(
        &self,
        assembly: usize,
        token: u32,
        methods: &[Argument],
        types: &[Argument],
    ) -> Result<ResolvedMethod, String> {
        let metadata = self.assembly(assembly)?.metadata();
        let reference = row(&metadata.member_refs, token, 0x0a)?;
        let name = metadata
            .strings
            .get(reference.name)
            .map_err(|e| e.to_string())?;
        let signature = signature::method(
            metadata
                .blobs
                .get(reference.signature)
                .map_err(|e| e.to_string())?,
        )?;
        let parent = self.member_parent(assembly, token, methods, types)?;
        let mut owner = Some(parent);
        let mut seen = std::collections::HashSet::new();
        while let Some(current) = owner {
            if seen.len() >= 64 || !seen.insert(current) {
                return Err("Inherited member cycle/depth exceeds64".into());
            }
            let raw = self.type_definition(current)?;
            let image = self.assembly(raw.assembly)?;
            let mut found = None;
            for &candidate in self
                .index(raw.assembly)?
                .methods
                .get(&(raw.token & 0xffffff))
                .and_then(|names| names.get(name))
                .map(Vec::as_slice)
                .unwrap_or(&[])
            {
                let row = row(&image.metadata().method_defs, candidate, 6)?;
                charge(
                    &self.generic_work,
                    image
                        .metadata()
                        .blobs
                        .get(row.signature)
                        .map_err(|e| e.to_string())?
                        .len()
                        + 1,
                )?;
                let other = image.method_signature(candidate)?;
                if self.member_signatures_equal(assembly, &signature, parent, &other, current)? {
                    if found.replace(candidate).is_some() {
                        return Err(format!("Ambiguous method {name}"));
                    }
                }
            }
            if let Some(token) = found {
                let definition = Definition {
                    assembly: raw.assembly,
                    token,
                };
                self.check_arity(definition, signature.generic_param_count as usize)?;
                return Ok(ResolvedMethod {
                    definition,
                    type_arguments: vec![],
                    method_arguments: vec![],
                    argument_assembly: assembly,
                    declaring: self.type_instance(current)?.map(|_| current),
                });
            }
            if matches!(name, ".ctor" | ".cctor") {
                break;
            }
            owner = self.base_type(current)?;
        }
        Err(format!(
            "Unresolved inherited method {name} at token 0x{token:08x}"
        ))
    }
}
