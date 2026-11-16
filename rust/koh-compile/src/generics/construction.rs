use super::{Argument, canonical, charge, constraints, scoped};
use crate::{
    ir::Type,
    resolve::{Definition, Resolver, coded, framework_type},
};
use clrmeta::{ElementType, TableId, TypeSig};

pub(crate) struct Construction {
    pub ty: Type,
    pub constructor: Option<Definition>,
}

impl Resolver<'_> {
    pub(crate) fn construction(
        &self,
        assembly: usize,
        token: u32,
        arguments: &[Argument],
    ) -> Result<Option<Construction>, String> {
        if token >> 24 != 0x2b {
            return Ok(None);
        }
        let image = self.assembly(assembly)?;
        let metadata = image.metadata();
        let specification = metadata
            .method_specs
            .get(
                (token & 0xffffff)
                    .checked_sub(1)
                    .ok_or("Invalid construction MethodSpec")? as usize,
            )
            .ok_or("Invalid construction MethodSpec")?;
        if specification.method.table != Some(TableId::MemberRef) {
            return Ok(None);
        }
        let reference = metadata
            .member_refs
            .get(
                specification
                    .method
                    .row
                    .checked_sub(1)
                    .ok_or("Invalid construction MemberRef")? as usize,
            )
            .ok_or("Invalid construction MemberRef")?;
        if framework_type(metadata, coded(reference.class)?)? != Some(("System", "Activator"))
            || metadata
                .strings
                .get(reference.name)
                .map_err(|e| e.to_string())?
                != "CreateInstance"
        {
            return Ok(None);
        }
        let signature_bytes = metadata
            .blobs
            .get(reference.signature)
            .map_err(|e| e.to_string())?;
        let argument_bytes = metadata
            .blobs
            .get(specification.instantiation)
            .map_err(|e| e.to_string())?;
        charge(
            &self.generic_work,
            signature_bytes.len() + argument_bytes.len() + 1,
        )?;
        let signature = crate::signature::method(signature_bytes)?;
        let types = crate::signature::method_spec(argument_bytes)?;
        if signature.calling_convention.0 != 0x10
            || signature.generic_param_count != 1
            || !signature.params.is_empty()
            || signature.return_type != TypeSig::MVar(0)
            || signature.sentinel.is_some()
            || types.len() != 1
        {
            return Err(
                "Activator.CreateInstance requires exact static T CreateInstance<T>() signature"
                    .into(),
            );
        }
        let argument = canonical(self, scoped(assembly, &types[0], arguments)?, 0)?;
        if !constraints::constructor(self, &argument)? {
            return Err("Generic construction requires a concrete type with a public parameterless constructor".into());
        }
        let storage = crate::lower::storage_type(self, argument.assembly, &argument.ty)?;
        let ty = crate::lower::bound_type(self, argument.assembly, &storage)?;
        let constructor = constraints::parameterless(self, &argument)?;
        if constructor.is_none()
            && !matches!(ty, Type::I32 | Type::I64 | Type::Native | Type::Struct(_))
            && argument.ty != TypeSig::Primitive(ElementType::Object)
        {
            return Err(
                "Generic reference construction has no public parameterless constructor".into(),
            );
        }
        Ok(Some(Construction { ty, constructor }))
    }
}
