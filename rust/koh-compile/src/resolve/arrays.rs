use super::{Resolver, coded, row};
use clrmeta::{ElementType, TableId, TypeSig};

#[derive(Clone, Copy)]
pub(crate) enum ArrayMethod {
    Construct,
    Get,
    Set,
    Address,
}

#[derive(Clone, Copy)]
pub(crate) enum ArrayQuery {
    Length,
    Dimension,
    Rank,
    LowerBound,
    UpperBound,
    LongLength,
    LongDimension,
}

impl Resolver<'_> {
    pub(crate) fn array_method(
        &self,
        assembly: usize,
        token: u32,
    ) -> Result<Option<(TypeSig, ArrayMethod)>, String> {
        if token >> 24 != 0x0a {
            return Ok(None);
        }
        let metadata = self.assembly(assembly)?.metadata();
        let member = row(&metadata.member_refs, token, 0x0a)?;
        if member.class.table != Some(TableId::TypeSpec) {
            return Ok(None);
        }
        let shape = self.type_signature(assembly, coded(member.class)?)?;
        let TypeSig::Array {
            element_type, rank, ..
        } = &shape
        else {
            return Ok(None);
        };
        if *rank != 2 {
            return Err(format!(
                "Rectangular array rank {rank} is unsupported; expected rank2"
            ));
        }
        let name = metadata
            .strings
            .get(member.name)
            .map_err(|e| e.to_string())?;
        let kind = match name {
            ".ctor" => ArrayMethod::Construct,
            "Get" => ArrayMethod::Get,
            "Set" => ArrayMethod::Set,
            "Address" => ArrayMethod::Address,
            _ => return Err(format!("Unsupported rectangular array member {name}")),
        };
        let actual = crate::signature::method(
            metadata
                .blobs
                .get(member.signature)
                .map_err(|e| e.to_string())?,
        )?;
        let mut expected = actual.clone();
        expected.calling_convention = clrmeta::CallingConvention(0x20);
        expected.generic_param_count = 0;
        expected.sentinel = None;
        expected.params = vec![TypeSig::Primitive(ElementType::I4); 2];
        expected.return_type = match kind {
            ArrayMethod::Construct | ArrayMethod::Set => TypeSig::Primitive(ElementType::Void),
            ArrayMethod::Get => (**element_type).clone(),
            ArrayMethod::Address => TypeSig::ByRef(element_type.clone()),
        };
        if matches!(kind, ArrayMethod::Set) {
            expected.params.push((**element_type).clone());
        }
        if !self.methods_equal(assembly, &actual, assembly, &expected, 0)? {
            return Err(format!("Invalid rectangular array {name} member signature"));
        }
        Ok(Some((shape, kind)))
    }

    pub(crate) fn array_query(
        &self,
        assembly: usize,
        token: u32,
    ) -> Result<Option<ArrayQuery>, String> {
        if token >> 24 != 0x0a {
            return Ok(None);
        }
        let metadata = self.assembly(assembly)?.metadata();
        let member = row(&metadata.member_refs, token, 0x0a)?;
        if member.class.table == Some(TableId::TypeSpec) {
            return Ok(None);
        }
        if self.framework_type(assembly, coded(member.class)?)? != Some(("System", "Array")) {
            return Ok(None);
        }
        let name = metadata
            .strings
            .get(member.name)
            .map_err(|e| e.to_string())?;
        let kind = match name {
            "get_Length" => ArrayQuery::Length,
            "get_LongLength" => ArrayQuery::LongLength,
            "GetLength" => ArrayQuery::Dimension,
            "GetLongLength" => ArrayQuery::LongDimension,
            "get_Rank" => ArrayQuery::Rank,
            "GetLowerBound" => ArrayQuery::LowerBound,
            "GetUpperBound" => ArrayQuery::UpperBound,
            _ => return Err(format!("Unsupported System.Array member {name}")),
        };
        let signature = crate::signature::method(
            metadata
                .blobs
                .get(member.signature)
                .map_err(|e| e.to_string())?,
        )?;
        let parameters = if matches!(
            kind,
            ArrayQuery::Dimension
                | ArrayQuery::LongDimension
                | ArrayQuery::LowerBound
                | ArrayQuery::UpperBound
        ) {
            vec![TypeSig::Primitive(ElementType::I4)]
        } else {
            vec![]
        };
        let returns = if matches!(kind, ArrayQuery::LongLength | ArrayQuery::LongDimension) {
            ElementType::I8
        } else {
            ElementType::I4
        };
        if signature.calling_convention.0 != 0x20
            || signature.generic_param_count != 0
            || signature.sentinel.is_some()
            || signature.params != parameters
            || signature.return_type != TypeSig::Primitive(returns)
        {
            return Err(format!("Invalid System.Array {name} signature"));
        }
        Ok(Some(kind))
    }
}
